using KaraokeToMp4.Lyrics;
using KaraokeToMp4.Video;

namespace KaraokeToMp4.Midi;

/// <summary>
/// Converts .kar files and MIDI files with lyric events into MP4 karaoke videos: the MIDI is
/// synthesised to audio with a SoundFont and the lyrics are rendered as a highlighted wipe.
/// </summary>
public sealed class MidiKaraokeConverter
{
    private readonly ConversionOptions _options;

    public MidiKaraokeConverter(ConversionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Reads the lyrics from a MIDI/KAR file without converting it.</summary>
    public static KaraokeLyrics ReadLyrics(string midiPath)
    {
        var midi = StandardMidiFile.FromFile(midiPath);
        var preferred = Path.GetExtension(midiPath).Equals(".kar", StringComparison.OrdinalIgnoreCase)
            ? KaraokeFormat.Kar
            : (KaraokeFormat?)null;
        return MidiLyricExtractor.Extract(midi, preferred);
    }

    public async Task ConvertAsync(
        string midiPath,
        string outputPath,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _options.Validate();
        if (!File.Exists(midiPath)) throw new FileNotFoundException("MIDI file not found.", midiPath);
        progress?.Report(new ConversionProgress(ConversionStage.Preparing, TimeSpan.Zero, TimeSpan.Zero));

        KaraokeLyrics lyrics;
        StandardMidiFile midi;
        try
        {
            midi = StandardMidiFile.FromFile(midiPath);
            lyrics = ReadLyrics(midiPath);
        }
        catch (InvalidDataException ex)
        {
            throw new KaraokeConversionException($"'{midiPath}' is not a valid MIDI file.", ex);
        }

        if (lyrics.Lines.Count == 0)
            throw new KaraokeConversionException($"'{midiPath}' does not contain any lyrics.");

        if (!string.IsNullOrWhiteSpace(_options.Title))
        {
            lyrics.TitleLines.Clear();
            lyrics.TitleLines.AddRange(_options.Title.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0));
        }
        else if (lyrics.TitleLines.Count == 0)
        {
            lyrics.TitleLines.Add(Path.GetFileNameWithoutExtension(midiPath));
        }

        string? tempWav = null;
        try
        {
            string audioPath;
            TimeSpan duration;
            if (!string.IsNullOrEmpty(_options.MidiAudioOverridePath))
            {
                audioPath = _options.MidiAudioOverridePath;
                if (!File.Exists(audioPath)) throw new FileNotFoundException("Audio override file not found.", audioPath);
                duration = await MediaProbe.TryGetDurationAsync(_options, audioPath, cancellationToken).ConfigureAwait(false)
                           ?? TimeSpan.FromSeconds(midi.DurationSeconds + _options.MidiTailSeconds);
            }
            else
            {
                if (string.IsNullOrEmpty(_options.SoundFontPath))
                    throw new KaraokeConversionException(
                        "A SoundFont is required to synthesise MIDI audio. Set ConversionOptions.SoundFontPath " +
                        "(or ConversionOptions.MidiAudioOverridePath to use a pre-rendered backing track).");

                tempWav = Path.Combine(Path.GetTempPath(), $"karaoke-to-mp4-{Guid.NewGuid():N}.wav");
                var midiFile = midiPath;
                duration = await Task.Run(() => MidiAudioRenderer.RenderToWav(
                    midiFile, _options.SoundFontPath, tempWav, _options.MidiSampleRate,
                    _options.MidiTailSeconds, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
                audioPath = tempWav;
            }

            var plan = new LyricScreenPlan(lyrics, _options.LinesPerPage, TimeSpan.FromSeconds(_options.PageLeadInSeconds));
            long totalFrames = (long)Math.Ceiling(duration.TotalSeconds * _options.FrameRate);

            using var renderer = new LyricFrameRenderer(_options, plan, lyrics.TitleLines);
            var frame = new byte[_options.Width * _options.Height * 4];

            await using var writer = FfmpegVideoWriter.Start(_options, _options.Width, _options.Height, audioPath, outputPath, videoFilter: null);
            for (long n = 0; n < totalFrames; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var t = TimeSpan.FromSeconds((double)n / _options.FrameRate);
                renderer.Render(t).CopyTo(frame);
                await writer.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);

                if (progress != null && n % _options.FrameRate == 0)
                    progress.Report(new ConversionProgress(ConversionStage.EncodingVideo, t, duration));
            }
            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new ConversionProgress(ConversionStage.Completed, duration, duration));
        }
        finally
        {
            if (tempWav != null)
            {
                try { File.Delete(tempWav); } catch { /* best effort */ }
            }
        }
    }
}
