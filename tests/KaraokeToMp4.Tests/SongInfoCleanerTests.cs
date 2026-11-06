using KaraokeToMp4.Metadata;
using KaraokeToMp4.Midi;

namespace KaraokeToMp4.Tests;

public class SongInfoCleanerTests
{
    [Theory]
    [InlineData("Patsy Cline", "Patsy Cline")]
    [InlineData("In the style of Patsy Cline", "Patsy Cline")]
    [InlineData("As made famous by Patsy Cline", "Patsy Cline")]
    [InlineData("Originally performed by: Patsy Cline", "Patsy Cline")]
    [InlineData("Patsy Cline (Karaoke Version)", "Patsy Cline")]
    [InlineData("Patsy Cline [Tune 1000]", "Patsy Cline")]
    [InlineData("Patsy Cline - Words & Music by Willie Nelson", "Patsy Cline")]
    [InlineData("Words & Music by Willie Nelson / Patsy Cline", "Patsy Cline")]
    [InlineData("Patsy Cline © 1961 Tree Publishing", "Patsy Cline")]
    [InlineData("Patsy Cline 1961", "Patsy Cline")]
    [InlineData("Ed Sheeran feat. Beyoncé", "Ed Sheeran")]
    [InlineData("Shakira ft Wyclef Jean", "Shakira")]
    [InlineData("Crazy - Patsy Cline", "Patsy Cline")]           // title in the artist field
    [InlineData("AC/DC", "AC/DC")]                               // slash inside a name is kept
    [InlineData("Simon & Garfunkel", "Simon & Garfunkel")]
    [InlineData("The Beatles", "The Beatles")]
    [InlineData("Sound Choice Karaoke", null)]                   // karaoke label, not an artist
    [InlineData("Sequenced by John Smith for Tune 1000 Corp, all rights reserved", null)]
    [InlineData("Words and music by Lennon and McCartney", null)]
    [InlineData("This song was a hit for many artists over the years in different styles", null)] // too long
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void CleanArtist(string raw, string? expected)
    {
        Assert.Equal(expected, SongInfoCleaner.CleanArtist(raw, title: "Crazy"));
    }

    [Theory]
    [InlineData("Crazy", "Crazy")]
    [InlineData("Crazy (Karaoke Version)", "Crazy")]
    [InlineData("Crazy [MIDI]", "Crazy")]
    [InlineData("Crazy (1961)", "Crazy")]
    [InlineData("Crazy © 1961 Tree Publishing", "Crazy")]
    [InlineData("(I Can't Get No) Satisfaction", "(I Can't Get No) Satisfaction")] // real part of the title kept
    [InlineData("Don't Stop Me Now (Live)", "Don't Stop Me Now (Live)")]
    [InlineData("© 1999", null)]
    public void CleanTitle(string raw, string? expected)
    {
        Assert.Equal(expected, SongInfoCleaner.CleanTitle(raw));
    }

    [Fact]
    public void Clean_KeepsRawValues()
    {
        var info = SongInfoCleaner.Clean(new SongInfo("Crazy (Karaoke Version)", "In the style of Patsy Cline", "ID3 tags"));
        Assert.Equal(("Crazy", "Patsy Cline"), (info.Title, info.Artist));
        Assert.Equal(("Crazy (Karaoke Version)", "In the style of Patsy Cline"), (info.RawTitle, info.RawArtist));
        Assert.Equal("ID3 tags", info.Source);
    }

    [Fact]
    public void Kar_SkipsCreditLineAndUsesNextArtistLine()
    {
        var midi = new StandardMidiFile(TestFiles.KarWithTags(
            new[] { "Crazy", "Words & Music by Willie Nelson", "Patsy Cline (Karaoke)" }, "la"));
        var info = SongInfoReader.FromMidi(midi);
        Assert.Equal(("Crazy", "Patsy Cline"), (info.Title, info.Artist));
        Assert.Equal("Patsy Cline (Karaoke)", info.RawArtist);
    }

    [Fact]
    public void Kar_NoPlausibleArtist_TitleOnly()
    {
        var midi = new StandardMidiFile(TestFiles.KarWithTags(new[] { "Crazy", "Sequenced by Tune 1000 Corp" }, "la"));
        var info = SongInfoReader.FromMidi(midi);
        Assert.Equal("Crazy", info.Title);
        Assert.Null(info.Artist);
        Assert.Equal("SC1-01 - Crazy", info.SuggestOutputFileName("SC1-01"));
    }

