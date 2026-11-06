namespace KaraokeToMp4.Lyrics;

/// <summary>One timed fragment of lyric text (a word or syllable).</summary>
/// <param name="Start">Time the syllable starts to be sung.</param>
/// <param name="End">Time the highlight wipe over the syllable completes.</param>
/// <param name="Text">Text including any trailing/leading spaces.</param>
public sealed record LyricSyllable(TimeSpan Start, TimeSpan End, string Text);

/// <summary>A line of lyrics as it appears on screen.</summary>
public sealed class LyricLine
{
    public List<LyricSyllable> Syllables { get; } = new();

    /// <summary>True when this line begins a new paragraph/verse (the screen should be cleared).</summary>
    public bool StartsParagraph { get; set; }

    public string Text => string.Concat(Syllables.Select(s => s.Text));
    public TimeSpan Start => Syllables.Count == 0 ? TimeSpan.Zero : Syllables[0].Start;
    public TimeSpan End => Syllables.Count == 0 ? TimeSpan.Zero : Syllables[^1].End;
}

/// <summary>All lyric data extracted from a karaoke file.</summary>
public sealed class KaraokeLyrics
{
    public List<LyricLine> Lines { get; } = new();

    /// <summary>Title / artist / info lines (from KAR @T tags or the MIDI track name).</summary>
    public List<string> TitleLines { get; } = new();

    public IEnumerable<LyricSyllable> Syllables => Lines.SelectMany(l => l.Syllables);
}
