using System.IO.Compression;
using KaraokeToMp4.Cdg;
using KaraokeToMp4.Health;

namespace KaraokeToMp4.Tests;

public class SongHealthCheckerTests
{
    /// <summary>A short CDG with valid error-correction data on every packet, like a good rip.</summary>
    private static byte[] CdgWithParity(int seconds = 2)
    {
        var data = TestFiles.SimpleCdg(seconds);
        for (int i = 0; i < data.Length; i += 24) CdgParity.ComputeParity(data.AsSpan(i, 24));
        return data;
    }

    private static void Damage(byte[] cdg, int packet, int symbols)
    {
        int[] positions = { 5, 9, 13, 17, 21 };
        for (int k = 0; k < symbols; k++) cdg[packet * 24 + positions[k]] ^= 0x15;
    }

    /// <summary>A .cdg plus an MP3 of the same length (2 s), or an empty MP3 when audioBytes is 0.</summary>
    private static string Pair(TempDir dir, string name, byte[] cdg, int audioBytes = -1)
    {
        File.WriteAllBytes(dir.File(name + ".mp3"), audioBytes < 0 ? Mp3InspectorTests.Mp3(2) : new byte[audioBytes]);
        return TestFiles.WriteTemp(dir.Path, name + ".cdg", cdg);
    }

    [Fact]
    public void GoodCdg_NoIssues()
    {
        using var dir = new TempDir();
        Assert.Equal(SongHealthStatus.NoIssues, SongHealthChecker.Check(Pair(dir, "good", CdgWithParity())).Status);
    }

