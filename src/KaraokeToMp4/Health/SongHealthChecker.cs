using System.IO.Compression;
using KaraokeToMp4.Archives;
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

    /// <summary>
    /// An MP3+G song whose .cdg has no error-correction data, so damage inside the graphics can't be
    /// detected. It passed every check that is possible without that data: valid graphics instructions,
    /// intact MP3 audio frames, and matching .cdg and MP3 lengths.
    /// </summary>
    CannotVerify,
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
            if (MidiExtensions.Contains(ext)) return CheckMidi(path);
            if (CdgConverter.AudioExtensions.Contains(ext))
                return Result(path, "Audio", SongHealthStatus.OnlyMusic, "Missing lyrics: no matching .cdg file.");
            if (IsArchivePath(path)) return CheckArchive(path);
            return Result(path, "Unknown", SongHealthStatus.CannotRepair, "Not a karaoke file type.");
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Never let one bad file stop a scan of a whole collection.
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
        if (ext != ".cdg" && !MidiExtensions.Contains(ext) && IsArchivePath(path))
        {
            // Only ZIPs are ever "Repairable" (other formats are read-only), and they are written back as ZIP.
            int songs;
            using (var archive = SongArchive.Open(path)) songs = archive.FindSongs().Count;
            if (songs > 1) return SplitZip(path, keepBackup).Select(Check).ToList();
            RebuildZip(path, keepBackup);
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
        return FromCdgData(cdgPath, "MP3+G", File.ReadAllBytes(cdgPath), () => (System.IO.Path.GetFileName(audio), File.ReadAllBytes(audio)));
    }

    private static bool IsArchivePath(string path) => ArchiveSniffer.IsArchiveFile(path);

    private static readonly Dictionary<string, string[]> UsualExtensions = new()
    {
        ["ZIP"] = new[] { ".zip" },
        ["7-Zip"] = new[] { ".7z" },
        ["RAR"] = new[] { ".rar" },
        ["TAR"] = new[] { ".tar" },
        ["tar.gz"] = new[] { ".tgz", ".gz" },
        ["gzip"] = new[] { ".gz" },
    };

    private static SongHealth CheckArchive(string path)
    {
        var ext = Ext(path);
        SongArchive archive;
        try
        {
            archive = SongArchive.Open(path);
        }
        catch (InvalidDataException ex)
        {
            return Result(path, $"Archive ({ext})", SongHealthStatus.CannotRepair, "Can't open the archive: " + ex.Message);
        }

        using (archive)
        {
            var format = archive.FormatName;
            var usual = UsualExtensions.TryGetValue(format, out var exts) && exts.Contains(ext);
            string kind = usual ? $"MP3+G {format}" : $"MP3+G {format} ({ext})";
            string readOnly = $"{format} archives are read-only here (only ZIPs are rewritten); the converter can still use it.";

            if (archive.IsEncrypted)
                return Result(path, kind, SongHealthStatus.CannotRepair, "The archive is password-protected.");

            var songs = archive.FindSongs();

            if (archive.IsRecovered)
            {
                // The zip's index is damaged but its files could be read one by one.
                bool lost = archive.RecoveryNote!.Contains("lost", StringComparison.Ordinal);
                var details = new List<string> { archive.RecoveryNote };
                details.AddRange(archive.Entries.Select(e => $"recovered: {e.FullName} ({e.Size:N0} bytes)"));
                return lost || songs.Count == 0
                    ? new SongHealth(path, kind, SongHealthStatus.CannotRepair,
                        "Damaged zip: part of its contents is cut off and lost." +
                        (songs.Count > 0 ? " Fix isn't offered; the recovered files are listed in the details." : ""), details)
                    : new SongHealth(path, kind, SongHealthStatus.Repairable,
                        $"Damaged zip index (often a cut-off copy or download); all {archive.Entries.Count} files were recovered. Fix rebuilds the zip.", details);
            }

            if (songs.Count == 0)
                return Result(path, $"{format} ({ext})", SongHealthStatus.CannotRepair, "The archive contains no karaoke files.");

            if (songs.Count > 1)
            {
                var details = songs.Select(sg =>
                    $"{sg.Name}: " + (sg.Cdg == null ? "audio only" : sg.Audio == null ? "graphics only" :
                        CdgInspector.Inspect(sg.Cdg.ReadAllBytes()).Summary)).ToList();
                return archive.CanRewrite
                    ? new SongHealth(path, kind, SongHealthStatus.Repairable,
                        $"Contains {songs.Count} songs (one per zip expected). Fix splits it into {songs.Count} zips.", details)
                    : new SongHealth(path, kind, SongHealthStatus.CannotRepair,
                        $"Contains {songs.Count} songs (one per archive expected). {readOnly}", details);
            }

            var song = songs[0];
            bool hasAudio = song.Audio is { Size: > 0 };
            bool hasCdg = song.Cdg is { Size: > 0 };
            if (!hasCdg)
                return Result(path, kind, SongHealthStatus.OnlyMusic, "The archive has audio but no .cdg graphics (music only).");
            if (!hasAudio)
                return Result(path, kind, SongHealthStatus.OnlyLyrics,
                    (song.Audio == null ? "The archive has no audio file" : "The audio file inside the archive is empty") + " (lyrics only).");

            var health = FromCdgData(path, kind, song.Cdg!.ReadAllBytes(), () => (song.Audio!.Name, song.Audio.ReadAllBytes()));
            if (health.Status == SongHealthStatus.Repairable && !archive.CanRewrite)
                health = health with { Status = SongHealthStatus.CannotRepair, Summary = health.Summary + " " + readOnly };
            return health;
        }
    }

    /// <param name="loadAudio">
    /// Reads the song's audio; only called when the .cdg has no error-correction data (the extra checks
    /// that need the audio are skipped for verifiable songs, to keep scans fast).
    /// </param>
    private static SongHealth FromCdgData(string path, string kind, byte[] data, Func<(string Name, byte[] Data)> loadAudio)
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

        if (!report.HasParity) return CheckWithoutParity(path, kind, report, details, loadAudio);

        bool graphicsDamaged = report.Repairable > 0 || report.Unrepairable > 0;
        if (!graphicsDamaged)
        {
            return report.TrailingBytes > 0
                ? new SongHealth(path, kind, SongHealthStatus.Repairable, "File ends mid-packet.", details)
                : new SongHealth(path, kind, SongHealthStatus.NoIssues, report.Summary, details);
        }

        // The disc was damaged (scratches, or a drive that reads the CD+G subcode badly): check the audio too.
        int damagedPackets = report.Repairable + report.Unrepairable;
        var audioProblems = CheckAudio(report, details, loadAudio);
        if (audioProblems.Count > 0)
            return new SongHealth(path, kind, SongHealthStatus.CannotRepair,
                $"{damagedPackets:N0} damaged graphics packets, and the audio is damaged too: " + string.Join("; ", audioProblems) + ".", details);

        string rebuilt = report.Recovered > 0
            ? $", {report.Recovered:N0} more rebuilt from twin packets"
            : "";
        if (report.Unrepairable == 0)
            return new SongHealth(path, kind, SongHealthStatus.Repairable,
                $"{report.Repairable:N0} damaged packet{(report.Repairable == 1 ? "" : "s")} (all repairable{rebuilt})" +
                (report.TrailingBytes > 0 ? "; file ends mid-packet" : "") + ".", details);

        // Some packets are beyond repair. If they are only a small share, the repaired song is still usable:
        // the lost packets are left blank (small gaps in the graphics).
        double lost = (double)report.Unrepairable / Math.Max(1, report.GraphicsPackets + report.Unrepairable);
        details.Add($"{report.Unrepairable:N0} of {report.GraphicsPackets + report.Unrepairable:N0} graphics packets can't be repaired ({lost:P2}).");
        if (lost <= MaxUnrepairableShare)
            return new SongHealth(path, kind, SongHealthStatus.Repairable,
                $"Partly repairable: {report.Repairable:N0} damaged packets can be repaired{rebuilt}; {report.Unrepairable:N0} ({lost:P2}) can't " +
                "and will be left blank (small gaps in the graphics). The audio is intact.", details);
        return new SongHealth(path, kind, SongHealthStatus.CannotRepair,
            $"{report.Unrepairable:N0} graphics packets ({lost:P1}) are too damaged to repair" +
            (report.Repairable > 0 ? $" ({report.Repairable:N0} others repairable)" : "") + ".", details);
    }

    /// <summary>
    /// Largest share of a .cdg's graphics packets that may be beyond repair for the song to still count as
    /// "Bad but repairable" (the lost packets are left blank). Default 2%.
    /// </summary>
    public static double MaxUnrepairableShare { get; set; } = 0.02;

    /// <summary>
    /// Checks the song's audio: MP3 frames intact, and the same length as the graphics. Adds a detail line
    /// and returns the problems found (empty when the audio is fine or isn't an MP3, which can't be checked).
    /// </summary>
    private static List<string> CheckAudio(CdgReport report, List<string> details, Func<(string Name, byte[] Data)> loadAudio)
    {
        var problems = new List<string>();
        var (audioName, audio) = loadAudio();
        if (!audioName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            details.Add($"Audio ({System.IO.Path.GetExtension(audioName)}) not checked: only MP3 audio can be checked.");
            return problems;
        }

        var mp3 = Audio.Mp3Inspector.Inspect(audio);
        details.Add($"MP3: {mp3.Frames:N0} frames, {mp3.Duration:m\\:ss\\.f}" +
                    (mp3.DamagedBytes > 0 ? $", {mp3.DamagedBytes:N0} bytes outside valid frames" : ""));
        if (mp3.Problem != null)
        {
            problems.Add("MP3: " + mp3.Problem);
            return problems;
        }

        var diff = report.Duration - mp3.Duration;
        if (diff.Duration() > LengthTolerance(report.Duration > mp3.Duration ? report.Duration : mp3.Duration))
        {
            problems.Add(diff > TimeSpan.Zero
                ? $"the MP3 is {diff.TotalSeconds:F0} s shorter than the graphics ({mp3.Duration:m\\:ss} vs {report.Duration:m\\:ss}): the audio is probably cut off"
                : $"the graphics are {(-diff).TotalSeconds:F0} s shorter than the MP3 ({report.Duration:m\\:ss} vs {mp3.Duration:m\\:ss}): the .cdg is probably cut off");
        }
        return problems;
    }

    /// <summary>Allowed difference between the .cdg and MP3 lengths before one of them counts as cut off.</summary>
    private static TimeSpan LengthTolerance(TimeSpan length) =>
        TimeSpan.FromSeconds(Math.Max(3, length.TotalSeconds * 0.02));

    /// <summary>
    /// For a .cdg without error-correction data: check what can be checked (graphics instructions, the MP3's
    /// frames, and that both files are the same length). Failing any check means the song is damaged in a
    /// way that can't be repaired; passing them all still isn't proof, so it goes to "Can't verify".
    /// </summary>
    private static SongHealth CheckWithoutParity(string path, string kind, CdgReport report, List<string> details,
        Func<(string Name, byte[] Data)> loadAudio)
    {
        var problems = new List<string>();

        // 1. Graphics: instructions that don't exist, or tiles drawn off the screen. (Filler packets are normal.)
        if (report.UnknownInstructions > 0)
            problems.Add($"{report.UnknownInstructions:N0} invalid graphics instruction{(report.UnknownInstructions == 1 ? "" : "s")}");
        if (report.OffScreenTiles > 0)
            problems.Add($"{report.OffScreenTiles:N0} graphics tile{(report.OffScreenTiles == 1 ? "" : "s")} drawn off-screen");

        // 2. Audio: every MP3 frame present and intact, and the same length as the graphics.
        problems.AddRange(CheckAudio(report, details, loadAudio));

        if (problems.Count > 0)
            return new SongHealth(path, kind, SongHealthStatus.CannotRepair,
                "No error-correction data, and " + string.Join("; ", problems) + ".", details);

        if (report.TrailingBytes > 0)
            return new SongHealth(path, kind, SongHealthStatus.Repairable, "File ends mid-packet.", details);

        details.Add("Without error-correction data, bit errors inside the graphics can't be detected.");
        return new SongHealth(path, kind, SongHealthStatus.CannotVerify,
            "No error-correction data, so graphics damage can't be ruled out. Graphics instructions, MP3 audio and lengths all check out." +
            (report.FillerPackets > 0 ? $" ({report.FillerPackets:N0} filler packets, normal.)" : ""), details);
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

    /// <summary>
    /// Rewrites a single-song zip with its .cdg repaired, keeping every other file (and the file dates).
    /// The new zip uses standard Deflate compression.
    /// </summary>
    private static void RebuildZip(string zipPath, bool keepBackup)
    {
        var temp = zipPath + ".repairing";
        try
        {
            using (var archive = SongArchive.Open(zipPath))
            using (var target = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var cdg = archive.FindSongs()[0].Cdg;
                foreach (var entry in archive.Entries)
                {
                    byte[]? replacement = null;
                    if (entry == cdg)
                    {
                        replacement = entry.ReadAllBytes();
                        CdgInspector.RepairInPlace(replacement);
                        replacement = WholePackets(replacement);
                    }
                    CopyEntry(entry, target, replacement, keepFolders: true);
                }
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
            using (var source = SongArchive.Open(zipPath))
            {
                foreach (var song in source.FindSongs())
                {
                    var temp = System.IO.Path.Combine(dir, $".split-{Guid.NewGuid():N}{ext}");
                    using (var target = ZipFile.Open(temp, ZipArchiveMode.Create))
                    {
                        if (song.Audio != null) CopyEntry(song.Audio, target, null, keepFolders: false);
                        if (song.Cdg != null)
                        {
                            var data = song.Cdg.ReadAllBytes();
                            CdgInspector.RepairInPlace(data);
                            CopyEntry(song.Cdg, target, WholePackets(data), keepFolders: false);
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

    private static void CopyEntry(SongArchiveEntry source, ZipArchive target, byte[]? replacement, bool keepFolders)
    {
        var entry = target.CreateEntry(keepFolders ? source.FullName : source.Name, CompressionLevel.Optimal);
        if (source.LastModified is { } modified && modified.Year >= 1980) entry.LastWriteTime = modified;
        using var output = entry.Open();
        output.Write(replacement ?? source.ReadAllBytes());
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

    private static SongHealth Result(string path, string kind, SongHealthStatus status, string summary) =>
        new(path, kind, status, summary, Array.Empty<string>());

    private static string KindOf(string path) => Ext(path) switch
    {
        ".cdg" => "MP3+G",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".tgz" => "MP3+G archive",
        ".kar" => "KAR",
        ".mid" or ".midi" => "MIDI",
        _ => "Audio",
    };

    private static string Ext(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant();
}
