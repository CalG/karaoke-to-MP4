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

/// <summary>
/// Scratched-disc rips (or drives that read the CD+G subcode badly): damaged graphics are only worth
/// repairing when the audio is intact.
/// </summary>
public class PartlyRepairableTests
{
    /// <summary>A 60 s .cdg with error-correction data and a tile drawn every 0.1 s (about 600 graphics packets).</summary>
    private static byte[] Cdg()
    {
        var data = TestFiles.SimpleCdg(60);
        for (int p = 3; p < 60 * 300; p += 30)
        {
            var tile = TestFiles.Tile(0, 1, 4, 5, 0x2A);
            tile.CopyTo(data, p * 24);
        }
        for (int i = 0; i < data.Length; i += 24) KaraokeToMp4.Cdg.CdgParity.ComputeParity(data.AsSpan(i, 24));
        return data;
    }

    private static void Damage(byte[] cdg, IEnumerable<int> packets, int symbols)
    {
        int[] positions = { 5, 9, 13, 17, 21 };
        foreach (var p in packets)
            for (int k = 0; k < symbols; k++) cdg[p * 24 + positions[k]] ^= 0x15;
    }

    private static string Pair(TempDir dir, byte[] cdg, byte[] mp3)
    {
        File.WriteAllBytes(dir.File("Song.mp3"), mp3);
        return TestFiles.WriteTemp(dir.Path, "Song.cdg", cdg);
    }

    [Fact]
    public void FewUnrepairablePackets_GoodAudio_IsRepairable_AndFixLeavesThemBlank()
    {
        using var dir = new TempDir();
        var cdg = Cdg();
        Damage(cdg, Enumerable.Range(0, 40).Select(i => 3 + i * 30), 1);  // 40 repairable
        Damage(cdg, new[] { 2403, 4803 }, 4);                              // 2 beyond repair (<2%)
        var path = Pair(dir, cdg, Mp3InspectorTests.Mp3(60));

        var health = SongHealthChecker.Check(path);
        Assert.Equal(SongHealthStatus.Repairable, health.Status);
        Assert.StartsWith("Partly repairable", health.Summary);

        Assert.Equal(SongHealthStatus.NoIssues, SongHealthChecker.Repair(path).Status);
        var fixedCdg = File.ReadAllBytes(path);
        Assert.All(fixedCdg.AsSpan(2403 * 24, 24).ToArray(), b => Assert.Equal(0, b)); // left blank
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void DamagedGraphics_AndDamagedAudio_CannotRepair()
    {
        using var dir = new TempDir();
        var cdg = Cdg();
        Damage(cdg, new[] { 33, 63 }, 1); // even fully repairable graphics...
        var mp3 = Mp3InspectorTests.Mp3(60);
        var junk = Enumerable.Repeat((byte)0x55, 30_000).ToArray();
        var brokenMp3 = mp3[..(417 * 800)].Concat(junk).Concat(mp3[(417 * 800)..]).ToArray();

        var health = SongHealthChecker.Check(Pair(dir, cdg, brokenMp3)); // ...with damaged audio is not worth fixing
        Assert.Equal(SongHealthStatus.CannotRepair, health.Status);
        Assert.Contains("audio is damaged too", health.Summary);
    }

    [Fact]
    public void DamagedGraphics_AndCutOffAudio_CannotRepair()
    {
        using var dir = new TempDir();
        var cdg = Cdg();
        Damage(cdg, new[] { 33 }, 1);
        var health = SongHealthChecker.Check(Pair(dir, cdg, Mp3InspectorTests.Mp3(30)));
        Assert.Equal(SongHealthStatus.CannotRepair, health.Status);
        Assert.Contains("cut off", health.Summary);
    }

    [Fact]
    public void TooManyUnrepairablePackets_CannotRepair()
    {
        using var dir = new TempDir();
        var cdg = Cdg();
        Damage(cdg, Enumerable.Range(0, 60).Select(i => 3 + i * 30), 4); // ~10% of the graphics lost
        var health = SongHealthChecker.Check(Pair(dir, cdg, Mp3InspectorTests.Mp3(60)));
        Assert.Equal(SongHealthStatus.CannotRepair, health.Status);
        Assert.Contains("too damaged to repair", health.Summary);
    }

    [Fact]
    public void UndamagedGraphics_DoNotReadTheAudio()
    {
        using var dir = new TempDir();
        // A placeholder "MP3" is never looked at when the graphics are fine (keeps scans fast).
        Assert.Equal(SongHealthStatus.NoIssues, SongHealthChecker.Check(Pair(dir, Cdg(), new byte[10])).Status);
    }
}

/// <summary>Rips with only the short Q check data, and zeroed rips that show stray bit errors.</summary>
public class PartialCheckDataTests
{
    /// <summary>A 20 s song whose packets carry Q check data but zeroed P check data.</summary>
    private static byte[] QOnlyCdg()
    {
        var data = TestFiles.SimpleCdg(20);
        for (int i = 0; i < data.Length; i += 24)
        {
            KaraokeToMp4.Cdg.CdgParity.ComputeParity(data.AsSpan(i, 24));
            data.AsSpan(i + 20, 4).Clear();
        }
        return data;
    }

