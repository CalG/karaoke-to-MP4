using KaraokeToMp4.Lyrics;
using KaraokeToMp4.Midi;

namespace KaraokeToMp4.Tests;

/// <summary>
/// Optional checks against a folder of real karaoke files that can't be committed (copyright).
/// Set KARAOKE_TEST_MEDIA to the folder (e.g. a clone of a private test-media repo); without it these
/// tests do nothing. They check parsing, song info and lyrics quickly, without encoding video.
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

    [Theory]
    [MemberData(nameof(MidiFiles))]
    public void MidiFile_ParsesWithSensibleTitleLyricsAndLength(string relativePath)
    {
        if (relativePath == "") return; // no media folder configured
        var path = Path.Combine(Environment.GetEnvironmentVariable("KARAOKE_TEST_MEDIA")!, relativePath);
        if (new FileInfo(path).Length == 0) return; // empty placeholder / failed upload: nothing to test

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
