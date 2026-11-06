using System.IO.Compression;
using KaraokeToMp4.Cdg;
using KaraokeToMp4.Midi;

namespace KaraokeToMp4.Health;

/// <summary>Overall condition of a karaoke song file.</summary>
public enum SongHealthStatus
{
    /// <summary>No problems detected.</summary>
    NoIssues,

    /// <summary>Damaged, but <see cref="SongHealthChecker.Repair"/> can fix it completely.</summary>
    Repairable,

    /// <summary>Damaged in a way that can't be fixed (part of the song missing, unreadable file...).</summary>
    CannotRepair,

    /// <summary>Music without lyrics: audio with no .cdg (or an empty one), or a MIDI file with no lyrics.</summary>
    OnlyMusic,

    /// <summary>Lyrics without music: a .cdg with no (or empty) audio, or a KAR/MIDI file with lyrics but no notes.</summary>
    OnlyLyrics,
}

/// <summary>Result of checking one song.</summary>
/// <param name="Path">The song's main file (.cdg, .zip, .kar or .mid; or a lone audio file).</param>
/// <param name="Kind">Short description of the format, e.g. "MP3+G", "KAR".</param>
/// <param name="Status">Overall verdict.</param>
/// <param name="Summary">One-line explanation suitable for a list view.</param>
/// <param name="Details">Further detail lines (may be empty).</param>
public sealed record SongHealth(string Path, string Kind, SongHealthStatus Status, string Summary, IReadOnlyList<string> Details)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>
/// Scans karaoke folders for damaged songs and repairs what can be repaired.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>MP3+G</b> (.cdg with audio, or a .zip of both): CD+G error-correction data repairs packets with
/// up to two damaged bytes. More damage, a missing/empty audio file, or an empty .cdg can't be repaired.</item>
/// <item><b>KAR / MIDI</b>: malformed bytes (bad event lengths, junk after the end of a track) are cleaned up
/// by rewriting the file. A file that was cut off is missing part of the song and can't be repaired.</item>
/// </list>
/// Repairs write a backup of the original (<c>name.ext.bak</c>) unless told not to.
/// </remarks>
public static class SongHealthChecker
{
    private static readonly string[] MidiExtensions = { ".kar", ".mid", ".midi" };

