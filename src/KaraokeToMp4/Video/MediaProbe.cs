using System.Diagnostics;
using System.Globalization;

namespace KaraokeToMp4.Video;

/// <summary>Uses ffprobe to read the duration of an audio file.</summary>
internal static class MediaProbe
{
    public static async Task<TimeSpan?> TryGetDurationAsync(FfmpegTools tools, string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(tools.FfprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var text = (await stdoutTask.ConfigureAwait(false)).Trim();
            if (p.ExitCode == 0 &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) &&
                secs > 0)
                return TimeSpan.FromSeconds(secs);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* ffprobe missing or failed: caller falls back */ }
        return null;
    }

    /// <summary>
    /// Works out which ffmpeg/ffprobe to use: the explicit paths in the options, otherwise
    /// <see cref="FfmpegLocator"/> (asking permission to download if needed).
    /// </summary>
    public static async Task<FfmpegTools> ResolveToolsAsync(ConversionOptions options, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.FfmpegPath))
        {
            var probe = options.FfprobePath;
            if (string.IsNullOrWhiteSpace(probe))
            {
                var dir = Path.GetDirectoryName(options.FfmpegPath);
                var sibling = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "ffprobe" + Path.GetExtension(options.FfmpegPath));
                probe = sibling != null && File.Exists(sibling) ? sibling : "ffprobe";
            }
            return new FfmpegTools(options.FfmpegPath, probe);
        }

        var tools = await FfmpegLocator.EnsureAsync(options.ConfirmFfmpegDownload, options.FfmpegDownloadProgress, ct)
            .ConfigureAwait(false);
        if (tools == null)
            throw new KaraokeConversionException(
                "ffmpeg was not found (looked in the app folder, " + FfmpegLocator.DownloadFolder + " and PATH). " +
                "Install ffmpeg, set ConversionOptions.FfmpegPath, or allow a download via ConversionOptions.ConfirmFfmpegDownload.");
        if (!string.IsNullOrWhiteSpace(options.FfprobePath))
            tools = tools with { FfprobePath = options.FfprobePath };
        return tools;
    }
}
