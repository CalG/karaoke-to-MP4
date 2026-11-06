using System.Text;

namespace KaraokeToMp4.Tests;

/// <summary>Builds tiny synthetic CDG and MIDI files so tests need no copyrighted media.</summary>
internal static class TestFiles
{
    // ---------------- CDG ----------------

    public static byte[] CdgPacket(byte instruction, params byte[] data)
    {
        var p = new byte[24];
        p[0] = 0x09;
        p[1] = instruction;
        Array.Copy(data, 0, p, 4, Math.Min(16, data.Length));
        return p;
    }

    public static byte[] MemoryPreset(byte color) => CdgPacket(1, color, 0);

    public static byte[] BorderPreset(byte color) => CdgPacket(2, color);

    /// <summary>Loads palette entries 0..7 (low=true) or 8..15 with 4-bit RGB values.</summary>
    public static byte[] LoadColors(bool low, params (int R, int G, int B)[] colors)
    {
        var d = new byte[16];
        for (int i = 0; i < 8 && i < colors.Length; i++)
        {
            var (r, g, b) = colors[i];
            d[2 * i] = (byte)((r << 2) | (g >> 2));
            d[2 * i + 1] = (byte)(((g & 0x03) << 4) | b);
        }
        return CdgPacket(low ? (byte)30 : (byte)31, d);
    }

    public static byte[] Tile(byte color0, byte color1, int row, int col, byte rowBits, bool xor = false)
    {
        var d = new byte[16];
        d[0] = color0; d[1] = color1; d[2] = (byte)row; d[3] = (byte)col;
        for (int i = 0; i < 12; i++) d[4 + i] = rowBits;
        return CdgPacket(xor ? (byte)38 : (byte)6, d);
    }

    public static byte[] Empty() => new byte[24];

    public static byte[] Concat(IEnumerable<byte[]> packets) => packets.SelectMany(p => p).ToArray();

    /// <summary>A CDG stream of <paramref name="seconds"/> that sets a palette and draws a few tiles.</summary>
    public static byte[] SimpleCdg(double seconds)
    {
        var packets = new List<byte[]>
        {
            LoadColors(true, (0, 0, 4), (15, 15, 15), (15, 0, 0), (0, 15, 0)),
            MemoryPreset(0),
            BorderPreset(2),
        };
        int total = (int)(seconds * 300);
        int col = 1;
        while (packets.Count < total)
        {
            if (packets.Count % 30 == 0 && col < 49)
                packets.Add(Tile(0, (byte)(1 + col % 3), 8, col++, 0x3F));
            else
                packets.Add(Empty());
        }
        return Concat(packets);
    }

    // ---------------- MIDI ----------------

    private static void VarLen(List<byte> b, int value)
    {
        var stack = new Stack<byte>();
        stack.Push((byte)(value & 0x7F));
        while ((value >>= 7) > 0) stack.Push((byte)((value & 0x7F) | 0x80));
        b.AddRange(stack);
    }

    private static byte[] Track(IEnumerable<(int Delta, byte[] Bytes)> events)
    {
        var body = new List<byte>();
        foreach (var (delta, bytes) in events)
        {
            VarLen(body, delta);
            body.AddRange(bytes);
        }
        VarLen(body, 0);
        body.AddRange(new byte[] { 0xFF, 0x2F, 0x00 });
        var t = new List<byte>(Encoding.ASCII.GetBytes("MTrk"));
        t.AddRange(BigEndian(body.Count));
        t.AddRange(body);
        return t.ToArray();
    }

    private static byte[] BigEndian(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] Meta(byte type, string text)
    {
        var data = Encoding.Latin1.GetBytes(text);
        var b = new List<byte> { 0xFF, type };
        VarLen(b, data.Length);
        b.AddRange(data);
        return b.ToArray();
    }

    private static byte[] Midi(int ticksPerQuarter, params byte[][] tracks)
    {
        var f = new List<byte>(Encoding.ASCII.GetBytes("MThd"));
        f.AddRange(BigEndian(6));
        f.AddRange(new byte[] { 0, 1, 0, (byte)tracks.Length, (byte)(ticksPerQuarter >> 8), (byte)ticksPerQuarter });
        foreach (var t in tracks) f.AddRange(t);
        return f.ToArray();
    }

    /// <summary>Tempo 120 BPM (0.5 s per quarter), 480 ticks per quarter.</summary>
    private static byte[] ConductorTrack(string name) => Track(new[]
    {
        (0, Meta(0x03, name)),
        (0, new byte[] { 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20 }), // 500000 us/quarter
    });

    /// <summary>Simple melody: one quarter note per syllable, running status used for note-offs.</summary>
    private static byte[] NoteTrack(int notes) => Track(Enumerable.Range(0, notes).SelectMany(i => new[]
    {
        (i == 0 ? 0 : 0, new byte[] { 0x90, (byte)(60 + i % 12), 100 }),
        (480, new byte[] { (byte)(60 + i % 12), 0 }), // running status note-off (velocity 0)
    }));

    /// <summary>A KAR file. Syllables are one quarter note (0.5 s) apart, starting at 1 s.</summary>
    public static byte[] Kar(params string[] syllables) => KarWithTags(new[] { "Test Song", "Test Artist" }, syllables);

    /// <summary>A KAR file with the given @T lines.</summary>
    public static byte[] KarWithTags(string[] titleTags, params string[] syllables)
    {
        var words = new List<(int, byte[])> { (0, Meta(0x01, "@KMIDI KARAOKE FILE")) };
        foreach (var t in titleTags) words.Add((0, Meta(0x01, "@T" + t)));
        for (int i = 0; i < syllables.Length; i++)
            words.Add((i == 0 ? 960 : 480, Meta(0x01, syllables[i])));

        return Midi(480, ConductorTrack("Karaoke"), NoteTrack(syllables.Length + 2), Track(words));
    }

    /// <summary>A MIDI file with Lyric (0x05) meta events, using CR for line breaks.</summary>
    public static byte[] MidiWithLyrics(params string[] syllables) => MidiWithLyricsAndHeader(Array.Empty<string>(), "Lyric Song", syllables);

    /// <summary>
    /// Like <see cref="MidiWithLyrics"/> but with Text events at tick 0 of the lyric track (the way many
    /// commercial lyric MIDI files store title/artist/copyright) and a chosen sequence name.
    /// </summary>
    public static byte[] MidiWithLyricsAndHeader(string[] headerTexts, string sequenceName, params string[] syllables)
    {
        var lyrics = new List<(int, byte[])>();
        foreach (var h in headerTexts) lyrics.Add((0, Meta(0x01, h)));
        for (int i = 0; i < syllables.Length; i++)
            lyrics.Add((i == 0 ? 960 : 480, Meta(0x05, syllables[i])));
        return Midi(480, ConductorTrack(sequenceName), NoteTrack(syllables.Length + 2), Track(lyrics));
    }

    public static string WriteTemp(string dir, string name, byte[] data)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }
}

/// <summary>Creates and deletes a temporary directory for a test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "k2mp4-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}
