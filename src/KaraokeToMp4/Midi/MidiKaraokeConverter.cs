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
        CancellationToken cancellationToken = default,
        SongInfo? songInfo = null)
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

        songInfo ??= Metadata.SongInfoReader.FromMidi(midi);
        if (!string.IsNullOrWhiteSpace(_options.Title))
        {
            lyrics.TitleLines.Clear();
            lyrics.TitleLines.AddRange(_options.Title.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0));
        }
        else if (songInfo.HasTitle)
        {
            // Title card shows the clean title/artist rather than every @T line.
            lyrics.TitleLines.Clear();
            lyrics.TitleLines.Add(songInfo.Title!);
            if (songInfo.HasArtist) lyrics.TitleLines.Add(songInfo.Artist!);
        }
        else if (lyrics.TitleLines.Count == 0)
        {
            lyrics.TitleLines.Add(Path.GetFileNameWithoutExtension(midiPath));
        }

        // Sort out ffmpeg first so a missing encoder is reported before minutes of synthesis.
        var tools = await MediaProbe.ResolveToolsAsync(_options, cancellationToken).ConfigureAwait(false);

        string? tempWav = null;
        try
        {
            string audioPath;
            TimeSpan duration;
            if (!string.IsNullOrEmpty(_options.MidiAudioOverridePath))
            {
                audioPath = _options.MidiAudioOverridePath;
                if (!File.Exists(audioPath)) throw new FileNotFoundException("Audio override file not found.", audioPath);
                duration = await MediaProbe.TryGetDurationAsync(tools, audioPath, cancellationToken).ConfigureAwait(false)
                           ?? TimeSpan.FromSeconds(midi.DurationSeconds + _options.MidiTailSeconds);
            }
            else
            {
                var soundFont = _options.SoundFontPath;
                if (string.IsNullOrEmpty(soundFont))
                {
                    soundFont = await SoundFontLocator.EnsureAsync(
                        _options.ConfirmSoundFontDownload, _options.SoundFontDownloadProgress, cancellationToken).ConfigureAwait(false);
                }
                if (string.IsNullOrEmpty(soundFont))
                    throw new KaraokeConversionException(
                        "A SoundFont is required to synthesise MIDI audio and none was found. Set ConversionOptions.SoundFontPath, " +
                        "allow a download via ConversionOptions.ConfirmSoundFontDownload (or SoundFontLocator.DownloadAsync), " +
                        "or use ConversionOptions.MidiAudioOverridePath for a pre-rendered backing track.");

                tempWav = Path.Combine(Path.GetTempPath(), $"karaoke-to-mp4-{Guid.NewGuid():N}.wav");
                var midiFile = midiPath;
                duration = await Task.Run(() => MidiAudioRenderer.RenderToWav(
                    midiFile, soundFont, tempWav, _options.MidiSampleRate,
                    _options.MidiTailSeconds, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
                audioPath = tempWav;
            }

            var plan = new LyricScreenPlan(lyrics, _options.LinesPerPage, TimeSpan.FromSeconds(_options.PageLeadInSeconds));
            long totalFrames = (long)Math.Ceiling(duration.TotalSeconds * _options.FrameRate);

            using var renderer = new LyricFrameRenderer(_options, plan, lyrics.TitleLines);
            var frame = new byte[_options.Width * _options.Height * 4];

            await using var writer = FfmpegVideoWriter.Start(_options, tools, _options.Width, _options.Height, audioPath, outputPath, videoFilter: null, songInfo);
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
