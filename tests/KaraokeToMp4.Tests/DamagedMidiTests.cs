using KaraokeToMp4.Midi;

namespace KaraokeToMp4.Tests;

/// <summary>Damage patterns seen in real KAR collections (files cut at 32 KB, garbage tails, odd end markers).</summary>
public class DamagedMidiTests
{
    private static byte[] Kar() => TestFiles.Kar("\\Hel", "lo ", "world", "/Se", "cond ", "line");

    [Fact]
    public void Intact_HasNoProblems()
    {
        Assert.False(new StandardMidiFile(Kar()).IsDamaged);
    }

    [Fact]
    public void Truncated_StillReadsLyrics_AndReportsIt()
    {
        var data = Kar();
        var cut = data.AsSpan(0, data.Length - 20).ToArray(); // lose the end of the last (lyrics) track
        var midi = new StandardMidiFile(cut);

        Assert.True(midi.IsDamaged);
        Assert.Contains(midi.Problems, p => p.Contains("truncated"));
        Assert.NotEmpty(MidiLyricExtractor.Extract(midi, KaraokeFormat.Kar).Lines);
    }

    [Fact]
    public void Truncated_ReencodesToSomethingTheSynthesiserAccepts()
    {
        var data = Kar();
        var midi = new StandardMidiFile(data.AsSpan(0, data.Length - 20).ToArray());
        using var ms = new MemoryStream(midi.ToBytes());
        var synthFile = new MeltySynth.MidiFile(ms);
        Assert.True(synthFile.Length > TimeSpan.Zero);
    }

    [Fact]
    public void TrailingStrayBytes_AreIgnored()
    {
        var data = Kar().Concat(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 }).ToArray();
        Assert.False(new StandardMidiFile(data).IsDamaged);
    }

    [Fact]
    public void FarAwayEndOfTrack_DoesNotStretchTheSong()
    {
        // Rewrite the conductor track's End-of-Track with a huge delta (like a file whose tempo track ends at 18 min).
        var data = Kar();
        int eot = data.AsSpan().IndexOf(new byte[] { 0x00, 0xFF, 0x2F, 0x00 });
        var patched = data[..eot].Concat(new byte[] { 0x8F, 0xFF, 0x7F, 0xFF, 0x2F, 0x00 }).Concat(data[(eot + 4)..]).ToArray();
        // Fix the conductor track's chunk length (+2 bytes).
        int len = patched[18] << 24 | patched[19] << 16 | patched[20] << 8 | patched[21];
        len += 2;
        patched[18] = (byte)(len >> 24); patched[19] = (byte)(len >> 16); patched[20] = (byte)(len >> 8); patched[21] = (byte)len;

        var midi = new StandardMidiFile(patched);
        Assert.False(midi.IsDamaged);
        Assert.InRange(midi.DurationSeconds, 1, 10);

        using var ms = new MemoryStream(midi.ToBytes());
        Assert.InRange(new MeltySynth.MidiFile(ms).Length.TotalSeconds, 1, 10);
    }

    [Fact]
    public void EmptyFile_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => new StandardMidiFile(Array.Empty<byte>()));
    }
}
