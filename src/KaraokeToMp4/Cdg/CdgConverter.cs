using System.IO.Compression;
using KaraokeToMp4.Video;

namespace KaraokeToMp4.Cdg;

/// <summary>Converts an MP3 (or other audio) + CDG pair into an MP4 video.</summary>
public sealed class CdgConverter
{
    /// <summary>Audio extensions searched for next to a .cdg file, in priority order.</summary>
    public static readonly string[] AudioExtensions = { ".mp3", ".ogg", ".wav", ".flac", ".m4a", ".aac", ".wma" };

    private readonly ConversionOptions _options;

    public CdgConverter(ConversionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Converts an audio file plus its matching .cdg graphics file into an MP4.
    /// </summary>
    public async Task ConvertAsync(
        string audioPath,
        string cdgPath,
        string outputPath,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        SongInfo? songInfo = null)
    {
        _options.Validate();
        if (!File.Exists(audioPath)) throw new FileNotFoundException("Audio file not found.", audioPath);
        if (!File.Exists(cdgPath)) throw new FileNotFoundException("CDG file not found.", cdgPath);

        progress?.Report(new ConversionProgress(ConversionStage.Preparing, TimeSpan.Zero, TimeSpan.Zero));
        var tools = await MediaProbe.ResolveToolsAsync(_options, cancellationToken).ConfigureAwait(false);
        songInfo ??= await Metadata.SongInfoReader.FromAudioAsync(audioPath, tools, cancellationToken).ConfigureAwait(false);
        if (!_options.IncludeArtist) songInfo = songInfo.WithoutArtist();

        var cdgData = await File.ReadAllBytesAsync(cdgPath, cancellationToken).ConfigureAwait(false);
        if (_options.CdgErrorCorrection)
        {
            var report = CdgInspector.RepairInPlace(cdgData);
            if (!report.IsClean)
                _options.OnWarning?.Invoke($"{Path.GetFileName(cdgPath)}: {report.Summary}");
        }
        var decoder = new CdgDecoder(cdgData);
        if (decoder.PacketCount == 0)
            throw new KaraokeConversionException($"'{cdgPath}' contains no CDG packets.");

        // Run the video for as long as the longer of the two streams; ffmpeg's -shortest
        // then trims to the audio so the song is never cut off early or padded with silence.
        var audioDuration = await MediaProbe.TryGetDurationAsync(tools, audioPath, cancellationToken).ConfigureAwait(false);
        var duration = audioDuration is { } a && a > decoder.Duration ? a : decoder.Duration;
        long totalFrames = (long)Math.Ceiling(duration.TotalSeconds * _options.FrameRate);

        bool crop = _options.CdgCropBorder;
        int frameW = crop ? CdgDecoder.Width - 2 * CdgDecoder.BorderX : CdgDecoder.Width;
        int frameH = crop ? CdgDecoder.Height - 2 * CdgDecoder.BorderY : CdgDecoder.Height;
        var frame = new byte[frameW * frameH * 4];

        string filter =
            $"scale={_options.Width}:{_options.Height}:force_original_aspect_ratio=decrease:flags={_options.CdgScaleAlgorithm}," +
            $"pad={_options.Width}:{_options.Height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1";

        await using var writer = FfmpegVideoWriter.Start(_options, tools, frameW, frameH, audioPath, outputPath, filter, songInfo);
        for (long n = 0; n < totalFrames; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = TimeSpan.FromSeconds((double)(n + 1) / _options.FrameRate);
            decoder.AdvanceTo(t);
            decoder.RenderBgra(frame, crop);
            await writer.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);

            if (progress != null && n % _options.FrameRate == 0)
                progress.Report(new ConversionProgress(ConversionStage.EncodingVideo, t, duration));
        }
        await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new ConversionProgress(ConversionStage.Completed, duration, duration));
    }

    /// <summary>
    /// Converts an archive containing an audio file and a .cdg file (the usual "MP3+G" bundle). Any format
    /// <see cref="Archives.SongArchive"/> reads is accepted (ZIP, 7-Zip, RAR, TAR, tar.gz), whatever the extension.
    /// </summary>
    public async Task ConvertZipAsync(
        string zipPath,
        string outputPath,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        SongInfo? songInfo = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "karaoke-to-mp4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string cdg, audio;
            using (var archive = Archives.SongArchive.Open(zipPath))
            {
                if (archive.IsEncrypted)
                    throw new KaraokeConversionException($"'{Path.GetFileName(zipPath)}' is password-protected.");
                var song = archive.FindSongs().FirstOrDefault(s => s.Cdg != null)
                    ?? throw new KaraokeConversionException($"'{Path.GetFileName(zipPath)}' does not contain a .cdg file.");
                if (song.Audio == null)
                    throw new KaraokeConversionException($"'{Path.GetFileName(zipPath)}' does not contain an audio file for '{song.Name}'.");

                // Extract under fixed names so archive paths can never escape the temp folder.
                cdg = Path.Combine(tempDir, "track.cdg");
                audio = Path.Combine(tempDir, "track" + Path.GetExtension(song.Audio.Name).ToLowerInvariant());
                await File.WriteAllBytesAsync(cdg, song.Cdg!.ReadAllBytes(), cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(audio, song.Audio.ReadAllBytes(), cancellationToken).ConfigureAwait(false);
            }
            await ConvertAsync(audio, cdg, outputPath, progress, cancellationToken, songInfo).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Finds the audio file that accompanies <paramref name="cdgPath"/> (same base name).</summary>
    public static string? FindAudioForCdg(string cdgPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(cdgPath))!;
        var baseName = Path.GetFileNameWithoutExtension(cdgPath);
        foreach (var ext in AudioExtensions)
        {
            var match = FindCaseInsensitive(dir, baseName + ext);
            if (match != null) return match;
        }
        return null;
    }

    /// <summary>Finds the .cdg file that accompanies <paramref name="audioPath"/> (same base name).</summary>
    public static string? FindCdgForAudio(string audioPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(audioPath))!;
        return FindCaseInsensitive(dir, Path.GetFileNameWithoutExtension(audioPath) + ".cdg");
    }

    private static string? FindCaseInsensitive(string dir, string fileName)
    {
        var exact = Path.Combine(dir, fileName);
        if (File.Exists(exact)) return exact;
        return Directory.EnumerateFiles(dir)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
    }
}
