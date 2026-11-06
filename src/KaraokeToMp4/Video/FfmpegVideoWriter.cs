using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace KaraokeToMp4.Video;

/// <summary>
/// Streams raw BGRA frames into an ffmpeg process which muxes them with an audio file into an MP4.
/// </summary>
internal sealed class FfmpegVideoWriter : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly int _frameBytes;
    private readonly Queue<string> _stderrTail = new();
    private readonly object _stderrLock = new();
    private bool _completed;

    private FfmpegVideoWriter(Process process, int frameBytes)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _frameBytes = frameBytes;
    }

    /// <summary>
    /// Starts ffmpeg.
    /// </summary>
    /// <param name="frameWidth">Width of the frames that will be written.</param>
    /// <param name="frameHeight">Height of the frames that will be written.</param>
    /// <param name="videoFilter">Optional -vf filter chain (e.g. scaling CDG up to the output size).</param>
    public static FfmpegVideoWriter Start(
        ConversionOptions options,
        FfmpegTools tools,
        int frameWidth,
        int frameHeight,
        string audioPath,
        string outputPath,
        string? videoFilter,
        SongInfo? songInfo = null)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats",
            options.Overwrite ? "-y" : "-n",
            // Video: raw frames on stdin.
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-s", $"{frameWidth}x{frameHeight}",
            "-r", options.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            // Audio.
            "-i", audioPath,
            "-map", "0:v:0",
            "-map", "1:a:0",
        };

        if (!string.IsNullOrEmpty(videoFilter))
        {
            args.Add("-vf");
            args.Add(videoFilter);
        }

        args.AddRange(new[]
        {
            "-c:v", options.VideoCodec,
            "-preset", options.VideoPreset,
            "-crf", options.Crf.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-c:a", options.AudioCodec,
            "-b:a", $"{options.AudioBitrateKbps}k",
            "-movflags", "+faststart",
            // Don't carry over tags from the inputs; only the ones set below.
            "-map_metadata", "-1",
            "-shortest",
        });

        foreach (var (key, value) in MetadataFor(options, songInfo))
        {
            args.Add("-metadata");
            args.Add($"{key}={value}");
        }

        if (!string.IsNullOrWhiteSpace(options.ExtraFfmpegOutputArgs))
            args.AddRange(SplitArgs(options.ExtraFfmpegOutputArgs));

        args.Add(outputPath);

        var psi = new ProcessStartInfo(tools.FfmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new KaraokeConversionException("ffmpeg failed to start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new KaraokeConversionException(
                $"Could not start ffmpeg ('{tools.FfmpegPath}'). Install ffmpeg or set ConversionOptions.FfmpegPath.", ex);
        }

        var writer = new FfmpegVideoWriter(process, frameWidth * frameHeight * 4);
        process.ErrorDataReceived += (_, e) => writer.OnStderr(e.Data);
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return writer;
    }

    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> bgra, CancellationToken ct)
    {
        if (bgra.Length != _frameBytes)
            throw new ArgumentException($"Frame must be {_frameBytes} bytes, got {bgra.Length}.");
        try
        {
            await _stdin.WriteAsync(bgra, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            await WaitForExitQuietly().ConfigureAwait(false);
            throw new KaraokeConversionException("ffmpeg stopped accepting frames: " + StderrTail(), ex);
        }
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        _completed = true;
        try
        {
            await _stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) { /* ffmpeg may already have exited; exit code below tells us */ }
        _stdin.Close();

        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (_process.ExitCode != 0)
            throw new KaraokeConversionException($"ffmpeg exited with code {_process.ExitCode}: {StderrTail()}");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            if (!_completed)
            {
                try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
            await WaitForExitQuietly().ConfigureAwait(false);
        }
        _process.Dispose();
    }

    private async Task WaitForExitQuietly()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch { /* best effort */ }
    }

    private void OnStderr(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_stderrLock)
        {
            _stderrTail.Enqueue(line);
            while (_stderrTail.Count > 30) _stderrTail.Dequeue();
        }
    }

    private string StderrTail()
    {
        lock (_stderrLock)
            return _stderrTail.Count == 0 ? "(no output)" : string.Join(Environment.NewLine, _stderrTail);
    }

    /// <summary>MP4 tags written for a song (title/artist/genre).</summary>
    internal static IEnumerable<(string Key, string Value)> MetadataFor(ConversionOptions options, SongInfo? info)
    {
        if (!options.WriteMetadata) yield break;
        if (info?.HasTitle == true) yield return ("title", OneLine(info.Title!));
        if (info?.HasArtist == true) yield return ("artist", OneLine(info.Artist!));
        if (!string.IsNullOrWhiteSpace(options.MetadataGenre)) yield return ("genre", OneLine(options.MetadataGenre));
    }

    private static string OneLine(string s) => string.Join(' ', s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    internal static IEnumerable<string> SplitArgs(string commandLine)
    {
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
