using KaraokeToMp4.Cdg;

namespace KaraokeToMp4.Tests;

public class CdgParityTests
{
    private static byte[] RandomPacket(Random rnd)
    {
        var p = new byte[24];
        rnd.NextBytes(p);
        for (int i = 0; i < 24; i++) p[i] &= 0x3F;
        p[0] = 0x09;
        p[1] = 38;
        CdgParity.ComputeParity(p);
        return p;
    }

    [Fact]
    public void ComputedParity_IsValid()
    {
        var rnd = new Random(1);
        for (int n = 0; n < 200; n++) Assert.True(CdgParity.IsValid(RandomPacket(rnd)));
    }

    [Fact]
    public void SingleDamagedSymbol_IsRepairedExactly()
    {
        var rnd = new Random(2);
        for (int n = 0; n < 200; n++)
        {
            var good = RandomPacket(rnd);
            var bad = (byte[])good.Clone();
            int pos = rnd.Next(24);
            bad[pos] ^= (byte)(1 + rnd.Next(63));
            Assert.Equal(CdgPacketStatus.Repaired, CdgParity.Repair(bad));
            Assert.Equal(good, bad);
        }
    }

    [Fact]
    public void TwoDamagedSymbols_AreRepairedExactly()
    {
        var rnd = new Random(3);
        for (int n = 0; n < 200; n++)
        {
            var good = RandomPacket(rnd);
            var bad = (byte[])good.Clone();
            int a = rnd.Next(24), b;
            do b = rnd.Next(24); while (b == a);
            bad[a] ^= (byte)(1 + rnd.Next(63));
            bad[b] ^= (byte)(1 + rnd.Next(63));
            Assert.Equal(CdgPacketStatus.Repaired, CdgParity.Repair(bad));
            Assert.Equal(good, bad);
        }
    }

    [Fact]
    public void Repair_KeepsOtherSubcodeChannelBits()
    {
        var p = RandomPacket(new Random(4));
        p[5] |= 0xC0;                      // P/Q channel bits that aren't part of CD+G
        var expected = (byte[])p.Clone();
        p[5] ^= 0x07;                      // damage the CD+G symbol only
        Assert.Equal(CdgPacketStatus.Repaired, CdgParity.Repair(p));
        Assert.Equal(expected, p);
    }

    [Fact]
    public void Inspector_RepairsDamage_AndBlanksUnrepairable()
    {
        var rnd = new Random(5);
        var packets = Enumerable.Range(0, 300).Select(_ => RandomPacket(rnd)).ToArray();
        packets[10][7] ^= 0x15;                                     // repairable
        foreach (var i in new[] { 3, 8, 12, 17 }) packets[20][i] ^= 0x2A; // 4 bad symbols: beyond repair
        var data = packets.SelectMany(p => p).ToArray();

        var report = CdgInspector.RepairInPlace(data);

        Assert.True(report.HasParity);
        Assert.Equal(1, report.Repairable);
        Assert.Equal(1, report.Unrepairable);
        Assert.Equal(2, report.DamageTimes.Count);
        Assert.False(report.IsClean);
        Assert.All(data.AsSpan(20 * 24, 24).ToArray(), b => Assert.Equal(0, b)); // blanked, draws nothing
        Assert.True(CdgParity.IsValid(data.AsSpan(10 * 24, 24)));
    }

    [Fact]
    public void Inspector_FileWithoutParity_IsLeftAlone()
    {
        var data = TestFiles.SimpleCdg(2);     // test packets have zeroed parity, like stripped rips
        var before = (byte[])data.Clone();
        var report = CdgInspector.RepairInPlace(data);
        Assert.False(report.HasParity);
        Assert.Equal(0, report.Repairable);
        Assert.Equal(before, data);
        Assert.Contains("can't be detected", report.Summary);
    }
}

public class CdgFillerTests
{
    [Fact]
    public void MusicMaestroStyleFiller_IsReportedAsFillerNotDamage()
    {
        var filler = new byte[24];
        for (int i = 0; i < 20; i++) filler[i] = 0x09;   // command 9, instruction 9, all data 0x09
        filler[9] |= 0x40;                                  // other subcode channel bit set, as on real discs
        KaraokeToMp4.Cdg.CdgParity.ComputeParity(filler);
        var data = Enumerable.Repeat(filler, 100).SelectMany(p => p).ToArray();

        var report = KaraokeToMp4.Cdg.CdgInspector.Inspect(data);
        Assert.Equal(100, report.FillerPackets);
        Assert.Equal(0, report.UnknownInstructions);
        Assert.True(report.IsClean);
        Assert.StartsWith("OK", report.Summary);
    }
}