    private static int FirstTile(byte[] cdg)
    {
        for (int i = 0; i < cdg.Length; i += 24) if (cdg[i] == 9 && cdg[i + 1] == 6) return i / 24;
        throw new InvalidOperationException();
    }

    private static string Pair(TempDir dir, byte[] cdg)
    {
        File.WriteAllBytes(dir.File("Song.mp3"), Mp3InspectorTests.Mp3(20));
        return TestFiles.WriteTemp(dir.Path, "Song.cdg", cdg);
    }

    [Fact]
    public void QOnlyRip_IsRecognised_AndCleanOneIsCannotVerify()
    {
        using var dir = new TempDir();
        var health = SongHealthChecker.Check(Pair(dir, QOnlyCdg()));
        Assert.Equal(SongHealthStatus.CannotVerify, health.Status);
        Assert.Contains("partial error-correction data", health.Summary);
    }

    [Fact]
    public void QOnlyRip_DamagedInstruction_IsRepairable_AndFixRestoresIt()
    {
        using var dir = new TempDir();
        var cdg = QOnlyCdg();
        int tile = FirstTile(cdg);
        cdg[tile * 24 + 1] ^= 0x20;                            // instruction 6 -> 38 (draws XOR instead of normal)
        var path = Pair(dir, cdg);

        var health = SongHealthChecker.Check(path);
        Assert.Equal(SongHealthStatus.Repairable, health.Status);
        Assert.Contains("damaged instruction", health.Summary);

        Assert.Equal(SongHealthStatus.CannotVerify, SongHealthChecker.Repair(path).Status);
        Assert.Equal(6, File.ReadAllBytes(path)[tile * 24 + 1] & 0x3F);
    }

    [Fact]
    public void QOnlyRip_InstructionDamagedBeyondRepair_CannotRepair()
    {
        using var dir = new TempDir();
        var cdg = QOnlyCdg();
        int tile = FirstTile(cdg);
        cdg[tile * 24 + 1] ^= 0x20;
        cdg[tile * 24 + 2] ^= 0x11;                            // two of the four Q symbols damaged
        var health = SongHealthChecker.Check(Pair(dir, cdg));
        Assert.Equal(SongHealthStatus.CannotRepair, health.Status);
        Assert.Contains("can't be repaired", health.Summary);
    }

    [Fact]
    public void ZeroedRip_WithStrayCheckBits_IsFlagged()
    {
        using var dir = new TempDir();
        var cdg = TestFiles.SimpleCdg(20);                     // check bytes all zero, like many rips
        int tile = FirstTile(cdg);
        cdg[tile * 24 + 21] = 0x04;                            // one flipped bit in an empty check byte
        var health = SongHealthChecker.Check(Pair(dir, cdg));
        Assert.Equal(SongHealthStatus.CannotVerify, health.Status);
        Assert.Contains("signs of bit errors (1 packet)", health.Summary);
        Assert.Contains(health.Details, d => d.StartsWith("Bit errors found: 1 packet has"));
    }

    [Fact]
    public void ZeroedRip_WithoutStrayBits_IsNotFlagged()
    {
        using var dir = new TempDir();
        var health = SongHealthChecker.Check(Pair(dir, TestFiles.SimpleCdg(20)));
        Assert.Equal(SongHealthStatus.CannotVerify, health.Status);
        Assert.DoesNotContain("bit errors", health.Summary);
    }
}
