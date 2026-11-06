namespace KaraokeToMp4.Midi;

/// <summary>Describes a SoundFont that <see cref="SoundFontLocator"/> can download.</summary>
/// <param name="Name">Display name.</param>
/// <param name="FileName">File name it is saved under in <see cref="SoundFontLocator.DownloadFolder"/>.</param>
/// <param name="Url">Download URL (pinned to a fixed version so the checksum stays valid).</param>
/// <param name="Sha256">Expected SHA-256 of the file (lower-case hex), or null to skip verification.</param>
/// <param name="SizeBytes">Expected size in bytes, used for progress and shown to the user.</param>
/// <param name="License">Short licence summary to show the user.</param>
public sealed record SoundFontInfo(string Name, string FileName, Uri Url, string? Sha256, long SizeBytes, string License)
{
    /// <summary>Size in MB, rounded, for prompts.</summary>
    public int SizeMegabytes => (int)Math.Round(SizeBytes / (1024.0 * 1024.0));
}

/// <summary>What the caller is asked to approve before a SoundFont is downloaded.</summary>
/// <param name="SoundFont">The SoundFont that would be downloaded.</param>
/// <param name="Destination">Full path it will be saved to.</param>
public sealed record SoundFontDownloadRequest(SoundFontInfo SoundFont, string Destination);

/// <summary>
/// Finds a General MIDI SoundFont (.sf2) for synthesising KAR/MIDI audio, or downloads one with the
/// user's permission, so nobody has to hunt for a SoundFont by hand.
/// </summary>
public static class SoundFontLocator
{
    /// <summary>
    /// GeneralUser GS v2 by S. Christian Collins: a ~31 MB General MIDI / GS bank that sounds good on
    /// typical karaoke MIDI files, is small enough to download quickly, and is free to use and
    /// redistribute (including commercially). Pinned to a specific commit so the checksum is stable.
    /// </summary>
    public static SoundFontInfo GeneralUserGs { get; } = new(
        Name: "GeneralUser GS 2.0",
        FileName: "GeneralUser-GS.sf2",
        Url: new Uri("https://raw.githubusercontent.com/mrbumpy409/GeneralUser-GS/684543d5e5efaef08d02be50dcda8d552478fa60/GeneralUser-GS.sf2"),
        Sha256: "9575028c7a1f589f5770fccc8cff2734566af40cd26ed836944e9a5152688cfe",
        SizeBytes: 32_319_396,
        License: "Free to use and redistribute, including commercially (GeneralUser GS License v2.0).");

    /// <summary>The SoundFont offered for download when none is found. Defaults to <see cref="GeneralUserGs"/>.</summary>
    public static SoundFontInfo Recommended { get; set; } = GeneralUserGs;

    /// <summary>Where downloaded SoundFonts are stored (per-user, survives app updates).</summary>
    public static string DownloadFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaraokeTools", "soundfonts");

    /// <summary>Also search well-known OS folders (e.g. /usr/share/sounds/sf2). Default true.</summary>
    public static bool SearchSystemFolders { get; set; } = true;

    /// <summary>Extra folders to search for .sf2 files, checked before the system folders.</summary>
    public static IList<string> AdditionalSearchFolders { get; } = new List<string>();

    /// <summary>
    /// Looks for an .sf2 file: the recommended file in <see cref="DownloadFolder"/>, any .sf2 in the
    /// app folder or the download folder, then well-known system locations. Returns null if none exists.
    /// </summary>
    public static string? TryLocate()
    {
        var preferred = Path.Combine(DownloadFolder, Recommended.FileName);
        if (File.Exists(preferred)) return preferred;

        foreach (var dir in CandidateFolders())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var match = Directory.EnumerateFiles(dir, "*.sf2")
                    .OrderByDescending(f => Path.GetFileName(f).Contains("GeneralUser", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => Path.GetFileName(f).Contains("GM", StringComparison.Ordinal))
                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (match != null) return match;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Unreadable folder: keep looking.
            }
        }
        return null;
    }

    /// <summary>
    /// Returns a SoundFont path, downloading <see cref="Recommended"/> if none is found and
    /// <paramref name="confirmDownload"/> approves. Returns null if the user declines.
    /// </summary>
    /// <param name="confirmDownload">
    /// Asked before anything is downloaded (e.g. show a Yes/No dialog). When null, nothing is downloaded.
    /// </param>
    public static async Task<string?> EnsureAsync(
        Func<SoundFontDownloadRequest, CancellationToken, Task<bool>>? confirmDownload,
        IProgress<double>? downloadProgress = null,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        var existing = TryLocate();
        if (existing != null) return existing;
        if (confirmDownload == null) return null;

        var request = new SoundFontDownloadRequest(Recommended, Path.Combine(DownloadFolder, Recommended.FileName));
        if (!await confirmDownload(request, cancellationToken).ConfigureAwait(false)) return null;
        return await DownloadAsync(Recommended, downloadProgress, cancellationToken, httpClient).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads <paramref name="soundFont"/> (default <see cref="Recommended"/>) into
    /// <see cref="DownloadFolder"/>, verifying its checksum, and returns the file path.
    /// </summary>
    /// <param name="downloadProgress">Receives 0..1 as the download proceeds.</param>
    public static async Task<string> DownloadAsync(
        SoundFontInfo? soundFont = null,
        IProgress<double>? downloadProgress = null,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        soundFont ??= Recommended;
        var destination = Path.Combine(DownloadFolder, soundFont.FileName);
        long total = soundFont.SizeBytes;
        await Downloads.VerifiedDownloader.DownloadAsync(
            new Downloads.DownloadAsset(soundFont.Url, soundFont.Sha256, soundFont.SizeBytes),
            destination,
            $"SoundFont '{soundFont.Name}'",
            read => { if (total > 0) downloadProgress?.Report(Math.Min(1.0, (double)read / total)); },
            httpClient,
            cancellationToken).ConfigureAwait(false);
        downloadProgress?.Report(1.0);
        return destination;
    }

    private static IEnumerable<string> CandidateFolders()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "soundfonts");
        yield return DownloadFolder;
        foreach (var extra in AdditionalSearchFolders) yield return extra;
        if (!SearchSystemFolders) yield break;

        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\soundfonts";
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, "Library", "Audio", "Sounds", "Banks");
            yield return "/Library/Audio/Sounds/Banks";
        }
        else
        {
            yield return "/usr/share/sounds/sf2";
            yield return "/usr/share/soundfonts";
            yield return "/usr/local/share/soundfonts";
        }
    }
}
