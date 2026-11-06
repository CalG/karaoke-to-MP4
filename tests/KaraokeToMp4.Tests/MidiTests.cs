using KaraokeToMp4.Lyrics;
using KaraokeToMp4.Midi;

namespace KaraokeToMp4.Tests;

public class MidiTests
{
    [Fact]
    public void Parser_ReadsHeaderTracksAndTempo()
    {
        var midi = new StandardMidiFile(TestFiles.Kar("Hel", "lo"));
        Assert.Equal(1, midi.Format);
        Assert.Equal(3, midi.TrackCount);
        Assert.Equal(480, midi.TicksPerQuarter);
        Assert.Equal(0.5, midi.TicksToSeconds(480), 6);
        Assert.Equal(1.0, midi.TicksToSeconds(960), 6);
    }

    [Fact]
    public void Parser_HandlesRunningStatus()
    {
        var midi = new StandardMidiFile(TestFiles.Kar("a", "b", "c"));
        var noteEvents = midi.Events.Where(e => e.Track == 1 && (e.Status & 0xF0) == 0x90).ToList();
        Assert.Equal(10, noteEvents.Count); // 5 note-ons + 5 running-status note-offs
        Assert.Equal(480 * 5, midi.Events.Where(e => e.Track == 1).Max(e => e.Tick));
    }

    [Fact]
    public void Parser_RejectsNonMidi()
    {
        Assert.Throws<InvalidDataException>(() => new StandardMidiFile(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void Kar_ExtractsTitleSyllablesAndLines()
    {
        var midi = new StandardMidiFile(TestFiles.Kar("\\Hel", "lo ", "world", "/Se", "cond ", "line", "\\New ", "verse"));
        var lyrics = MidiLyricExtractor.Extract(midi, KaraokeFormat.Kar);

        Assert.Equal(new[] { "Test Song", "Test Artist" }, lyrics.TitleLines);
        Assert.Equal(3, lyrics.Lines.Count);
        Assert.Equal("Hello world", lyrics.Lines[0].Text);
        Assert.Equal("Second line", lyrics.Lines[1].Text);
        Assert.Equal("New verse", lyrics.Lines[2].Text);
        Assert.True(lyrics.Lines[0].StartsParagraph);
        Assert.False(lyrics.Lines[1].StartsParagraph);
        Assert.True(lyrics.Lines[2].StartsParagraph);

        var first = lyrics.Lines[0].Syllables[0];
        Assert.Equal(TimeSpan.FromSeconds(1), first.Start);
        Assert.Equal(TimeSpan.FromSeconds(1.5), first.End);
    }

    [Fact]
    public void LyricEvents_UseCarriageReturnAsLineBreak()
    {
        var midi = new StandardMidiFile(TestFiles.MidiWithLyrics("Twin", "kle ", "twin", "kle\r", "lit", "tle ", "star\r"));
        var lyrics = MidiLyricExtractor.Extract(midi);
        Assert.Equal(2, lyrics.Lines.Count);
        Assert.Equal("Twinkle twinkle", lyrics.Lines[0].Text);
        Assert.Equal("little star", lyrics.Lines[1].Text);
        Assert.Equal(new[] { "Lyric Song" }, lyrics.TitleLines); // falls back to track name
    }

    [Fact]
    public void DetectFormat_ClassifiesMidiFiles()
    {
        using var dir = new TempDir();
        var kar = TestFiles.WriteTemp(dir.Path, "a.kar", TestFiles.Kar("a"));
        var karAsMid = TestFiles.WriteTemp(dir.Path, "b.mid", TestFiles.Kar("a"));
        var lyr = TestFiles.WriteTemp(dir.Path, "c.mid", TestFiles.MidiWithLyrics("a"));
        var bad = TestFiles.WriteTemp(dir.Path, "d.mid", new byte[] { 0, 1, 2 });

        Assert.Equal(KaraokeFormat.Kar, KaraokeConverter.DetectFormat(kar));
        Assert.Equal(KaraokeFormat.Kar, KaraokeConverter.DetectFormat(karAsMid));
        Assert.Equal(KaraokeFormat.MidiWithLyrics, KaraokeConverter.DetectFormat(lyr));
        Assert.Equal(KaraokeFormat.Unknown, KaraokeConverter.DetectFormat(bad));
    }

    [Fact]
    public void DetectFormat_FindsCdgPairs()
    {
        using var dir = new TempDir();
        var mp3 = TestFiles.WriteTemp(dir.Path, "Song.mp3", new byte[10]);
        var cdg = TestFiles.WriteTemp(dir.Path, "Song.cdg", new byte[24]);
        var lone = TestFiles.WriteTemp(dir.Path, "Other.mp3", new byte[10]);

        Assert.Equal(KaraokeFormat.Mp3Cdg, KaraokeConverter.DetectFormat(mp3));
        Assert.Equal(KaraokeFormat.Mp3Cdg, KaraokeConverter.DetectFormat(cdg));
        Assert.Equal(KaraokeFormat.Unknown, KaraokeConverter.DetectFormat(lone));
        Assert.Equal(mp3, Cdg.CdgConverter.FindAudioForCdg(cdg));
    }

    [Fact]
    public void ScreenPlan_SplitsPagesOnParagraphsAndCapacity()
    {
        var midi = new StandardMidiFile(TestFiles.Kar("\\a", "/b", "/c", "\\d"));
        var lyrics = MidiLyricExtractor.Extract(midi, KaraokeFormat.Kar);
        var plan = new LyricScreenPlan(lyrics, linesPerPage: 2, leadIn: TimeSpan.FromSeconds(3));

        Assert.Equal(3, plan.Pages.Count); // [a,b] [c] [d]
        Assert.Equal(TimeSpan.Zero, plan.Pages[0].ShowFrom);
        Assert.Equal(plan.Pages[0].ShowUntil, plan.Pages[1].ShowFrom);

        int hint = 0;
        Assert.Same(plan.Pages[0], plan.PageAt(TimeSpan.FromSeconds(1.2), ref hint));
        Assert.Same(plan.Pages[2], plan.PageAt(TimeSpan.FromSeconds(2.6), ref hint));
        Assert.Null(plan.PageAt(TimeSpan.FromSeconds(60), ref hint));
    }
}
