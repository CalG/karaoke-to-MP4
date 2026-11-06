using System.IO.Compression;
using System.Runtime.InteropServices;
using KaraokeToMp4.Downloads;

namespace KaraokeToMp4.Video;

/// <summary>A downloadable ffmpeg + ffprobe build for one platform.</summary>
/// <param name="Version">ffmpeg version, e.g. "6.1".</param>
/// <param name="Platform">Platform label, e.g. "win-64".</param>
/// <param name="Ffmpeg">Zip containing the ffmpeg executable.</param>
/// <param name="Ffprobe">Zip containing the ffprobe executable.</param>
public sealed record FfmpegRelease(string Version, string Platform, DownloadAsset Ffmpeg, DownloadAsset Ffprobe)
{
    public long TotalBytes => Ffmpeg.SizeBytes + Ffprobe.SizeBytes;
    public int SizeMegabytes => (int)Math.Round(TotalBytes / (1024.0 * 1024.0));
}

/// <summary>What the caller is asked to approve before ffmpeg is downloaded.</summary>
/// <param name="Release">The build that would be downloaded.</param>
/// <param name="Destination">Folder it will be saved to.</param>
public sealed record FfmpegDownloadRequest(FfmpegRelease Release, string Destination);

/// <summary>Full paths to the ffmpeg and ffprobe executables.</summary>
public sealed record FfmpegTools(string FfmpegPath, string FfprobePath);

/// <summary>
/// Finds ffmpeg/ffprobe, or downloads them with the user's permission, so nobody has to install
/// ffmpeg by hand. Uses the same download folder as the karaoke player app, so a copy downloaded
/// by either one is shared.
/// </summary>
public static class FfmpegLocator
{
    private const string BaseUrl = "https://github.com/ffbinaries/ffbinaries-prebuilt/releases/download/v6.1/";

    private static FfmpegRelease Build(string platform, long ffmpegSize, string ffmpegSha, long probeSize, string probeSha) => new(
        "6.1", platform,
        new DownloadAsset(new Uri($"{BaseUrl}ffmpeg-6.1-{platform}.zip"), ffmpegSha, ffmpegSize),
        new DownloadAsset(new Uri($"{BaseUrl}ffprobe-6.1-{platform}.zip"), probeSha, probeSize));

    /// <summary>
    /// Static ffmpeg 6.1 builds from ffbinaries (the source FFMpegCore's downloader uses), pinned
    /// with their SHA-256 checksums. Windows/macOS builds also run on ARM via emulation/Rosetta.
    /// </summary>
    public static IReadOnlyDictionary<string, FfmpegRelease> KnownReleases { get; } = new Dictionary<string, FfmpegRelease>
    {
        ["win-64"] = Build("win-64",
            54_681_842, "b0fb4bcef9d4b5f7a77d2e4854f80d4ce3e43809bc29fd1f97caa1b467f96993",
            54_617_320, "3c0ea2856cf65edff23a2d4e76b1ceabba65fe8b1bed684ee2eeba24aa9427c3"),
        ["linux-64"] = Build("linux-64",
            29_170_850, "8bb4a27f5fd02f3dd9a5e75c9eddf6ace1d50a08929ee0d20bbf17eb467fb711",
            29_099_372, "cb690c360042b51d9e901db2b0185c585330c1067b5c5edf0b6a5e26e0375e2a"),
        ["linux-arm-64"] = Build("linux-arm-64",
            25_285_360, "0e3a60df317df0bbfbe25e8b777864b5407d166e5b5ac692fb1e477568a4c886",
            25_220_724, "c99358a90d93ada454e2f4dd26cd25718d36b4032b0ad5539425eac6e66349d8"),
        ["macos-64"] = Build("macos-64",
            25_810_979, "ffcd56ce5ef50c4d36d675b0ee80674f5a0869f94746460ff5d058a33cbd3128",
            25_764_046, "878ab8787ca6c48a11cb668c01d544be4c1bf655637d719cdf3b3179841545f2"),
    };

    /// <summary>
    /// The build offered for this machine, or null if none is available (e.g. 32-bit systems).
    /// Can be replaced to download from somewhere else.
    /// </summary>
    public static FfmpegRelease? Release { get; set; } = ForCurrentPlatform();

    /// <summary>Where downloads go (per-user, shared with the karaoke player app).</summary>
    public static string DownloadFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaraokeTools", "ffmpeg");

    /// <summary>Also search the folders on PATH. Default true.</summary>
    public static bool SearchPath { get; set; } = true;

    /// <summary>Extra folders to search, checked after the app and download folders.</summary>
    public static IList<string> AdditionalSearchFolders { get; } = new List<string>();

