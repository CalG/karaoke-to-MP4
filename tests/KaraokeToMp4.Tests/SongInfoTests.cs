using System.Text;
using KaraokeToMp4.Metadata;
using KaraokeToMp4.Midi;

namespace KaraokeToMp4.Tests;

public class SongInfoTests
{
    // ---------- file naming ----------

    [Theory]
    [InlineData("SC8127-05", "Yesterday", "The Beatles", "SC8127-05 - The Beatles - Yesterday")]
    [InlineData("The Beatles - Yesterday", "Yesterday", "The Beatles", "The Beatles - Yesterday")]
    [InlineData("beatles_yesterday", "Yesterday", "The Beatles", "beatles_yesterday")]               // case, "The", punctuation
    [InlineData("Yesterday", "Yesterday", "The Beatles", "Yesterday - The Beatles")]                 // only artist missing
    [InlineData("Beatles", "Yesterday (Remastered 2009)", "Beatles", "Beatles - Yesterday (Remastered 2009)")]
    [InlineData("Beatles - Yesterday", "Yesterday (Remastered 2009)", "Beatles", "Beatles - Yesterday")] // bracketed extra ignored
    [InlineData("Simon and Garfunkel - The Boxer", "The Boxer", "Simon & Garfunkel", "Simon and Garfunkel - The Boxer")]
    [InlineData("Beyonce - Halo", "Halo", "Beyoncé", "Beyonce - Halo")]                               // accents
    [InlineData("track01", null, null, "track01")]
    [InlineData("track01", "AC/DC: Back?", null, "track01 - AC_DC_ Back_")]                          // sanitised
    public void SuggestOutputFileName_AppendsOnlyMissingParts(string baseName, string? title, string? artist, string expected)
    {
        Assert.Equal(expected, new SongInfo(title, artist).SuggestOutputFileName(baseName));
    }

