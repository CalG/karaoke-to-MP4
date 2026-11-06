using System.Formats.Tar;
using System.IO.Compression;
using KaraokeToMp4.Archives;
using KaraokeToMp4.Health;

namespace KaraokeToMp4.Tests;

/// <summary>
/// Archives made with 7-Zip and tar (Fixtures folder, synthetic content). They cover formats and ZIP
/// compression methods that .NET's own System.IO.Compression can't read.
/// </summary>
public class SongArchiveTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string CopyFixture(TempDir dir, string name, string? as_ = null)
    {
        var path = dir.File(as_ ?? name);
        File.Copy(Fixture(name), path);
        return path;
    }

    [Theory]
    [InlineData("song.7z", "7-Zip")]
    [InlineData("song.tar.gz", "tar.gz")]
    [InlineData("song-lzma.zip", "ZIP")]
    [InlineData("song-bzip2.zip", "ZIP")]
    [InlineData("song-7z-content.mp3gz", "7-Zip")]
    public void ReadsEveryFormat(string fixture, string format)
    {
        using var archive = SongArchive.Open(Fixture(fixture));
        Assert.Equal(format, archive.FormatName);
        var song = Assert.Single(archive.FindSongs());
        Assert.Equal("Artist - Song", song.Name);
        Assert.Equal(2 * 300 * 24, song.Cdg!.ReadAllBytes().Length);
        Assert.Equal(4010, song.Audio!.ReadAllBytes().Length);
    }

    [Fact]
    public void LzmaZip_IsUnreadableForSystemIoCompression_ButReadableHere()
    {
        // This is why SharpCompress is used: 7-Zip can make .zip files .NET can list but not decompress.
        using (var zip = ZipFile.OpenRead(Fixture("song-lzma.zip")))
        {
            Assert.ThrowsAny<Exception>(() => { using var s = zip.Entries[0].Open(); s.ReadByte(); });
        }
        using var archive = SongArchive.Open(Fixture("song-lzma.zip"));
        Assert.NotEmpty(archive.FindSongs()[0].Cdg!.ReadAllBytes());
    }

    [Theory]
    [InlineData("song.7z", "MP3+G 7-Zip")]
    [InlineData("song.tar.gz", "MP3+G tar.gz")]
    [InlineData("song-lzma.zip", "MP3+G ZIP")]
    [InlineData("song-7z-content.mp3gz", "MP3+G 7-Zip (.mp3gz)")]
    public void Checker_AndConverter_AcceptEveryFormat(string fixture, string kind)
    {
        using var dir = new TempDir();
        var path = CopyFixture(dir, fixture);
        Assert.Contains(path, SongHealthChecker.FindSongs(new[] { dir.Path }));
        var health = SongHealthChecker.Check(path);
        Assert.Equal(SongHealthStatus.NoIssues, health.Status);
        Assert.Equal(kind, health.Kind);
        Assert.Equal(KaraokeFormat.Mp3Cdg, KaraokeConverter.DetectFormat(path));
    }

    [Fact]
    public void MultiSong7z_IsReadOnly_AndLeftUntouched()
    {
        using var dir = new TempDir();
        var path = CopyFixture(dir, "two-songs.7z");
        var before = File.ReadAllBytes(path);

        var health = SongHealthChecker.Check(path);
        Assert.Equal(SongHealthStatus.CannotRepair, health.Status);
        Assert.Contains("2 songs", health.Summary);
        Assert.Contains("read-only", health.Summary);

        SongHealthChecker.RepairAll(path);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void MultiSongLzmaZip_IsSplitIntoStandardZips()
    {
        using var dir = new TempDir();
        var path = CopyFixture(dir, "two-songs-lzma.zip");
        Assert.Equal(SongHealthStatus.Repairable, SongHealthChecker.Check(path).Status);

        var after = SongHealthChecker.RepairAll(path);
        Assert.Equal(new[] { "Song A.zip", "Song B.zip" }, after.Select(r => r.FileName).OrderBy(n => n));
        Assert.All(after, r => Assert.Equal(SongHealthStatus.NoIssues, r.Status));

        // The new zips use standard compression that System.IO.Compression (and every zip tool) can read.
        using var zip = ZipFile.OpenRead(dir.File("Song A.zip"));
        using var s = zip.GetEntry("Song A.cdg")!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.Equal(2 * 300 * 24, ms.Length);
    }

    [Fact]
    public void NotAnArchive_Throws()
    {
        using var dir = new TempDir();
        var path = TestFiles.WriteTemp(dir.Path, "x.7z", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Throws<InvalidDataException>(() => SongArchive.Open(path));
        Assert.Equal(SongHealthStatus.CannotRepair, SongHealthChecker.Check(path).Status);
    }
}

/// <summary>Real conversion from an archive (tar.gz built here with .NET's own TAR + GZip support).</summary>
[Collection("Tools")]
public class ArchiveConversionTests
{
    [Fact]
    public async Task TarGzSong_ConvertsToMp4()
    {
        bool hasFfmpeg;
        try { hasFfmpeg = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true })!.WaitForExit(10000); }
        catch { hasFfmpeg = false; }
        if (!hasFfmpeg) return;

        using var dir = new TempDir();
        var mp3 = dir.File("tone.mp3");
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
            $"-hide_banner -loglevel error -y -f lavfi -i sine=duration=2 \"{mp3}\"") { RedirectStandardError = true })!;
        p.WaitForExit();

        var tgz = dir.File("Song.tgz");
        using (var fs = File.Create(tgz))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gz))
        {
            tar.WriteEntry(mp3, "Song.mp3");
            var cdg = dir.File("Song.cdg");
            File.WriteAllBytes(cdg, TestFiles.SimpleCdg(2));
            tar.WriteEntry(cdg, "Song.cdg");
        }

        var output = dir.File("out.mp4");
        await new KaraokeConverter(EndToEndTests.SmallOptions()).ConvertAsync(tgz, output);
        Assert.True(new FileInfo(output).Length > 1000);
    }
}
