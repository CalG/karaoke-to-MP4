using KaraokeToMp4.Cdg;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace KaraokeToMp4.Archives;

/// <summary>One file inside an archive.</summary>
public sealed class SongArchiveEntry
{
    private readonly Func<byte[]> _read;

    internal SongArchiveEntry(IArchiveEntry entry)
        : this(entry.Key, entry.Size, entry.LastModifiedTime, entry.IsEncrypted, () => ReadStream(entry.OpenEntryStream, entry.Size))
    {
    }

    /// <summary>An entry whose bytes were already read (used when recovering a damaged zip).</summary>
    internal SongArchiveEntry(string? key, byte[] data, DateTime? lastModified, bool encrypted)
        : this(key, data.Length, lastModified, encrypted, () => data)
    {
    }

    private SongArchiveEntry(string? key, long size, DateTime? lastModified, bool encrypted, Func<byte[]> read)
    {
        FullName = (key ?? "").Replace('\\', '/');
        Name = Path.GetFileName(FullName);
        Size = size;
        LastModified = lastModified;
        IsEncrypted = encrypted;
        _read = read;
    }

    private static byte[] ReadStream(Func<Stream> open, long size)
    {
        using var input = open();
        using var ms = new MemoryStream(size > 0 && size < int.MaxValue ? (int)size : 0);
        input.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>File name without folders.</summary>
    public string Name { get; }

    /// <summary>Path inside the archive.</summary>
    public string FullName { get; }

    /// <summary>Uncompressed size in bytes.</summary>
    public long Size { get; }

    public DateTime? LastModified { get; }

    public bool IsEncrypted { get; }

    /// <summary>Decompresses the entry.</summary>
    /// <exception cref="InvalidDataException">The entry's data is damaged.</exception>
    public byte[] ReadAllBytes()
    {
        try
        {
            return _read();
        }
        catch (Exception ex) when (SongArchive.IsDataError(ex))
        {
            throw new InvalidDataException($"'{Name}' inside the archive is damaged: {ex.Message}", ex);
        }
    }
}

/// <summary>A song inside an archive: its .cdg and/or audio file.</summary>
/// <param name="Name">Song name (the .cdg's or audio file's name without extension).</param>
public sealed record ArchivedSong(string Name, SongArchiveEntry? Cdg, SongArchiveEntry? Audio);

/// <summary>
/// Read access to karaoke song bundles in any common archive format, via SharpCompress: ZIP (including
/// LZMA/BZip2/PPMd/Deflate64 entries that .NET's own ZipArchive can't read), 7-Zip, RAR (incl. RAR5),
/// TAR, and gzip (including .tar.gz). The format is recognised from the content, not the extension.
/// </summary>
/// <remarks>Writing is done elsewhere and only as ZIP (SharpCompress can't create 7-Zip or RAR).</remarks>
public sealed class SongArchive : IDisposable
{
    private readonly List<IDisposable> _owned = new();

    private SongArchive(string formatName, ArchiveType type, IReadOnlyList<SongArchiveEntry> entries)
    {
        FormatName = formatName;
        Type = type;
        Entries = entries;
    }

    /// <summary>Human-readable format, e.g. "ZIP", "7-Zip", "RAR", "TAR", "tar.gz".</summary>
    public string FormatName { get; }

    /// <summary>The (outer) archive type.</summary>
    public ArchiveType Type { get; }

    /// <summary>Files in the archive (folders excluded).</summary>
    public IReadOnlyList<SongArchiveEntry> Entries { get; }

    /// <summary>True when any file is password-protected.</summary>
    public bool IsEncrypted => Entries.Any(e => e.IsEncrypted);

    /// <summary>True when this tool can rewrite the archive (ZIP only).</summary>
    public bool CanRewrite => Type == ArchiveType.Zip;

    /// <summary>
    /// True when the archive's index was damaged and the files were recovered by reading the archive from
    /// the start instead. <see cref="RecoveryNote"/> says what was (and wasn't) recovered.
    /// </summary>
    public bool IsRecovered { get; private init; }

    /// <summary>Explanation of a recovery (see <see cref="IsRecovered"/>).</summary>
    public string? RecoveryNote { get; private init; }

    /// <summary>
    /// True for exceptions caused by the archive's content (damage, unsupported data) rather than by access
    /// to the file (missing, locked, no permission) or cancellation.
    /// </summary>
    internal static bool IsDataError(Exception ex) =>
        ex is not (OperationCanceledException or UnauthorizedAccessException or FileNotFoundException
                  or DirectoryNotFoundException or OutOfMemoryException) &&
        (ex is not IOException || ex is EndOfStreamException || ex is InvalidDataException);

    /// <summary>Opens <paramref name="path"/>; throws <see cref="InvalidDataException"/> if it isn't a readable archive.</summary>
    public static SongArchive Open(string path)
    {
        try
        {
            return OpenNormally(path);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            // A zip whose index (central directory) is missing or damaged - typically a cut-off download or
            // copy - can usually still be read file by file from the start.
            string? lostFile = null;
            if (ArchiveSniffer.Detect(path) == ArchiveKind.Zip)
            {
                var recovered = TryRecoverZip(path, ex.Message, out lostFile);
                if (recovered != null) return recovered;
            }
            throw new InvalidDataException(lostFile != null
                ? $"'{Path.GetFileName(path)}' is damaged: the zip is cut off inside '{Path.GetFileName(lostFile)}', so its contents are lost."
                : $"'{Path.GetFileName(path)}' is damaged or not a readable archive: {ex.Message}", ex);
        }
    }