    /// <summary>
    /// Lists the songs in <paramref name="folders"/>: each .cdg (with its audio), each .zip, each .kar/.mid,
    /// every audio file without a matching .cdg (music that is missing its lyrics), and archives with other
    /// extensions (recognised by their content). Backups (.bak) and unreadable folders are skipped.
    /// </summary>
    public static IEnumerable<string> FindSongs(IEnumerable<string> folders, bool includeSubfolders = true)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in folders.Where(Directory.Exists))
        {
            var byFolder = Directory.EnumerateFiles(root, "*", options).GroupBy(f => System.IO.Path.GetDirectoryName(f) ?? "");
            foreach (var folder in byFolder)
            {
                var files = folder.ToList();
                var cdgBases = new HashSet<string>(
                    files.Where(f => Ext(f) == ".cdg").Select(f => System.IO.Path.GetFileNameWithoutExtension(f)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var ext = Ext(f);
                    bool song = ext == ".cdg" || ext == ".zip" || MidiExtensions.Contains(ext) ||
                                // Audio without graphics: music that is missing its lyrics.
                                (CdgConverter.AudioExtensions.Contains(ext) &&
                                 !cdgBases.Contains(System.IO.Path.GetFileNameWithoutExtension(f))) ||
                                // Song bundles with a custom extension (e.g. ".mp3gz"): recognised by content.
                                (!IsBackup(f) && ArchiveSniffer.MightBeArchive(f) && ArchiveSniffer.Detect(f) != ArchiveKind.None);
                    if (song && seen.Add(f)) yield return f;
                }
            }
        }
    }

    /// <summary>Checks one song (see <see cref="FindSongs"/> for which paths count as songs).</summary>
    public static SongHealth Check(string path)
    {
        try
        {
            var ext = Ext(path);
            if (ext == ".cdg") return CheckCdgPair(path);
            if (ext == ".zip") return CheckZip(path);
            if (MidiExtensions.Contains(ext)) return CheckMidi(path);
            if (CdgConverter.AudioExtensions.Contains(ext))
                return Result(path, "Audio", SongHealthStatus.OnlyMusic, "Missing lyrics: no matching .cdg file.");

            var archive = ArchiveSniffer.MightBeArchive(path) ? ArchiveSniffer.Detect(path) : ArchiveKind.None;
            if (archive == ArchiveKind.Zip) return CheckZip(path);
            if (archive != ArchiveKind.None)
                return Result(path, $"{ArchiveSniffer.Describe(archive)} ({ext})", SongHealthStatus.CannotRepair,
                    $"{ArchiveSniffer.Describe(archive)}-compressed file: can't look inside (only ZIP-style song bundles are supported).");
            return Result(path, "Unknown", SongHealthStatus.CannotRepair, "Not a karaoke file type.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Result(path, KindOf(path), SongHealthStatus.CannotRepair, "Could not read the file: " + ex.Message);
        }
    }

    /// <summary>
    /// Repairs a song reported as <see cref="SongHealthStatus.Repairable"/> and returns its new health.
    /// The original is kept as <c>name.ext.bak</c> (or .bak2, .bak3...) when <paramref name="keepBackup"/> is true.
    /// Songs that aren't repairable are left untouched.
    /// </summary>
    public static SongHealth Repair(string path, bool keepBackup = true) => RepairAll(path, keepBackup)[0];

    /// <summary>
    /// Like <see cref="Repair"/>, but returns every resulting song: repairing a zip that holds several songs
    /// splits it into one zip per song (named after each song, with the original's extension), so one
    /// repair can produce several files. The original zip is kept as a .bak when <paramref name="keepBackup"/> is true.
    /// </summary>
    public static IReadOnlyList<SongHealth> RepairAll(string path, bool keepBackup = true)
    {
        var before = Check(path);
        if (before.Status != SongHealthStatus.Repairable) return new[] { before };

        var ext = Ext(path);
        if (ext != ".cdg" && !MidiExtensions.Contains(ext) && ArchiveSniffer.IsZip(path))
        {
            int songs;
            using (var zip = ZipFile.OpenRead(path)) songs = ZipSongs(zip).Count;
            if (songs > 1) return SplitZip(path, keepBackup).Select(Check).ToList();
            RepairZip(path, keepBackup);
            return new[] { Check(path) };
        }

        if (ext == ".cdg")
        {
            var data = File.ReadAllBytes(path);
            CdgInspector.RepairInPlace(data);
            ReplaceFile(path, WholePackets(data), keepBackup);
        }
        else if (MidiExtensions.Contains(ext))
        {
            var midi = StandardMidiFile.FromFile(path);
            ReplaceFile(path, midi.ToBytes(), keepBackup);
        }
        return new[] { Check(path) };
    }

    // ---------------- checks ----------------

    private static SongHealth CheckCdgPair(string cdgPath)
    {
        var audio = CdgConverter.FindAudioForCdg(cdgPath);
        if (audio == null)
            return Result(cdgPath, "MP3+G", SongHealthStatus.OnlyLyrics, "No matching audio file, e.g. .mp3 (lyrics only).");
        if (new FileInfo(audio).Length == 0)
            return Result(cdgPath, "MP3+G", SongHealthStatus.OnlyLyrics, $"The audio file is empty, 0 bytes (lyrics only): {System.IO.Path.GetFileName(audio)}");
        return FromCdgData(cdgPath, "MP3+G", File.ReadAllBytes(cdgPath));
    }

    /// <summary>One song inside a zip: its .cdg and/or audio entry.</summary>
    private sealed record ZipSong(string Name, ZipArchiveEntry? Cdg, ZipArchiveEntry? Audio);

    /// <summary>Pairs up the .cdg and audio entries of a zip by name (one song per .cdg).</summary>
    private static List<ZipSong> ZipSongs(ZipArchive zip)
    {
        var cdgs = zip.Entries.Where(e => Ext(e.Name) == ".cdg").ToList();
        var audios = zip.Entries.Where(e => CdgConverter.AudioExtensions.Contains(Ext(e.Name))).ToList();
        var songs = new List<ZipSong>();
        var used = new HashSet<ZipArchiveEntry>();
        foreach (var cdg in cdgs)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(cdg.Name);
            var audio = audios.FirstOrDefault(a => !used.Contains(a) &&
                string.Equals(System.IO.Path.GetFileNameWithoutExtension(a.Name), name, StringComparison.OrdinalIgnoreCase));
            if (audio != null) used.Add(audio);
            songs.Add(new ZipSong(name, cdg, audio));
        }

        var leftover = audios.Where(a => !used.Contains(a)).ToList();
        // A single pair whose files are named differently is still one song.
        if (songs.Count == 1 && songs[0].Audio == null && leftover.Count == 1)
            return new List<ZipSong> { songs[0] with { Audio = leftover[0] } };
        songs.AddRange(leftover.Select(a => new ZipSong(System.IO.Path.GetFileNameWithoutExtension(a.Name), null, a)));
        return songs;
    }

    private static SongHealth CheckZip(string zipPath)
    {
        var ext = Ext(zipPath);
        string kind = ext == ".zip" ? "MP3+G zip" : $"MP3+G zip ({ext})";
        using var zip = ZipFile.OpenRead(zipPath);
        var songs = ZipSongs(zip);

        if (songs.Count == 0)
            return Result(zipPath, ext == ".zip" ? "ZIP" : $"ZIP ({ext})", SongHealthStatus.CannotRepair, "The archive contains no karaoke files.");

        if (songs.Count > 1)
        {
            var details = songs.Select(sg =>
                $"{sg.Name}: " + (sg.Cdg == null ? "audio only" : sg.Audio == null ? "graphics only" :
                    CdgInspector.Inspect(ReadEntry(sg.Cdg)).Summary)).ToList();
            return new SongHealth(zipPath, kind, SongHealthStatus.Repairable,
                $"Contains {songs.Count} songs (one per zip expected). Fix splits it into {songs.Count} zips.", details);
        }

        var song = songs[0];
        bool hasAudio = song.Audio is { Length: > 0 };
        bool hasCdg = song.Cdg is { Length: > 0 };
        if (!hasCdg)
            return Result(zipPath, kind, SongHealthStatus.OnlyMusic, "The zip has audio but no .cdg graphics (music only).");
        if (!hasAudio)
            return Result(zipPath, kind, SongHealthStatus.OnlyLyrics,
                (song.Audio == null ? "The zip has no audio file" : "The audio file inside the zip is empty") + " (lyrics only).");
        return FromCdgData(zipPath, kind, ReadEntry(song.Cdg!));
    }

    private static SongHealth FromCdgData(string path, string kind, byte[] data)
    {
        if (data.Length < CdgDecoder.PacketSize)
            return Result(path, kind, SongHealthStatus.OnlyMusic, "The .cdg file is empty (music only).");

        var report = CdgInspector.Inspect(data);
        var details = new List<string>
        {
            $"{report.Packets:N0} packets ({report.Duration:m\\:ss}); error-correction data: {(report.HasParity ? "yes" : "no")}",
        };
        if (report.DamageTimes.Count > 0)
        {
            var times = report.DamageTimes.GroupBy(t => (int)t.TotalSeconds)
                .Select(g => $"{g.Key / 60}:{g.Key % 60:00}" + (g.Count() > 1 ? $"x{g.Count()}" : ""));
            details.Add("Damage at " + string.Join(", ", times.Take(30)) + (report.DamageTimes.Count > 30 ? ", ..." : ""));
        }

        SongHealthStatus status;
        if (report.Unrepairable > 0) status = SongHealthStatus.CannotRepair;
        else if (report.Repairable > 0 || report.TrailingBytes > 0) status = SongHealthStatus.Repairable;
        else status = SongHealthStatus.NoIssues;

        var summary = status switch
        {
            SongHealthStatus.CannotRepair =>
                $"{report.Unrepairable} graphics packet{(report.Unrepairable == 1 ? " is" : "s are")} too damaged to repair" +
                (report.Repairable > 0 ? $" ({report.Repairable} others repairable)" : "") + ".",
            SongHealthStatus.Repairable =>
                (report.Repairable > 0 ? $"{report.Repairable} damaged graphics packet{(report.Repairable == 1 ? "" : "s")} (all repairable)" : "File ends mid-packet") +
                (report.Repairable > 0 && report.TrailingBytes > 0 ? "; file ends mid-packet" : "") + ".",
            _ => report.Summary,
        };
        return new SongHealth(path, kind, status, summary, details);
    }

    private static SongHealth CheckMidi(string path)
    {
        string kind = Ext(path) == ".kar" ? "KAR" : "MIDI";
        if (new FileInfo(path).Length == 0)
            return Result(path, kind, SongHealthStatus.CannotRepair, "The file is empty (0 bytes).");

        StandardMidiFile midi;
        try { midi = StandardMidiFile.FromFile(path); }
        catch (InvalidDataException) { return Result(path, kind, SongHealthStatus.CannotRepair, "Not a valid MIDI file."); }

        if (KaraokeConverter.DetectFormat(path) == KaraokeFormat.Unknown)
            return Result(path, kind, SongHealthStatus.OnlyMusic, "Plain MIDI file with no lyrics (music only).");

        bool hasNotes = midi.Events.Any(e => !e.IsMeta && (e.Status & 0xF0) == 0x90 && e.Data.Length > 1 && e.Data[1] > 0);
        if (!hasNotes)
            return Result(path, kind, SongHealthStatus.OnlyLyrics, "Lyrics but no music: the file has no notes (lyrics only).");

        var details = midi.Problems.Concat(midi.Repairs).ToList();
        if (midi.IsTruncated)
            return new SongHealth(path, kind, SongHealthStatus.CannotRepair,
                "The file is cut off: part of the song is missing (it plays, but stops early).", details);
        if (midi.IsDamaged || midi.Repairs.Count > 0)
            return new SongHealth(path, kind, SongHealthStatus.Repairable,
                "Malformed data that can be cleaned up (" + (details.FirstOrDefault() ?? "damaged") + ")", details);
        return new SongHealth(path, kind, SongHealthStatus.NoIssues, "OK - no damage found", details);
    }

    // ---------------- repair helpers ----------------

    private static void RepairZip(string zipPath, bool keepBackup)
    {
        // Work on a copy so the original is only replaced once the new zip is complete.
        var temp = zipPath + ".repairing";
        File.Copy(zipPath, temp, overwrite: true);
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Update))
            {
                var entry = ZipSongs(zip)[0].Cdg!;
                var data = ReadEntry(entry);
                CdgInspector.RepairInPlace(data);
                var name = entry.FullName;
                var modified = entry.LastWriteTime;
                entry.Delete();
                var fresh = zip.CreateEntry(name, CompressionLevel.Optimal);
                fresh.LastWriteTime = modified;
                using var s = fresh.Open();
                s.Write(WholePackets(data));
            }
            ReplaceWith(temp, zipPath, keepBackup);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Splits a zip holding several songs into one zip per song, next to the original and with the same
    /// extension. Damaged .cdg files are repaired on the way. The original is kept as a .bak (or deleted).
    /// Nothing is changed if any step fails.
    /// </summary>
    private static List<string> SplitZip(string zipPath, bool keepBackup)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(zipPath))!;
        var ext = System.IO.Path.GetExtension(zipPath);
        var temps = new List<(string Temp, string Name)>();
        try
        {
            using (var source = ZipFile.OpenRead(zipPath))
            {
                foreach (var song in ZipSongs(source))
                {
                    var temp = System.IO.Path.Combine(dir, $".split-{Guid.NewGuid():N}{ext}");
                    using (var target = ZipFile.Open(temp, ZipArchiveMode.Create))
                    {
                        if (song.Audio != null) CopyEntry(song.Audio, target, null);
                        if (song.Cdg != null)
                        {
                            var data = ReadEntry(song.Cdg);
                            CdgInspector.RepairInPlace(data);
                            CopyEntry(song.Cdg, target, WholePackets(data));
                        }
                    }
                    temps.Add((temp, SongInfo.SanitizeFileName(song.Name)));
                }
            }

            if (keepBackup) File.Move(zipPath, NextBackupName(zipPath));
            else File.Delete(zipPath);
        }
        catch
        {
            foreach (var (temp, _) in temps)
                if (File.Exists(temp)) File.Delete(temp);
            throw;
        }

        var created = new List<string>();
        foreach (var (temp, name) in temps)
        {
            var final = System.IO.Path.Combine(dir, name + ext);
            for (int n = 2; File.Exists(final); n++) final = System.IO.Path.Combine(dir, $"{name} ({n}){ext}");
            File.Move(temp, final);
            created.Add(final);
        }
        return created;
    }

    private static void CopyEntry(ZipArchiveEntry source, ZipArchive target, byte[]? replacement)
    {
        var entry = target.CreateEntry(source.Name, CompressionLevel.Optimal);
        entry.LastWriteTime = source.LastWriteTime;
        using var output = entry.Open();
        if (replacement != null)
        {
            output.Write(replacement);
        }
        else
        {
            using var input = source.Open();
            input.CopyTo(output);
        }
    }

    private static bool IsBackup(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetExtension(path), @"^\.bak\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
        path.EndsWith(".repairing", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes <paramref name="bytes"/> over <paramref name="path"/>, keeping a backup if asked.</summary>
    private static void ReplaceFile(string path, byte[] bytes, bool keepBackup)
    {
        var temp = path + ".repairing";
        File.WriteAllBytes(temp, bytes);
        try { ReplaceWith(temp, path, keepBackup); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void ReplaceWith(string newFile, string path, bool keepBackup)
    {
        var modified = File.GetLastWriteTimeUtc(path);
        File.Replace(newFile, path, keepBackup ? NextBackupName(path) : null);
        File.SetLastWriteTimeUtc(path, modified); // keep the song's original date for sorting in players
    }

    /// <summary>"song.cdg.bak", or "song.cdg.bak2", ".bak3"... if earlier backups exist.</summary>
    public static string NextBackupName(string path)
    {
        var name = path + ".bak";
        for (int n = 2; File.Exists(name); n++) name = path + ".bak" + n;
        return name;
    }

    private static byte[] WholePackets(byte[] data) =>
        data.Length % CdgDecoder.PacketSize == 0 ? data : data[..(data.Length - data.Length % CdgDecoder.PacketSize)];

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static SongHealth Result(string path, string kind, SongHealthStatus status, string summary) =>
        new(path, kind, status, summary, Array.Empty<string>());

    private static string KindOf(string path) => Ext(path) switch
    {
        ".cdg" => "MP3+G",
        ".zip" => "MP3+G zip",
        ".kar" => "KAR",
        ".mid" or ".midi" => "MIDI",
        _ => "Audio",
    };

    private static string Ext(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant();
}