    [Fact]
    public async Task IncludeArtistFalse_DropsArtist()
    {
        using var dir = new TempDir();
        var kar = TestFiles.WriteTemp(dir.Path, "01.kar", TestFiles.KarWithTags(new[] { "Crazy", "Patsy Cline" }, "la"));
        var converter = new KaraokeConverter(new ConversionOptions { IncludeArtist = false });

        var info = await converter.ReadSongInfoAsync(kar);
        Assert.Equal("Crazy", info.Title);
        Assert.Null(info.Artist);
        Assert.Equal(Path.Combine(dir.Path, "01 - Crazy.mp4"), await converter.SuggestOutputPathAsync(kar));
    }
}

public class SongInfoCleanerLabelTests
{
    [Theory]
    [InlineData("Zoom Karaoke", null)]
    [InlineData("Sunfly", null)]
    [InlineData("Patsy Cline - Sound Choice", "Patsy Cline")]
    [InlineData("Chartbuster Karaoke / Patsy Cline", "Patsy Cline")]
    public void KaraokeLabelsAreNotArtists(string raw, string? expected)
    {
        Assert.Equal(expected, KaraokeToMp4.Metadata.SongInfoCleaner.CleanArtist(raw, "Crazy"));
    }
}

/// <summary>Patterns seen in real Tune 1000 / KAR files (built synthetically; no copyrighted content).</summary>
public class RealWorldPatternTests
{
    [Theory]
    [InlineData("TOO MUCH HEAVEN                 ;Barry Gibb / Robin Gibb / Maurice Gibb", "Too Much Heaven")]
    [InlineData("I WRITE THE SONGS               ;Words and music by Bruce Johnston", "I Write the Songs")]
    [InlineData("WORDS                           ;Barry Gibb / Robin Gibb / Maurice Gibb", "Words")]
    [InlineData("STAYIN' ALIVE ;x", "Stayin' Alive")]
    public void Tune1000SequenceName_TitleBeforeSemicolon(string sequenceName, string expected)
    {
        var midi = new KaraokeToMp4.Midi.StandardMidiFile(TestFiles.MidiWithLyricsAndHeader(new[] { "Back Vocal" }, sequenceName, "la\r"));
        var info = KaraokeToMp4.Metadata.SongInfoReader.FromMidi(midi);
        Assert.Equal(expected, info.Title);
        Assert.Null(info.Artist); // writers are not the performer
    }

    [Fact]
    public void Tune1000Notice_IsNotShownAsLyrics_AndLineFeedStartsNewScreen()
    {
        var midi = new KaraokeToMp4.Midi.StandardMidiFile(TestFiles.MidiWithLyricsAndHeader(Array.Empty<string>(), "SONG ;w",
            "ALL rights reserved. Not for broadcast or ", "\r", "transmission of any kind. ", "\r", "DO NOT DUPLICATE. NOT FOR RENTAL. ", "\r",
            "\n", "First ", "line\r", "Second\r", "\n", "Next ", "verse\r"));
        var lyrics = KaraokeToMp4.Midi.MidiLyricExtractor.Extract(midi);
        Assert.Equal(new[] { "First line", "Second", "Next verse" }, lyrics.Lines.Select(l => l.Text.Trim()));
        Assert.True(lyrics.Lines[0].StartsParagraph);
        Assert.False(lyrics.Lines[1].StartsParagraph);
        Assert.True(lyrics.Lines[2].StartsParagraph);
    }

    [Theory]
    [InlineData("ALL AROUND THE WORLD", "All Around the World")]
    [InlineData("HOW CAN YOU MEND A BROKEN HEART", "How Can You Mend a Broken Heart")]
    [InlineData("AMOR DE HOMBRE", "Amor de Hombre")]
    [InlineData("THE WAY YOU ARE", "The Way You Are")]          // small word first stays capitalised
    [InlineData("ROCKY II THEME", "Rocky II Theme")]
    [InlineData("Already Mixed Case", "Already Mixed Case")]
    [InlineData("HEY-HO (LET'S GO)", "Hey-Ho (Let's Go)")]
    public void AllCapsTitles_BecomeTitleCase(string raw, string expected)
    {
        Assert.Equal(expected, KaraokeToMp4.Metadata.SongInfoCleaner.CleanTitle(raw));
    }

    [Fact]
    public void ArtistCapitalsAreKept()
    {
        Assert.Equal("ABBA", KaraokeToMp4.Metadata.SongInfoCleaner.CleanArtist("ABBA"));
        Assert.Equal("Bryan Adams, Rod Stewart, and Sting", KaraokeToMp4.Metadata.SongInfoCleaner.CleanArtist("Recorded by Bryan Adams, Rod Stewart, and Sting"));
    }
}