    private static SongArchive OpenNormally(string path)
    {
        var outer = ArchiveFactory.Open(path, new ReaderOptions());
        var owned = new List<IDisposable> { outer };
        try
        {
            var files = outer.Entries.Where(e => !e.IsDirectory).ToList();
            string format = Describe(outer.Type);

            // A .tar.gz / .tgz opens as a gzip holding one .tar: look inside that too.
            if (outer.Type == ArchiveType.GZip && files.Count == 1)
            {
                var inner = TryOpenNested(files[0], owned);
                if (inner != null)
                {
                    files = inner.Entries.Where(e => !e.IsDirectory).ToList();
                    format = "tar.gz";
                }
            }

            var archive = new SongArchive(format, outer.Type, files.Select(e => new SongArchiveEntry(e)).ToList());
            archive._owned.AddRange(owned);
            return archive;
        }
        catch
        {
            foreach (var d in owned) d.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads a zip front to back using the header stored before each file, ignoring the (damaged) index at
    /// the end. Files that are cut off are left out. Returns null when nothing could be recovered.
    /// </summary>
    private static SongArchive? TryRecoverZip(string path, string indexError, out string? lostAt)
    {
        var entries = new List<SongArchiveEntry>();
        lostAt = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = ReaderFactory.Open(fs, new ReaderOptions { LookForHeader = true });
            while (true)
            {
                bool more;
                try { more = reader.MoveToNextEntry(); }
                catch (Exception ex) when (IsDataError(ex)) { break; } // reached the damage
                if (!more) break;

                var entry = reader.Entry;
                if (entry.IsDirectory) continue;
                try
                {
                    using var ms = new MemoryStream();
                    using (var s = reader.OpenEntryStream()) s.CopyTo(ms);
                    if (entry.Size > 0 && ms.Length != entry.Size)
                    {
                        lostAt = entry.Key;
                        break;
                    }
                    entries.Add(new SongArchiveEntry(entry.Key, ms.ToArray(), entry.LastModifiedTime, entry.IsEncrypted));
                }
                catch (Exception ex) when (IsDataError(ex))
                {
                    lostAt = entry.Key;
                    break;
                }
            }
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            // Couldn't even start reading: fall through with whatever was recovered.
        }

        if (entries.Count == 0) return null;
        var note = $"The zip's index is damaged ({indexError}); {entries.Count} file{(entries.Count == 1 ? " was" : "s were")} recovered" +
                   (lostAt != null ? $", but '{Path.GetFileName(lostAt)}' is cut off and was lost." : ".");
        return new SongArchive("ZIP", ArchiveType.Zip, entries) { IsRecovered = true, RecoveryNote = note };
    }

    private static IArchive? TryOpenNested(IArchiveEntry entry, List<IDisposable> owned)
    {
        var ms = new MemoryStream();
        using (var s = entry.OpenEntryStream()) s.CopyTo(ms);
        ms.Position = 0;
        try
        {
            if (!ArchiveFactory.IsArchive(ms, out _, 65536)) { ms.Dispose(); return null; }
            ms.Position = 0;
            var inner = ArchiveFactory.Open(ms, new ReaderOptions());
            owned.Add(inner);
            owned.Add(ms);
            return inner;
        }
        catch
        {
            ms.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Groups the archive's files into songs: each .cdg with the audio file of the same name; a single
    /// .cdg and single audio file with different names still form one song; any other audio is a song
    /// without graphics.
    /// </summary>
    public IReadOnlyList<ArchivedSong> FindSongs()
    {
        var cdgs = Entries.Where(e => Ext(e.Name) == ".cdg").ToList();
        var audios = Entries.Where(e => CdgConverter.AudioExtensions.Contains(Ext(e.Name))).ToList();
        var songs = new List<ArchivedSong>();
        var used = new HashSet<SongArchiveEntry>();
        foreach (var cdg in cdgs)
        {
            var name = Path.GetFileNameWithoutExtension(cdg.Name);
            var audio = audios.FirstOrDefault(a => !used.Contains(a) &&
                string.Equals(Path.GetFileNameWithoutExtension(a.Name), name, StringComparison.OrdinalIgnoreCase));
            if (audio != null) used.Add(audio);
            songs.Add(new ArchivedSong(name, cdg, audio));
        }

        var leftover = audios.Where(a => !used.Contains(a)).ToList();
        if (songs.Count == 1 && songs[0].Audio == null && leftover.Count == 1)
            return new[] { songs[0] with { Audio = leftover[0] } };
        songs.AddRange(leftover.Select(a => new ArchivedSong(Path.GetFileNameWithoutExtension(a.Name), null, a)));
        return songs;
    }

    public static string Describe(ArchiveType type) => type switch
    {
        ArchiveType.Zip => "ZIP",
        ArchiveType.SevenZip => "7-Zip",
        ArchiveType.Rar => "RAR",
        ArchiveType.Tar => "TAR",
        ArchiveType.GZip => "gzip",
        _ => type.ToString(),
    };

    private static string Ext(string name) => Path.GetExtension(name).ToLowerInvariant();

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
    }
}
