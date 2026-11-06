using System.Diagnostics;
using System.Globalization;

namespace KaraokeToMp4.Video;

/// <summary>Uses ffprobe to read the duration of an audio file.</summary>
internal static class MediaProbe
{
    public static async Task<TimeSpan?> TryGetDurationAsync(ConversionOptions options, string path, CancellationToken ct)
    {
        foreach (var probe in CandidateProbePaths(options))
        {
            try
            {
                var psi = new ProcessStartInfo(probe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path })
                    psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p is null) continue;
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
            catch { /* try next candidate */ }
        }
        return null;
    }

    private static IEnumerable<string> CandidateProbePaths(ConversionOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.FfprobePath))
        {
            yield return options.FfprobePath;
            yield break;
        }

        var dir = Path.GetDirectoryName(options.FfmpegPath);
        if (!string.IsNullOrEmpty(dir))
        {
            var ext = Path.GetExtension(options.FfmpegPath);
            yield return Path.Combine(dir, "ffprobe" + ext);
        }
        yield return "ffprobe";
    }
}
