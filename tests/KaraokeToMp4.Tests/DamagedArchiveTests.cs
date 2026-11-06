using System.IO.Compression;
using KaraokeToMp4.Archives;
using KaraokeToMp4.Cdg;
using KaraokeToMp4.Health;

namespace KaraokeToMp4.Tests;

/// <summary>
/// Zips damaged the way real collections are: an interrupted copy/download cuts off the index (central
/// directory) at the end, which makes SharpCompress fail with "Failed to locate zip header".
/// </summary>
public class DamagedArchiveTests
{
    private static byte[] Cdg()
    {
        var data = TestFiles.SimpleCdg(1);
        for (int i = 0; i < data.Length; i += 24) CdgParity.ComputeParity(data.AsSpan(i, 24));
        return data;
    }

    private static byte[] ZipBytes()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Random (incompressible) audio, so offsets in the zip are predictable.
            var audio = new byte[5000];
            new Random(1).NextBytes(audio);
            using (var s = zip.CreateEntry("Song.mp3").Open()) s.Write(audio);
            using (var s = zip.CreateEntry("Song.cdg").Open()) s.Write(Cdg());
        }
        return ms.ToArray();
    }

    private static int CentralDirectoryStart(byte[] zip) => zip.AsSpan().IndexOf("PK\x01\x02"u8);

    [Fact]
    public void ZipWithoutIndex_IsRecovered_AndFixRebuildsIt()
    {
        using var dir = new TempDir();
        var zip = ZipBytes();
        var path = TestFiles.WriteTemp(dir.Path, "Song.zip", zip[..CentralDirectoryStart(zip)]);

        // The original failure: the normal archive reader can't find the index.
        Assert.ThrowsAny<Exception>(() =>
        {
            using var a = SharpCompress.Archives.ArchiveFactory.Open(path, new SharpCompress.Readers.ReaderOptions());
            _ = a.Entries.ToList();
        });

        using (var archive = SongArchive.Open(path))
        {
            Assert.True(archive.IsRecovered);
            Assert.Equal(new[] { "Song.cdg", "Song.mp3" }, archive.Entries.Select(e => e.Name).OrderBy(n => n));
            Assert.Equal(Cdg(), archive.FindSongs()[0].Cdg!.ReadAllBytes());
        }

        var before = SongHealthChecker.Check(path);
        Assert.Equal(SongHealthStatus.Repairable, before.Status);
        Assert.Contains("index", before.Summary);

        var after = SongHealthChecker.Repair(path);
        Assert.Equal(SongHealthStatus.NoIssues, after.Status);
        using var fixedZip = ZipFile.OpenRead(path); // a normal zip again
        Assert.Equal(2, fixedZip.Entries.Count);
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void ZipCutOffInsideASong_CannotRepair_AndDoesNotThrow()
    {
        using var dir = new TempDir();
        var zip = ZipBytes();
        // Cut inside the second file (the .cdg): the MP3 before it survives, the .cdg is lost.
        var path = TestFiles.WriteTemp(dir.Path, "Song.zip", zip[..(CentralDirectoryStart(zip) - 30)]);
        var health = SongHealthChecker.Check(path);
        Assert.Equal(SongHealthStatus.CannotRepair, health.Status);
        Assert.Contains("lost", health.Summary);
        Assert.Contains(health.Details, d => d.Contains("Song.cdg") && d.Contains("cut off"));
        Assert.Contains(health.Details, d => d.Contains("recovered: Song.mp3"));

        // Cut inside the first file: nothing survives, and the message says where it was cut.
        var early = TestFiles.WriteTemp(dir.Path, "Early.zip", zip[..200]);
        var earlyHealth = SongHealthChecker.Check(early);
        Assert.Equal(SongHealthStatus.CannotRepair, earlyHealth.Status);
        Assert.Contains("cut off inside 'Song.mp3'", earlyHealth.Summary);
    }

    [Fact]
    public void JunkThatLooksLikeAZip_CannotRepair_AndDoesNotThrow()
    {
        using var dir = new TempDir();
        var junk = "PK\x03\x04"u8.ToArray().Concat(Enumerable.Repeat((byte)0xEE, 500)).ToArray();
        var path = TestFiles.WriteTemp(dir.Path, "bad.zip", junk);

        Assert.Throws<InvalidDataException>(() => SongArchive.Open(path));
        Assert.Equal(SongHealthStatus.CannotRepair, SongHealthChecker.Check(path).Status);
        Assert.Equal(KaraokeFormat.Unknown, KaraokeConverter.DetectFormat(path));
    }

    [Fact]
    public void ScanOfAFolderWithDamagedZips_Completes()
    {
        using var dir = new TempDir();
        var zip = ZipBytes();
        TestFiles.WriteTemp(dir.Path, "a.zip", zip);
        TestFiles.WriteTemp(dir.Path, "b.zip", zip[..CentralDirectoryStart(zip)]);
        TestFiles.WriteTemp(dir.Path, "c.zip", zip[..100]);
        TestFiles.WriteTemp(dir.Path, "d.zip", Array.Empty<byte>());

        var results = SongHealthChecker.FindSongs(new[] { dir.Path }).Select(SongHealthChecker.Check).ToList();
        Assert.Equal(4, results.Count);
        Assert.Equal(SongHealthStatus.NoIssues, results.Single(r => r.FileName == "a.zip").Status);
        Assert.Equal(SongHealthStatus.Repairable, results.Single(r => r.FileName == "b.zip").Status);
        Assert.Equal(SongHealthStatus.CannotRepair, results.Single(r => r.FileName == "c.zip").Status);
        Assert.Equal(SongHealthStatus.CannotRepair, results.Single(r => r.FileName == "d.zip").Status);
    }
}
