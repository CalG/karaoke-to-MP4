using System.IO.Compression;
using KaraokeToMp4.Cdg;
using KaraokeToMp4.Health;

namespace KaraokeToMp4.Tests;

public class ArchiveTests
{
    private static byte[] Cdg(bool damaged = false)
    {
        var data = TestFiles.SimpleCdg(1);
        for (int i = 0; i < data.Length; i += 24) CdgParity.ComputeParity(data.AsSpan(i, 24));
        if (damaged) data[24 * 5 + 9] ^= 0x11;
        return data;
    }

    private static string MakeZip(string path, params (string Name, byte[] Data)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
            using (var s = zip.CreateEntry(name).Open()) s.Write(data);
        return path;
    }

    private static List<string> EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.Name).OrderBy(n => n).ToList();
    }

    [Fact]
    public void Sniffer_RecognisesArchivesByContent()
    {
        Assert.Equal(ArchiveKind.Zip, ArchiveSniffer.Detect("PK\x03\x04rest"u8));
        Assert.Equal(ArchiveKind.GZip, ArchiveSniffer.Detect(new byte[] { 0x1F, 0x8B, 8, 0 }));
        Assert.Equal(ArchiveKind.SevenZip, ArchiveSniffer.Detect(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }));
        Assert.Equal(ArchiveKind.Rar, ArchiveSniffer.Detect("Rar!\x1A\x07"u8));
        Assert.Equal(ArchiveKind.None, ArchiveSniffer.Detect("ID3\x03"u8));
    }

    [Fact]
    public void ZipWithCustomExtension_IsFoundCheckedAndConvertible()
    {
        using var dir = new TempDir();
        var path = MakeZip(dir.File("Artist - Song.mp3gz"), ("Artist - Song.mp3", new byte[200]), ("Artist - Song.cdg", Cdg()));

        Assert.Contains(path, SongHealthChecker.FindSongs(new[] { dir.Path }));
        var health = SongHealthChecker.Check(path);
        Assert.Equal(SongHealthStatus.NoIssues, health.Status);
        Assert.Equal("MP3+G ZIP (.mp3gz)", health.Kind);
        Assert.Equal(KaraokeFormat.Mp3Cdg, KaraokeConverter.DetectFormat(path));
    }

    [Fact]
    public void OtherArchiveFormats_AreListedWithTheirExtension()
    {
        using var dir = new TempDir();
        var gz = dir.File("song.mp3gz");
        using (var fs = File.Create(gz))
        using (var g = new GZipStream(fs, CompressionLevel.Fastest))
            g.Write(new byte[100]);

        Assert.Contains(gz, SongHealthChecker.FindSongs(new[] { dir.Path }));
        var health = SongHealthChecker.Check(gz);
        Assert.Equal(SongHealthStatus.CannotRepair, health.Status);
        Assert.Equal("gzip (.mp3gz)", health.Kind);
    }

    [Fact]
    public void Backups_AreNotScanned()
    {
        using var dir = new TempDir();
        MakeZip(dir.File("a.zip.bak"), ("a.mp3", new byte[10]), ("a.cdg", Cdg()));
        MakeZip(dir.File("b.mp3gz.bak2"), ("b.mp3", new byte[10]), ("b.cdg", Cdg()));
        Assert.Empty(SongHealthChecker.FindSongs(new[] { dir.Path }));
    }

    [Fact]
    public void SinglePairWithDifferentNames_IsOneSong()
    {
        using var dir = new TempDir();
        var path = MakeZip(dir.File("x.zip"), ("track01.mp3", new byte[100]), ("Some Song.cdg", Cdg()));
        Assert.Equal(SongHealthStatus.NoIssues, SongHealthChecker.Check(path).Status);
    }

    [Fact]
    public void MultiSongZip_IsRepairable_AndFixSplitsIt()
    {
        using var dir = new TempDir();
        var path = MakeZip(dir.File("Disc 1.mp3gz"),
            ("Song A.mp3", new byte[100]), ("Song A.cdg", Cdg(damaged: true)),
            ("Song B.mp3", new byte[120]), ("Song B.cdg", Cdg()),
            ("readme.txt", "x"u8.ToArray()));

        var before = SongHealthChecker.Check(path);
        Assert.Equal(SongHealthStatus.Repairable, before.Status);
        Assert.Contains("2 songs", before.Summary);
        Assert.Equal(2, before.Details.Count);

        var after = SongHealthChecker.RepairAll(path);

        Assert.Equal(2, after.Count);
        Assert.All(after, r => Assert.Equal(SongHealthStatus.NoIssues, r.Status)); // Song A's damage repaired too
        Assert.Equal(new[] { "Song A.mp3gz", "Song B.mp3gz" }, after.Select(r => r.FileName).OrderBy(n => n));
        Assert.Equal(new[] { "Song A.cdg", "Song A.mp3" }, EntryNames(dir.File("Song A.mp3gz")));
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void SplittingAZipNamedAfterOneOfItsSongs_ReusesTheName()
    {
        using var dir = new TempDir();
        var path = MakeZip(dir.File("Song A.zip"),
            ("Song A.mp3", new byte[100]), ("Song A.cdg", Cdg()),
            ("Song B.mp3", new byte[100]), ("Song B.cdg", Cdg()));

        var after = SongHealthChecker.RepairAll(path, keepBackup: false);
        Assert.Equal(new[] { "Song A.zip", "Song B.zip" }, after.Select(r => r.FileName).OrderBy(n => n));
        Assert.Equal(new[] { "Song A.cdg", "Song A.mp3" }, EntryNames(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void MultiSongZip_WithLooseAudio_KeepsItAsItsOwnZip()
    {
        using var dir = new TempDir();
        var path = MakeZip(dir.File("mixed.zip"),
            ("A.mp3", new byte[100]), ("A.cdg", Cdg()), ("Bonus.mp3", new byte[100]));

        var after = SongHealthChecker.RepairAll(path);
        Assert.Contains(after, r => r.FileName == "A.zip" && r.Status == SongHealthStatus.NoIssues);
        Assert.Contains(after, r => r.FileName == "Bonus.zip" && r.Status == SongHealthStatus.OnlyMusic);
    }
}
