namespace KaraokeToMp4.Lyrics;

/// <summary>A group of lines shown on screen together.</summary>
public sealed class LyricPage
{
    public List<LyricLine> Lines { get; } = new();
    public TimeSpan FirstSyllable => Lines[0].Start;
    public TimeSpan LastSyllableEnd => Lines[^1].End;

    /// <summary>When the page appears.</summary>
    public TimeSpan ShowFrom { get; internal set; }

    /// <summary>When the page is removed.</summary>
    public TimeSpan ShowUntil { get; internal set; }
}

/// <summary>
/// Decides which lyric lines are visible at any moment: lines are grouped into pages
/// (split on paragraph breaks or when a page is full), and each page is shown from shortly
/// before its first syllable until the next page takes over.
/// </summary>
public sealed class LyricScreenPlan
{
    private static readonly TimeSpan HoldAfterPage = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HoldAfterLastPage = TimeSpan.FromSeconds(2);

    public IReadOnlyList<LyricPage> Pages { get; }

    /// <summary>Until this time, the title card is displayed (if enabled).</summary>
    public TimeSpan TitleUntil { get; }

    public LyricScreenPlan(KaraokeLyrics lyrics, int linesPerPage, TimeSpan leadIn)
    {
        if (linesPerPage < 1) throw new ArgumentOutOfRangeException(nameof(linesPerPage));

        var pages = new List<LyricPage>();
        LyricPage? current = null;
        foreach (var line in lyrics.Lines)
        {
            if (current == null || line.StartsParagraph || current.Lines.Count >= linesPerPage)
            {
                current = new LyricPage();
                pages.Add(current);
            }
            current.Lines.Add(line);
        }

        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            if (i == 0)
            {
                p.ShowFrom = Max(TimeSpan.Zero, p.FirstSyllable - leadIn);
            }
            else
            {
                var prev = pages[i - 1];
                // After a long instrumental break, blank the screen and bring the page in with a lead-in;
                // otherwise flip to it as soon as the previous page has been sung.
                bool longGap = p.FirstSyllable - prev.LastSyllableEnd > leadIn + HoldAfterPage;
                p.ShowFrom = longGap ? p.FirstSyllable - leadIn : Min(prev.LastSyllableEnd, p.FirstSyllable);
                prev.ShowUntil = longGap ? prev.LastSyllableEnd + HoldAfterPage : p.ShowFrom;
            }
        }
        if (pages.Count > 0)
            pages[^1].ShowUntil = pages[^1].LastSyllableEnd + HoldAfterLastPage;

        Pages = pages;
        TitleUntil = pages.Count > 0 ? pages[0].ShowFrom : TimeSpan.MaxValue;
    }

    /// <summary>Returns the page visible at <paramref name="time"/>, or null for a blank screen.</summary>
    public LyricPage? PageAt(TimeSpan time, ref int hint)
    {
        if (Pages.Count == 0) return null;
        if (hint < 0 || hint >= Pages.Count || Pages[hint].ShowFrom > time) hint = 0;
        for (int i = hint; i < Pages.Count; i++)
        {
            var p = Pages[i];
            if (time < p.ShowFrom) { hint = i; return null; }
            if (time < p.ShowUntil) { hint = i; return p; }
        }
        hint = Pages.Count - 1;
        return null;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
