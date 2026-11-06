using MeltySynth;

namespace KaraokeToMp4.Midi;

/// <summary>
/// Synthesises a MIDI file to a 32-bit float stereo WAV using a SoundFont (via MeltySynth).
/// </summary>
public static class MidiAudioRenderer
{
    private const int BlockSize = 4096;

    /// <summary>Renders <paramref name="midiPath"/> to <paramref name="wavPath"/>.</summary>
    /// <returns>Duration of the rendered audio.</returns>
    public static TimeSpan RenderToWav(
        string midiPath,
        string soundFontPath,
        string wavPath,
        int sampleRate = 44100,
        double tailSeconds = 2.0,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(soundFontPath))
            throw new FileNotFoundException("SoundFont not found.", soundFontPath);

        MeltySynth.MidiFile midi;
        try
        {
            // Re-encode through our tolerant parser first: many KAR files in the wild are truncated or
            // slightly malformed, which the synthesiser's strict reader rejects outright.
            var clean = StandardMidiFile.FromFile(midiPath).ToBytes();
            using var ms = new MemoryStream(clean);
            midi = new MeltySynth.MidiFile(ms);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new KaraokeConversionException($"Could not read MIDI file '{midiPath}' for synthesis.", ex);
        }

        var synth = new Synthesizer(soundFontPath, sampleRate);
        var sequencer = new MidiFileSequencer(synth);
        sequencer.Play(midi, loop: false);

        var duration = midi.Length + TimeSpan.FromSeconds(Math.Max(0, tailSeconds));
        long totalSamples = (long)Math.Ceiling(duration.TotalSeconds * sampleRate);

        var left = new float[BlockSize];
        var right = new float[BlockSize];
        var interleaved = new byte[BlockSize * 2 * sizeof(float)];

        using var fs = new FileStream(wavPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var bw = new BinaryWriter(fs);
        WriteWavHeader(bw, sampleRate, totalSamples);

        long written = 0;
        long nextReport = 0;
        while (written < totalSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = (int)Math.Min(BlockSize, totalSamples - written);
            sequencer.Render(left.AsSpan(0, n), right.AsSpan(0, n));

            for (int i = 0; i < n; i++)
            {
                BitConverter.TryWriteBytes(interleaved.AsSpan(i * 8, 4), left[i]);
                BitConverter.TryWriteBytes(interleaved.AsSpan(i * 8 + 4, 4), right[i]);
            }
            bw.Write(interleaved, 0, n * 8);
            written += n;

            if (progress != null && written >= nextReport)
            {
                progress.Report(new ConversionProgress(
                    ConversionStage.SynthesizingAudio,
                    TimeSpan.FromSeconds((double)written / sampleRate),
                    duration));
                nextReport += sampleRate * 10L;
            }
        }
        return TimeSpan.FromSeconds((double)totalSamples / sampleRate);
    }

    private static void WriteWavHeader(BinaryWriter bw, int sampleRate, long totalSamples)
    {
        const short channels = 2;
        const short bitsPerSample = 32;
        long dataBytes = totalSamples * channels * (bitsPerSample / 8);
        if (dataBytes > uint.MaxValue - 50)
            throw new KaraokeConversionException("MIDI audio is too long to fit in a WAV file.");

        bw.Write("RIFF"u8);
        bw.Write((uint)(4 + 8 + 16 + 8 + dataBytes));
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)3); // WAVE_FORMAT_IEEE_FLOAT
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8));
        bw.Write((short)(channels * (bitsPerSample / 8)));
        bw.Write(bitsPerSample);
        bw.Write("data"u8);
        bw.Write((uint)dataBytes);
    }
}
