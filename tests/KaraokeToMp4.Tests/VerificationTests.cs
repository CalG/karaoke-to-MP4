using KaraokeToMp4.Audio;
using KaraokeToMp4.Health;

namespace KaraokeToMp4.Tests;

public class Mp3InspectorTests
{
    /// <summary>MPEG-1 layer III, 128 kbit/s, 44.1 kHz: 417-byte frames of 1152 samples (26.1 ms).</summary>
    internal static byte[] Frames(int count)
    {
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        var rnd = new Random(count);
        rnd.NextBytes(frame.AsSpan(4));
        for (int i = 4; i < frame.Length; i++) if (frame[i] == 0xFF) frame[i] = 0x7F; // no false syncs inside
        return Enumerable.Repeat(frame, count).SelectMany(f => f).ToArray();
    }

    internal static byte[] Mp3(double seconds) => Frames((int)Math.Round(seconds * 44100 / 1152));

    [Fact]
    public void MeasuresLength()
    {
        var r = Mp3Inspector.Inspect(Frames(1000));
        Assert.Equal(1000, r.Frames);
        Assert.Equal(1000 * 1152 / 44100.0, r.Duration.TotalSeconds, 3);
        Assert.Null(r.Problem);
    }

    [Fact]
    public void SkipsTags()
    {
        var id3 = new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 20 }.Concat(new byte[20]);
        var v1 = "TAG"u8.ToArray().Concat(new byte[125]);
        var r = Mp3Inspector.Inspect(id3.Concat(Frames(100)).Concat(v1).ToArray());
        Assert.Equal(100, r.Frames);
        Assert.Equal(0, r.DamagedBytes);
    }

    [Fact]
    public void FindsDamagedSections()
    {
        var good = Frames(400);
        var junk = Enumerable.Repeat((byte)0x55, 20_000).ToArray();
        var r = Mp3Inspector.Inspect(good[..(417 * 200)].Concat(junk).Concat(good[(417 * 200)..]).ToArray());
        Assert.Equal(400, r.Frames);
        Assert.True(r.DamagedBytes >= 20_000);
        Assert.NotNull(r.Problem);
    }

    [Fact]
    public void ToleratesACutOffLastFrame()
    {
        var data = Frames(100);
        var r = Mp3Inspector.Inspect(data[..^200]);
        Assert.Equal(99, r.Frames);
        Assert.True(r.EndsMidFrame);
        Assert.Null(r.Problem);
    }

    [Fact]
    public void NoFrames_IsAProblem()
    {
        Assert.Equal("no MP3 audio frames found", Mp3Inspector.Inspect(new byte[5000]).Problem);
    }
}

/// <summary>MP3+G songs whose .cdg has no error-correction data (like some Sound Choice rips).</summary>
public class CannotVerifyTests
{
    private static string Pair(TempDir dir, byte[] cdg, byte[] mp3)
    {
        File.WriteAllBytes(dir.File("Song.mp3"), mp3);
        return TestFiles.WriteTemp(dir.Path, "Song.cdg", cdg);
    }

    [Fact]
    public void EverythingChecksOut_GoesToCannotVerify()
    {
        using var dir = new TempDir();
        var health = SongHealthChecker.Check(Pair(dir, TestFiles.SimpleCdg(20), Mp3InspectorTests.Mp3(20)));
        Assert.Equal(SongHealthStatus.CannotVerify, health.Status);
        Assert.Contains(health.Details, d => d.StartsWith("MP3:"));
    }

    [Fact]
    public void AudioMuchShorterThanGraphics_CannotRepair()
    {
        using var dir = new TempDir();
        var health = SongHealthChecker.Check(Pair(dir, TestFiles.SimpleCdg(60), Mp3InspectorTests.Mp3(30)));
        Assert.Equal(SongHealthStatus.CannotRepair, health.Status);
        Assert.Contains("audio is probably cut off", health.Summary);
    }

    [Fact]
    public void GraphicsMuchShorterThanAudio_CannotRepair()
    {
        using var dir = new TempDir();
        var health = SongHealthChecker.Check(Pair(dir, TestFiles.SimpleCdg(30), Mp3InspectorTests.Mp3(60)));
        Assert.Equal(SongHealthStatus.CannotRepair, health.Status);
        Assert.Contains(".cdg is probably cut off", health.Summary);
    }

    [Fact]
    public void SmallLengthDifference_IsFine()
    {
        using var dir = new TempDir();
        var health = SongHealthChecker.Check(Pair(dir, TestFiles.SimpleCdg(60), Mp3InspectorTests.Mp3(61.5)));
        Assert.Equal(SongHealthStatus.CannotVerify, health.Status);
    }

    [Fact]
    public void DamagedAudio_CannotRepair()
    {
        using var dir = new TempDir();
        var mp3 = Mp3InspectorTests.Mp3(20);
        var junk = Enumerable.Repeat((byte)0x55, 30_000).ToArray();
        var broken = mp3[..(417 * 300)].Concat(junk).Concat(mp3[(417 * 300)..]).ToArray();
        var health = SongHealthChecker.Check(Pair(dir, TestFiles.SimpleCdg(21), broken));
        Assert.Equal(SongHealthStatus.CannotRepair, health.Status);
        Assert.Contains("damaged section", health.Summary);
    }

    [Fact]
    public void InvalidGraphicsInstructions_CannotRepair()
    {
        using var dir = new TempDir();
        var cdg = TestFiles.SimpleCdg(20);
        for (int p = 1000; p < 1010; p++) { cdg[p * 24] = 0x09; cdg[p * 24 + 1] = 0x33; cdg[p * 24 + 4] = 1; } // not a CD+G instruction
        var health = SongHealthChecker.Check(Pair(dir, cdg, Mp3InspectorTests.Mp3(20)));
        Assert.Equal(SongHealthStatus.CannotRepair, health.Status);
        Assert.Contains("invalid graphics instruction", health.Summary);
    }

    [Fact]
    public void SongsWithErrorCorrectionData_AreUnaffected()
    {
        using var dir = new TempDir();
        var cdg = TestFiles.SimpleCdg(20);
        for (int i = 0; i < cdg.Length; i += 24) KaraokeToMp4.Cdg.CdgParity.ComputeParity(cdg.AsSpan(i, 24));
        // Even with an MP3 that isn't checked (placeholder bytes), a verifiable .cdg means "No issues".
        Assert.Equal(SongHealthStatus.NoIssues, SongHealthChecker.Check(Pair(dir, cdg, new byte[100])).Status);
    }
}
