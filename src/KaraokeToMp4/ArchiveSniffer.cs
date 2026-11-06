namespace KaraokeToMp4;

/// <summary>Archive formats recognised by their content (not their file extension).</summary>
public enum ArchiveKind
{
    None,
    Zip,
    GZip,
    SevenZip,
    Rar,
    Tar,
}

/// <summary>
/// Identifies archives by their first bytes, so song bundles with a custom extension (e.g. a zip renamed
/// to ".mp3gz" or ".kzip") are still recognised.
/// </summary>
public static class ArchiveSniffer
{
    /// <summary>Extensions that are never archives, so collection scans don't need to open them.</summary>
    private static readonly HashSet<string> NeverArchive = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cdg", ".kar", ".mid", ".midi", ".mp3", ".ogg", ".wav", ".flac", ".m4a", ".aac", ".wma",
        ".mp4", ".m4v", ".avi", ".mkv", ".wmv", ".mov", ".mpg", ".mpeg", ".vob",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".txt", ".nfo", ".lrc", ".pdf", ".doc", ".docx",
        ".htm", ".html", ".xml", ".json", ".ini", ".db", ".log", ".lnk", ".url", ".m3u", ".m3u8", ".pls",
        ".cue", ".sfv", ".md5", ".exe", ".dll", ".sf2",
    };

    public static ArchiveKind Detect(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[512];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return Detect(head[..n]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ArchiveKind.None;
        }
    }

    public static ArchiveKind Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && (head[2] is 3 or 5 or 7) && (head[3] is 4 or 6 or 8))
            return ArchiveKind.Zip;
        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
            return ArchiveKind.GZip;
        if (head.Length >= 6 && head[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
            return ArchiveKind.SevenZip;
        if (head.Length >= 4 && head[..4].SequenceEqual("Rar!"u8))
            return ArchiveKind.Rar;
        if (head.Length >= 262 && head.Slice(257, 5).SequenceEqual("ustar"u8))
            return ArchiveKind.Tar;
        return ArchiveKind.None;
    }

    /// <summary>True for .zip files and for zips with any other extension.</summary>
    public static bool IsZip(string path) =>
        Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            ? File.Exists(path)
            : !NeverArchive.Contains(Path.GetExtension(path)) && Detect(path) == ArchiveKind.Zip;

    /// <summary>True for any recognised archive (ZIP, 7-Zip, RAR, TAR, gzip), whatever its extension.</summary>
    public static bool IsArchive(string path) =>
        File.Exists(path) && !NeverArchive.Contains(Path.GetExtension(path)) && Detect(path) != ArchiveKind.None;

    /// <summary>Archive extensions that are treated as archives without sniffing their content.</summary>
    public static readonly IReadOnlySet<string> ArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz",
    };

    /// <summary>
    /// True for files with an archive extension, and for any other file whose content is a recognised
    /// archive (e.g. a song bundle saved as ".mp3gz").
    /// </summary>
    public static bool IsArchiveFile(string path) =>
        (ArchiveExtensions.Contains(Path.GetExtension(path)) && File.Exists(path)) || IsArchive(path);

    /// <summary>True when a file with this extension could be an archive worth opening during a scan.</summary>
    public static bool MightBeArchive(string path) => !NeverArchive.Contains(Path.GetExtension(path));

    public static string Describe(ArchiveKind kind) => kind switch
    {
        ArchiveKind.Zip => "ZIP",
        ArchiveKind.GZip => "GZip",
        ArchiveKind.SevenZip => "7-Zip",
        ArchiveKind.Rar => "RAR",
        ArchiveKind.Tar => "TAR",
        _ => "not an archive",
    };
}