    [Fact]
    public void DamagedCdg_IsRepairable_AndRepairFixesItWithBackup()
    {
        using var dir = new TempDir();
        var cdg = CdgWithParity();
        var original = (byte[])cdg.Clone();
        Damage(cdg, 10, 1);
        Damage(cdg, 50, 2);
        var path = Pair(dir, "song", cdg);
        var damagedBytes = File.ReadAllBytes(path);

        var before = SongHealthChecker.Check(path);
        Assert.Equal(SongHealthStatus.Repairable, before.Status);
        Assert.Contains("2 damaged", before.Summary);

        var after = SongHealthChecker.Repair(path);
        Assert.Equal(SongHealthStatus.NoIssues, after.Status);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(damagedBytes, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void BadlyDamagedCdg_CannotRepair_AndRepairLeavesItAlone()
    {
        using var dir = new TempDir();
        var cdg = CdgWithParity();
        Damage(cdg, 10, 4);
        var path = Pair(dir, "song", cdg);
        var bytes = File.ReadAllBytes(path);

        Assert.Equal(SongHealthStatus.CannotRepair, SongHealthChecker.Check(path).Status);
        Assert.Equal(SongHealthStatus.CannotRepair, SongHealthChecker.Repair(path).Status);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void MissingOrEmptyAudio_IsOnlyLyrics()
    {
        using var dir = new TempDir();
        var lone = TestFiles.WriteTemp(dir.Path, "lone.cdg", CdgWithParity());
        Assert.Equal(SongHealthStatus.OnlyLyrics, SongHealthChecker.Check(lone).Status);
        var empty = Pair(dir, "empty", CdgWithParity(), audioBytes: 0);
        Assert.Equal(SongHealthStatus.OnlyLyrics, SongHealthChecker.Check(empty).Status);
    }

    [Fact]
    public void EmptyCdg_IsOnlyMusic()
    {
        using var dir = new TempDir();
        var path = Pair(dir, "song", Array.Empty<byte>());
        Assert.Equal(SongHealthStatus.OnlyMusic, SongHealthChecker.Check(path).Status);
    }

    [Fact]
    public void Zips_OnlyMusic_OnlyLyrics_AndJunk()
    {
        using var dir = new TempDir();
        string MakeZip(string name, bool audio, bool cdg)
        {
            var path = dir.File(name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            if (audio) using (var s = zip.CreateEntry("Song.mp3").Open()) s.Write(new byte[100]);
            if (cdg) using (var s = zip.CreateEntry("Song.cdg").Open()) s.Write(CdgWithParity());
            using (var s = zip.CreateEntry("readme.txt").Open()) s.Write("x"u8);
            return path;
        }
        Assert.Equal(SongHealthStatus.OnlyMusic, SongHealthChecker.Check(MakeZip("music.zip", true, false)).Status);
        Assert.Equal(SongHealthStatus.OnlyLyrics, SongHealthChecker.Check(MakeZip("lyrics.zip", false, true)).Status);
        Assert.Equal(SongHealthStatus.CannotRepair, SongHealthChecker.Check(MakeZip("junk.zip", false, false)).Status);
        Assert.Equal(SongHealthStatus.NoIssues, SongHealthChecker.Check(MakeZip("good.zip", true, true)).Status);
    }

    [Fact]
    public void LyricsWithoutNotes_IsOnlyLyrics()
    {
        // One track: a few lyric events and nothing else.
        var track = new List<byte>();
        foreach (var word in new[] { "Hel", "lo ", "world" })
        {
            track.AddRange(new byte[] { 0x83, 0x60, 0xFF, 0x05, (byte)word.Length });
            track.AddRange(System.Text.Encoding.ASCII.GetBytes(word));
        }
        track.AddRange(new byte[] { 0x00, 0xFF, 0x2F, 0x00 });
        var file = new List<byte>("MThd"u8.ToArray());
        file.AddRange(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xE0 });
        file.AddRange("MTrk"u8.ToArray());
        file.AddRange(new[] { (byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count });
        file.AddRange(track);

        using var dir = new TempDir();
        var path = TestFiles.WriteTemp(dir.Path, "words.mid", file.ToArray());
        Assert.Equal(SongHealthStatus.OnlyLyrics, SongHealthChecker.Check(path).Status);
    }

    [Fact]
    public void ZipWithDamagedCdg_IsRepairedInsideTheZip()
    {
        using var dir = new TempDir();
        var cdg = CdgWithParity();
        var original = (byte[])cdg.Clone();
        Damage(cdg, 7, 1);
        var zipPath = dir.File("bundle.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("Song.mp3").Open()) s.Write(Mp3InspectorTests.Mp3(2));
            using (var s = zip.CreateEntry("Song.cdg").Open()) s.Write(cdg);
        }

        Assert.Equal(SongHealthStatus.Repairable, SongHealthChecker.Check(zipPath).Status);
        Assert.Equal(SongHealthStatus.NoIssues, SongHealthChecker.Repair(zipPath).Status);
        Assert.True(File.Exists(zipPath + ".bak"));

        using var fixedZip = ZipFile.OpenRead(zipPath);
        using var ms = new MemoryStream();
        using (var s = fixedZip.GetEntry("Song.cdg")!.Open()) s.CopyTo(ms);
        Assert.Equal(original, ms.ToArray());
        Assert.NotNull(fixedZip.GetEntry("Song.mp3"));
    }

    [Fact]
    public void Midi_Classification()
    {
        using var dir = new TempDir();
        var good = TestFiles.WriteTemp(dir.Path, "good.kar", TestFiles.Kar("\\Hel", "lo"));
        var data = TestFiles.Kar("\\Hel", "lo", "/world");
        var cut = TestFiles.WriteTemp(dir.Path, "cut.kar", data[..^20]);
        var empty = TestFiles.WriteTemp(dir.Path, "empty.kar", Array.Empty<byte>());
        var plain = TestFiles.WriteTemp(dir.Path, "plain.mid", TestFiles.MidiWithLyricsAndHeader(Array.Empty<string>(), "Tune"));

        Assert.Equal(SongHealthStatus.NoIssues, SongHealthChecker.Check(good).Status);
        Assert.Equal(SongHealthStatus.CannotRepair, SongHealthChecker.Check(cut).Status);
        Assert.Contains("cut off", SongHealthChecker.Check(cut).Summary);
        Assert.Contains("empty", SongHealthChecker.Check(empty).Summary);
        Assert.Contains("no lyrics", SongHealthChecker.Check(plain).Summary);
        Assert.Equal(SongHealthStatus.OnlyMusic, SongHealthChecker.Check(plain).Status);
    }

    [Fact]
    public void MalformedMidi_IsRepairable_AndRepairKeepsLyrics()
    {
        using var dir = new TempDir();
        // Text event with a too-small length byte, leaving junk in the track (like real Soft Karaoke files).
        var kar = TestFiles.Kar("\\Hel", "lo ", "world");
        int at = kar.AsSpan().IndexOf("@TTest Song"u8) - 1;   // the length byte of that text event
        kar[at] = 3;                                           // "@TT" only; "est Song" becomes junk
        var path = TestFiles.WriteTemp(dir.Path, "song.kar", kar);
        var lyricsBefore = KaraokeToMp4.Midi.MidiKaraokeConverter.ReadLyrics(path).Lines.Select(l => l.Text).ToList();

        Assert.Equal(SongHealthStatus.Repairable, SongHealthChecker.Check(path).Status);
        Assert.Equal(SongHealthStatus.NoIssues, SongHealthChecker.Repair(path).Status);
        Assert.Equal(lyricsBefore, KaraokeToMp4.Midi.MidiKaraokeConverter.ReadLyrics(path).Lines.Select(l => l.Text).ToList());
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void FindSongs_ListsKaraokeFilesAndEveryAudioFileWithoutLyrics()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("sub"));
        Pair(dir, "a", CdgWithParity());
        File.WriteAllBytes(dir.File("orphan.mp3"), new byte[10]);              // no .cdg
        TestFiles.WriteTemp(dir.File("sub"), "b.kar", TestFiles.Kar("x"));
        File.WriteAllBytes(Path.Combine(dir.File("sub"), "music.mp3"), new byte[10]); // also flagged, even with no .cdg files nearby

        var names = SongHealthChecker.FindSongs(new[] { dir.Path }).Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "a.cdg", "orphan.mp3", "b.kar", "music.mp3" }.OrderBy(x => x), names.OrderBy(x => x));
        Assert.DoesNotContain("a.mp3", names); // audio that has its .cdg is part of that song, not listed separately

        var top = SongHealthChecker.FindSongs(new[] { dir.Path }, includeSubfolders: false).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain("b.kar", top);
        Assert.Equal(SongHealthStatus.OnlyMusic, SongHealthChecker.Check(dir.File("orphan.mp3")).Status);
    }

    [Fact]
    public void BackupNames_DoNotOverwriteEarlierBackups()
    {
        using var dir = new TempDir();
        var p = dir.File("x.cdg");
        Assert.Equal(p + ".bak", SongHealthChecker.NextBackupName(p));
        File.WriteAllText(p + ".bak", "");
        Assert.Equal(p + ".bak2", SongHealthChecker.NextBackupName(p));
    }
}
