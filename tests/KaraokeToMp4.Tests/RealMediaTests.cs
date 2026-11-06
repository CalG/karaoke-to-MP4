using KaraokeToMp4.Lyrics;
using KaraokeToMp4.Midi;

namespace KaraokeToMp4.Tests;

/// <summary>
/// Optional checks against a folder of real karaoke files that can't be committed (copyright).
/// Set KARAOKE_TEST_MEDIA to the folder (e.g. a clone of a private test-media repo); without it these
/// tests do nothing. They check parsing, song info, lyrics and CDG graphics quickly, without encoding video.
/// </summary>
public class RealMediaTests
{
    public static IEnumerable<object[]> MidiFiles()
    {
        var root = Environment.GetEnvironmentVariable("KARAOKE_TEST_MEDIA");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            yield return new object[] { "" };
            yield break;
        }
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".kar", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".midi", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f))
            yield return new object[] { Path.GetRelativePath(root, f) };
    }

    public static IEnumerable<object[]> CdgFiles()
    {
        var root = Environment.GetEnvironmentVariable("KARAOKE_TEST_MEDIA");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            yield return new object[] { "" };
            yield break;
        }
        foreach (var f in Directory.EnumerateFiles(root, "*.cdg", SearchOption.AllDirectories).OrderBy(f => f))
            yield return new object[] { Path.GetRelativePath(root, f) };
    }

    [Theory]
    [MemberData(nameof(CdgFiles))]
    public void CdgFile_HasAudioDecodesAndShowsGraphics(string relativePath)
    {
        if (relativePath == "") return;
        var path = Path.Combine(Environment.GetEnvironmentVariable("KARAOKE_TEST_MEDIA")!, relativePath);

        Assert.Equal(KaraokeFormat.Mp3Cdg, KaraokeConverter.DetectFormat(path));
        var audio = KaraokeToMp4.Cdg.CdgConverter.FindAudioForCdg(path);
        Assert.NotNull(audio);

        var decoder = KaraokeToMp4.Cdg.CdgDecoder.FromFile(path);
        Assert.InRange(decoder.Duration.TotalSeconds, 30, 15 * 60);

        // Walk through the song; somewhere there must be a frame with real graphics (several colours).
        var frame = new byte[KaraokeToMp4.Cdg.CdgDecoder.Width * KaraokeToMp4.Cdg.CdgDecoder.Height * 4];
        int maxColours = 0;
        for (double t = 5; t < decoder.Duration.TotalSeconds; t += 10)
        {
            decoder.AdvanceTo(TimeSpan.FromSeconds(t));
            decoder.RenderBgra(frame);
            var colours = new HashSet<int>();
            for (int i = 0; i < frame.Length; i += 4 * 97) colours.Add(frame[i] | frame[i + 1] << 8 | frame[i + 2] << 16);
            maxColours = Math.Max(maxColours, colours.Count);
        }
        Assert.True(maxColours >= 3, $"no lyric graphics found (max {maxColours} colours)");

        // Reading tags must never throw, even when the MP3 has none.
        _ = KaraokeToMp4.Metadata.Id3Reader.Read(audio!);
    }

    [Theory]
    [MemberData(nameof(MidiFiles))]
    public void MidiFile_ParsesWithSensibleTitleLyricsAndLength(string relativePath)
    {
        if (relativePath == "") return; // no media folder configured
        var path = Path.Combine(Environment.GetEnvironmentVariable("KARAOKE_TEST_MEDIA")!, relativePath);
        if (new FileInfo(path).Length == 0) return; // empty placeholder / failed upload: nothing to test

        // Plain MIDI files (music only) are allowed in the collection; they must be reported as such.
        if (KaraokeConverter.DetectFormat(path) == KaraokeFormat.Unknown)
        {
            Assert.Contains("no lyrics", KaraokeConverter.DescribeUnsupported(path));
            return;
        }

        var midi = StandardMidiFile.FromFile(path);
        var lyrics = MidiKaraokeConverter.ReadLyrics(path);
        var info = KaraokeToMp4.Metadata.SongInfoReader.FromMidi(midi);

        Assert.True(lyrics.Lines.Count > 5, "expected lyrics");
        Assert.DoesNotContain(lyrics.Lines, l => MidiLyricExtractor.IsNotice(l.Text));
        Assert.False(string.IsNullOrWhiteSpace(info.Title), "expected a title");
        Assert.DoesNotContain(";", info.Title);
        Assert.InRange(midi.DurationSeconds, 30, 15 * 60);

        var plan = new LyricScreenPlan(lyrics, 4, TimeSpan.FromSeconds(3));
        Assert.All(plan.Pages, p => Assert.InRange(p.Lines.Count, 1, 4));

        using var ms = new MemoryStream(midi.ToBytes());
        Assert.InRange(new MeltySynth.MidiFile(ms).Length.TotalSeconds, 30, 15 * 60);
    }
}