    /// <summary>
    /// Looks for a folder containing both ffmpeg and ffprobe: the app's folder, <see cref="DownloadFolder"/>,
    /// <see cref="AdditionalSearchFolders"/>, then PATH. Returns null if there isn't one.
    /// </summary>
    public static FfmpegTools? TryLocate()
    {
        foreach (var dir in CandidateFolders())
        {
            var tools = InFolder(dir);
            if (tools != null) return tools;
        }
        return null;
    }

    /// <summary>
    /// Returns ffmpeg/ffprobe, downloading them if they aren't found and <paramref name="confirmDownload"/>
    /// approves. Returns null if the user declines or no build exists for this platform.
    /// </summary>
    /// <param name="confirmDownload">Asked before anything is downloaded. When null, nothing is downloaded.</param>
    public static async Task<FfmpegTools?> EnsureAsync(
        Func<FfmpegDownloadRequest, CancellationToken, Task<bool>>? confirmDownload,
        IProgress<double>? downloadProgress = null,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        var existing = TryLocate();
        if (existing != null) return existing;
        if (confirmDownload == null || Release == null) return null;

        if (!await confirmDownload(new FfmpegDownloadRequest(Release, DownloadFolder), cancellationToken).ConfigureAwait(false))
            return null;
        return await DownloadAsync(downloadProgress, cancellationToken, httpClient).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads <see cref="Release"/> into <see cref="DownloadFolder"/>, verifying checksums, and
    /// returns the executable paths.
    /// </summary>
    /// <param name="downloadProgress">Receives 0..1 across both downloads.</param>
    public static async Task<FfmpegTools> DownloadAsync(
        IProgress<double>? downloadProgress = null,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        var release = Release ?? throw new KaraokeConversionException(
            $"No ffmpeg download is available for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}). " +
            "Install ffmpeg and set ConversionOptions.FfmpegPath.");

        Directory.CreateDirectory(DownloadFolder);
        long total = Math.Max(1, release.TotalBytes);
        long done = 0;

        foreach (var (name, asset) in new[] { ("ffmpeg", release.Ffmpeg), ("ffprobe", release.Ffprobe) })
        {
            var zipPath = Path.Combine(DownloadFolder, $"{name}-{release.Version}-{release.Platform}.zip");
            long before = done;
            try
            {
                await VerifiedDownloader.DownloadAsync(asset, zipPath, $"{name} {release.Version}",
                    read => downloadProgress?.Report(Math.Min(1.0, (double)(before + read) / total)),
                    httpClient, cancellationToken).ConfigureAwait(false);
                ExtractExecutable(zipPath, ExeName(name));
            }
            finally
            {
                try { File.Delete(zipPath); } catch { /* best effort */ }
            }
            done += asset.SizeBytes;
        }

        downloadProgress?.Report(1.0);
        return InFolder(DownloadFolder)
               ?? throw new KaraokeConversionException($"ffmpeg was downloaded but could not be found in {DownloadFolder}.");
    }

    /// <summary>Returns the tools if <paramref name="dir"/> contains both ffmpeg and ffprobe.</summary>
    public static FfmpegTools? InFolder(string dir)
    {
        try
        {
            var ffmpeg = Path.Combine(dir, ExeName("ffmpeg"));
            var ffprobe = Path.Combine(dir, ExeName("ffprobe"));
            return File.Exists(ffmpeg) && File.Exists(ffprobe) ? new FfmpegTools(ffmpeg, ffprobe) : null;
        }
        catch (ArgumentException)
        {
            return null; // Malformed PATH entry.
        }
    }

    internal static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static void ExtractExecutable(string zipPath, string exeName)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        // Match on the file name only and write to a fixed path, so archive paths can't escape the folder.
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, exeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KaraokeConversionException($"'{Path.GetFileName(zipPath)}' does not contain {exeName}.");
        var target = Path.Combine(DownloadFolder, exeName);
        entry.ExtractToFile(target, overwrite: true);

        if (!OperatingSystem.IsWindows())
        {
            // Zip extraction doesn't keep the executable bit.
            File.SetUnixFileMode(target, File.GetUnixFileMode(target)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private static FfmpegRelease? ForCurrentPlatform()
    {
        var arch = RuntimeInformation.OSArchitecture;
        string? key = null;
        if (OperatingSystem.IsWindows() && arch is Architecture.X64 or Architecture.Arm64) key = "win-64";
        else if (OperatingSystem.IsMacOS() && arch is Architecture.X64 or Architecture.Arm64) key = "macos-64";
        else if (OperatingSystem.IsLinux() && arch == Architecture.X64) key = "linux-64";
        else if (OperatingSystem.IsLinux() && arch == Architecture.Arm64) key = "linux-arm-64";
        return key != null ? KnownReleases[key] : null;
    }

    private static IEnumerable<string> CandidateFolders()
    {
        yield return AppContext.BaseDirectory;
        yield return DownloadFolder;
        foreach (var extra in AdditionalSearchFolders) yield return extra;
        if (!SearchPath) yield break;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return dir.Trim('"');
    }
}
