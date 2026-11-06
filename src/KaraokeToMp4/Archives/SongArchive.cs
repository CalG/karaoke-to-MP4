using KaraokeToMp4.Cdg;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace KaraokeToMp4.Archives;

/// <summary>One file inside an archive.</summary>
public sealed class SongArchiveEntry
{
    private readonly IArchiveEntry _entry;

    internal SongArchiveEntry(IArchiveEntry entry)
    {
        _entry = entry;
        FullName = (entry.Key ?? "").Replace('\\', '/');
        Name = Path.GetFileName(FullName);
    }

    /// <summary>File name without folders.</summary>
    public string Name { get; }

    /// <summary>Path inside the archive.</summary>
    public string FullName { get; }

    /// <summary>Uncompressed size in bytes.</summary>
    public long Size => _entry.Size;

    public DateTime? LastModified => _entry.LastModifiedTime;

    public bool IsEncrypted => _entry.IsEncrypted;

    /// <summary>Decompresses the entry.</summary>
    public byte[] ReadAllBytes()
    {
        using var input = _entry.OpenEntryStream();
        using var ms = new MemoryStream(Size > 0 && Size < int.MaxValue ? (int)Size : 0);
        input.CopyTo(ms);
        return ms.ToArray();
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

    /// <summary>Opens <paramref name="path"/>; throws <see cref="InvalidDataException"/> if it isn't a readable archive.</summary>
    public static SongArchive Open(string path)
    {
        IArchive outer;
        try
        {
            outer = ArchiveFactory.Open(path, new ReaderOptions());
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a readable archive: {ex.Message}", ex);
        }

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
