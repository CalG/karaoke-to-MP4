using System.Security.Cryptography;
using KaraokeToMp4.Cdg;

namespace KaraokeToMp4.Health;

/// <summary>Whether a song's files are moved or copied to the destination.</summary>
public enum RelocateAction
{
    Move,
    Copy,
}

/// <summary>What happened to one file.</summary>
public enum FileOutcome
{
    /// <summary>Moved to the destination.</summary>
    Moved,

    /// <summary>Copied to the destination (the original stays).</summary>
    Copied,

    /// <summary>An identical copy was already at the destination (for a move, the source was removed).</summary>
    AlreadyThere,

    /// <summary>A different file with the same name is already at the destination; nothing was done.</summary>
    Conflict,

    /// <summary>The song isn't under the source root (or the roots overlap); nothing was done.</summary>
    Skipped,

    /// <summary>An error occurred (see the message).</summary>
    Failed,
}

/// <summary>Result for one file of a song.</summary>
public sealed record RelocatedFile(string Source, string Destination, FileOutcome Outcome, string? Message = null);

/// <summary>Result for one song (all of its files).</summary>
public sealed record RelocateResult(SongHealth Song, IReadOnlyList<RelocatedFile> Files)
{
    /// <summary>True when every file of the song is now at the destination.</summary>
    public bool Succeeded => Files.Count > 0 && Files.All(f => f.Outcome is FileOutcome.Moved or FileOutcome.Copied or FileOutcome.AlreadyThere);

    /// <summary>One-line description of a failure (null when <see cref="Succeeded"/>).</summary>
    public string? Problem => Succeeded ? null : Files.FirstOrDefault(f => f.Message != null)?.Message ?? "Not relocated.";
}