    [Fact]
    public void SuggestOutputPath_IsNextToInput()
    {
        var input = Path.Combine(Path.GetTempPath(), "x", "Song.cdg");
        var path = new SongInfo("Title", "Artist").SuggestOutputPath(input);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "x", "Song - Artist - Title.mp4"), path);
    }

    // ---------- MIDI ----------

    [Fact]
    public void Kar_TitleThenArtistFromAtT()
    {
        var info = SongInfoReader.FromMidi(new StandardMidiFile(TestFiles.KarWithTags(new[] { "Yesterday", "The Beatles", "Sequenced by Someone" }, "la")));
        Assert.Equal("Yesterday", info.Title);
        Assert.Equal("The Beatles", info.Artist);
        Assert.Equal("KAR @T tags", info.Source);
    }

    [Fact]
    public void Kar_SkipsJunkTitleLines()
    {
        var info = SongInfoReader.FromMidi(new StandardMidiFile(TestFiles.KarWithTags(new[] { "(c) 1999 Soft Karaoke", "Hey Jude", "Beatles" }, "la")));
        Assert.Equal("Hey Jude", info.Title);
        Assert.Equal("Beatles", info.Artist);
    }

    [Fact]
    public void LyricMidi_UsesTextEventsBeforeFirstLyric()
    {
        var midi = new StandardMidiFile(TestFiles.MidiWithLyricsAndHeader(
            new[] { "Copyright (c) 2001 Tune 1000", "Crazy", "Patsy Cline" }, "Words", "Cra", "zy\r"));
        var info = SongInfoReader.FromMidi(midi);
        Assert.Equal("Crazy", info.Title);
        Assert.Equal("Patsy Cline", info.Artist);
    }

    [Fact]
    public void LyricMidi_FallsBackToSequenceName()
    {
        var info = SongInfoReader.FromMidi(new StandardMidiFile(TestFiles.MidiWithLyricsAndHeader(Array.Empty<string>(), "Moon River", "Moon\r")));
        Assert.Equal("Moon River", info.Title);
        Assert.Null(info.Artist);
    }

    [Fact]
    public void LyricMidi_GenericSequenceNameIgnored()
    {
        var info = SongInfoReader.FromMidi(new StandardMidiFile(TestFiles.MidiWithLyricsAndHeader(Array.Empty<string>(), "Untitled", "la\r")));
        Assert.True(info.IsEmpty);
    }

    // ---------- ID3 ----------

    private static byte[] SyncSafe(int v) => new[] { (byte)((v >> 21) & 0x7F), (byte)((v >> 14) & 0x7F), (byte)((v >> 7) & 0x7F), (byte)(v & 0x7F) };

    private static byte[] TextFrame(int major, string id, byte encoding, byte[] text)
    {
        var body = new byte[] { encoding }.Concat(text).ToArray();
        var f = new List<byte>(Encoding.ASCII.GetBytes(id));
        if (major == 2) f.AddRange(new[] { (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length });
        else if (major == 3) f.AddRange(new[] { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, (byte)0, (byte)0 });
        else { f.AddRange(SyncSafe(body.Length)); f.AddRange(new byte[] { 0, 0 }); }
        f.AddRange(body);
        return f.ToArray();
    }

    private static byte[] Id3v2(int major, params byte[][] frames)
    {
        var data = frames.SelectMany(f => f).Concat(new byte[20]).ToArray(); // + padding
        return Encoding.ASCII.GetBytes("ID3").Concat(new byte[] { (byte)major, 0, 0 }).Concat(SyncSafe(data.Length)).Concat(data)
            .Concat(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }) // start of fake audio
            .ToArray();
    }

    [Fact]
    public void Id3v23_Latin1AndUtf16()
    {
        var tag = Id3v2(3,
            TextFrame(3, "TIT2", 0, Encoding.Latin1.GetBytes("Café")),
            TextFrame(3, "TPE1", 1, new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("Björk\0")).ToArray()));
        var info = Id3Reader.Read(new MemoryStream(tag));
        Assert.Equal("Café", info.Title);
        Assert.Equal("Björk", info.Artist);
    }

    [Fact]
    public void Id3v24_Utf8_FirstOfMultipleValues()
    {
        var tag = Id3v2(4,
            TextFrame(4, "TIT2", 3, Encoding.UTF8.GetBytes("Señorita")),
            TextFrame(4, "TPE1", 3, Encoding.UTF8.GetBytes("Shawn Mendes\0Camila Cabello")));
        var info = Id3Reader.Read(new MemoryStream(tag));
        Assert.Equal("Señorita", info.Title);
        Assert.Equal("Shawn Mendes", info.Artist);
    }

    [Fact]
    public void Id3v22_ThreeLetterFrames_AlbumArtistFallback()
    {
        var tag = Id3v2(2, TextFrame(2, "TT2", 0, "Old Tag"u8.ToArray()), TextFrame(2, "TP2", 0, "Band"u8.ToArray()));
        var info = Id3Reader.Read(new MemoryStream(tag));
        Assert.Equal("Old Tag", info.Title);
        Assert.Equal("Band", info.Artist);
    }

    [Fact]
    public void Id3v1_AtEndOfFile()
    {
        var v1 = new byte[128];
        Encoding.ASCII.GetBytes("TAG").CopyTo(v1, 0);
        Encoding.ASCII.GetBytes("Song One").CopyTo(v1, 3);
        Encoding.ASCII.GetBytes("Singer").CopyTo(v1, 33);
        var file = new byte[500].Concat(v1).ToArray();
        var info = Id3Reader.Read(new MemoryStream(file));
        Assert.Equal("Song One", info.Title);
        Assert.Equal("Singer", info.Artist);
    }

    [Fact]
    public void NoTags_IsEmpty()
    {
        Assert.True(Id3Reader.Read(new MemoryStream(new byte[300])).IsEmpty);
    }

    [Fact]
    public async Task ReadAsync_FindsId3InsideZip()
    {
        using var dir = new TempDir();
        var zipPath = dir.File("bundle.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("x.mp3").Open())
                s.Write(Id3v2(3, TextFrame(3, "TIT2", 0, "Zipped"u8.ToArray()), TextFrame(3, "TPE1", 0, "Zipper"u8.ToArray())));
            using (var s = zip.CreateEntry("x.cdg").Open())
                s.Write(new byte[24]);
        }
        var info = await SongInfoReader.ReadAsync(zipPath, tools: null);
        Assert.Equal(("Zipped", "Zipper"), (info.Title, info.Artist));
    }
}