/// <summary>
/// Moves or copies songs from a source root to a destination root, keeping each song's path relative to
/// the root (e.g. <c>Archive\CDG\e\song.zip</c> becomes <c>Missing Music\CDG\e\song.zip</c>).
/// </summary>
/// <remarks>
/// A song's files always travel together: a .cdg with its audio file(s), and any .bak copies left by
/// repairs. Existing files are never overwritten: if a different file with the same name is already at
/// the destination, the whole song is left in place and reported as a conflict.
/// </remarks>
public static class SongOrganizer
{
    /// <summary>All files that make up the song at <paramref name="songPath"/>, including backups.</summary>
    public static IReadOnlyList<string> SongFiles(string songPath)
    {
        var files = new List<string>();
        if (File.Exists(songPath)) files.Add(songPath);

        var dir = Path.GetDirectoryName(Path.GetFullPath(songPath))!;
        var baseName = Path.GetFileNameWithoutExtension(songPath);
        IEnumerable<string> siblings;
        try
        {
            // One directory listing per song (cheap even on network shares).
            siblings = Directory.EnumerateFiles(dir, baseName + ".*", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            siblings = Array.Empty<string>();
        }

        // A .cdg brings its audio (same name, any audio extension).
        if (Path.GetExtension(songPath).Equals(".cdg", StringComparison.OrdinalIgnoreCase))
        {
            files.AddRange(siblings.Where(f =>
                string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase) &&
                CdgConverter.AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
        }

        // Backups made by repairs: "<file>.bak", "<file>.bak2", ...
        var backups = siblings.Where(f => files.Any(main => IsBackupOf(f, main))).ToList();
        files.AddRange(backups);
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsBackupOf(string candidate, string file)
    {
        var name = Path.GetFileName(candidate);
        var main = Path.GetFileName(file);
        if (!name.StartsWith(main + ".bak", StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = name[(main.Length + 4)..];
        return suffix.All(char.IsDigit);
    }

    /// <summary>
    /// Checks a source/destination root pair. Returns an error message, or null when they can be used.
    /// </summary>
    public static string? ValidateRoots(string sourceRoot, string destinationRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot)) return "Choose the source root folder.";
        if (string.IsNullOrWhiteSpace(destinationRoot)) return "Choose a destination folder.";
        var src = Normalize(sourceRoot);
        var dst = Normalize(destinationRoot);
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) return "The destination is the same folder as the source root.";
        if (IsInside(dst, src)) return $"The destination '{destinationRoot}' is inside the source root; moved songs would be scanned again.";
        if (IsInside(src, dst)) return $"The source root is inside the destination '{destinationRoot}'.";
        return null;
    }

    /// <summary>
    /// The path of <paramref name="path"/> relative to <paramref name="root"/>, or null if it isn't inside it.
    /// </summary>
    public static string? RelativeTo(string root, string path)
    {
        var rel = Path.GetRelativePath(Normalize(root), Path.GetFullPath(path));
        if (rel == "." || Path.IsPathRooted(rel) || rel == ".." ||
            rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            rel.StartsWith("../", StringComparison.Ordinal))
            return null;
        return rel;
    }

    /// <summary>
    /// Moves or copies all files of <paramref name="song"/> from <paramref name="sourceRoot"/> to the same
    /// relative location under <paramref name="destinationRoot"/>.
    /// </summary>
    public static RelocateResult Relocate(SongHealth song, string sourceRoot, string destinationRoot, RelocateAction action)
    {
        var error = ValidateRoots(sourceRoot, destinationRoot);
        if (error != null) return Fail(song, FileOutcome.Skipped, error);

        var files = SongFiles(song.Path);
        if (files.Count == 0) return Fail(song, FileOutcome.Skipped, "The file no longer exists.");

        // Plan every file first, so a song is never split up by a conflict half-way through.
        var plan = new List<(string Source, string Destination, bool Identical)>();
        foreach (var file in files)
        {
            var rel = RelativeTo(sourceRoot, file);
            if (rel == null) return Fail(song, FileOutcome.Skipped, $"'{file}' is not inside the source root '{sourceRoot}'.");
            var dest = Path.Combine(Normalize(destinationRoot), rel);

            bool identical = false;
            if (File.Exists(dest))
            {
                try
                {
                    identical = SameContent(file, dest);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Fail(song, FileOutcome.Failed, $"Could not compare with '{dest}': {ex.Message}");
                }
                if (!identical)
                {
                    return new RelocateResult(song, files.Select(f => new RelocatedFile(f, dest,
                        f == file ? FileOutcome.Conflict : FileOutcome.Skipped,
                        f == file ? $"A different file already exists at '{dest}'." : null)).ToList());
                }
            }
            plan.Add((file, dest, identical));
        }

        var results = new List<RelocatedFile>();
        foreach (var (source, dest, identical) in plan)
        {
            try
            {
                if (identical)
                {
                    if (action == RelocateAction.Move) File.Delete(source);
                    results.Add(new RelocatedFile(source, dest, FileOutcome.AlreadyThere));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (action == RelocateAction.Move)
                {
                    // File.Move works across drives and network shares (it copies, then deletes).
                    File.Move(source, dest, overwrite: false);
                    results.Add(new RelocatedFile(source, dest, FileOutcome.Moved));
                }
                else
                {
                    File.Copy(source, dest, overwrite: false);
                    File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
                    if (new FileInfo(dest).Length != new FileInfo(source).Length)
                        throw new IOException("The copy has a different size from the original.");
                    results.Add(new RelocatedFile(source, dest, FileOutcome.Copied));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new RelocatedFile(source, dest, FileOutcome.Failed, ex.Message));
            }
        }
        return new RelocateResult(song, results);
    }

    /// <summary>
    /// Deletes folders under <paramref name="root"/> that are now empty, starting from each of
    /// <paramref name="folders"/> and walking up towards the root (the root itself is never deleted).
    /// </summary>
    /// <returns>The number of folders removed.</returns>
    public static int RemoveEmptyFolders(string root, IEnumerable<string> folders)
    {
        var normalizedRoot = Normalize(root);
        int removed = 0;
        foreach (var start in folders.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Length))
        {
            var dir = Normalize(start);
            while (IsInside(dir, normalizedRoot))
            {
                try
                {
                    if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
                    Directory.Delete(dir);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }
                dir = Path.GetDirectoryName(dir)!;
            }
        }
        return removed;
    }

    private static RelocateResult Fail(SongHealth song, FileOutcome outcome, string message) =>
        new(song, new[] { new RelocatedFile(song.Path, "", outcome, message) });

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="folder"/>.</summary>
    private static bool IsInside(string path, string folder)
    {
        var rel = Path.GetRelativePath(folder, path);
        return rel != "." && !Path.IsPathRooted(rel) && rel != ".." &&
               !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !rel.StartsWith("../", StringComparison.Ordinal);
    }
}
