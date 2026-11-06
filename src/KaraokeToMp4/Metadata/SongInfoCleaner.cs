using System.Text.RegularExpressions;

namespace KaraokeToMp4.Metadata;

/// <summary>
/// Reduces the free-form title/artist text found in karaoke files to just the song name and the
/// original performing artist. Karaoke files often pack extra data into these fields, e.g.
/// "In the style of Patsy Cline (Karaoke Version) - Words &amp; Music by Willie Nelson © 1961".
/// </summary>
public static class SongInfoCleaner
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Longest artist we accept after cleaning; anything longer is almost certainly not just a name.</summary>
    public static int MaxArtistLength { get; set; } = 40;

    /// <summary>Most words we accept in an artist name after cleaning.</summary>
    public static int MaxArtistWords { get; set; } = 6;

    // "(Karaoke Version)", "[Tune 1000]", "{MIDI}" ... any bracketed extra.
    private static readonly Regex Brackets = new(@"\s*[\(\[\{<][^\)\]\}>]*[\)\]\}>]", Opts);

    // Copyright notices and everything after them.
    private static readonly Regex Copyright = new(@"\s*(©|\(c\)|\(p\)|℗|copyright\b|all rights reserved).*$", Opts);

    // Lead-ins that come before the performer's name.
    private static readonly Regex LeadIn = new(
        @"^\s*(?:(?:as\s+)?(?:made\s+famous|made\s+popular|popularized|popularised|originally\s+performed|performed|recorded|sung|originally\s+recorded|originally)\s+by|in\s+the\s+style\s+of|style\s+of|karaoke\s+version\s+of|version\s+of|tribute\s+to|artist|artiste|singer|performer|by)\s*[:\-]?\s+",
        Opts);

    // Featured artists: keep the main artist only.
    private static readonly Regex Featuring = new(@"\s+(?:feat\.?|ft\.?|featuring|duet\s+with)\s+.*$", Opts);

    // Separators between unrelated pieces of information.
    private static readonly Regex PartSeparator = new(@"\s+[-–—|/]\s+|\s*;\s*|\s*\|\s*|\t|\s{3,}", Opts);

    // Pieces that are credits, not the performer.
    private static readonly Regex CreditPart = new(
        @"\b(words|music|lyrics?|written|writer|composed|composer|arranged|arrangement|sequenced|sequence|produced|producer|programmed|transcribed|published|publisher|licensed|recorded at|tempo|key of|bpm)\b",
        Opts);

    // Words that mark karaoke packaging rather than the artist.
    private static readonly Regex PackagingWord = new(
        @"\b(karaoke|instrumental|backing\s+track|minus\s+one|midi|kar|tune\s*1000|version|demo|vocal\s+guide|lead\s+vocal|no\s+vocals?|with\s+vocals?)\b",
        Opts);

    // Karaoke producers/labels that often sit in the artist field of commercial tracks.
    private static readonly Regex KaraokeLabel = new(
        @"\bkaraoke\b|\b(sound\s*choice|sunfly|chartbuster|pocket\s*songs|party\s*tyme|karafun|singing\s+machine|nutech|priddis|tune\s*1000|mr\.?\s*entertainer|ameritz|kj\s+tracks)\b",
        Opts);

    private static readonly Regex Year = new(@"\b(19|20)\d{2}\b");
    private static readonly Regex Spaces = new(@"\s+");

    /// <summary>Cleans a whole <see cref="SongInfo"/>, keeping the original text in RawTitle/RawArtist.</summary>
    public static SongInfo Clean(SongInfo raw)
    {
        var title = CleanTitle(raw.Title);
        var artist = CleanArtist(raw.Artist, title);
        return raw with
        {
            Title = title,
            Artist = artist,
            RawTitle = raw.RawTitle ?? raw.Title,
            RawArtist = raw.RawArtist ?? raw.Artist,
        };
    }

    // Disc/track catalogue codes tacked onto a title: "Song - CP000015", "Song [SC8127-05]", "Song (DK026-03)".
    private static readonly Regex CatalogueCode = new(
        @"(\s*[-–—|:]\s*|\s*[\(\[]\s*)[A-Z]{1,5}[-_]?\d{3,}(?:[-_.]\d{1,3})?\s*[\)\]]?\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>Removes karaoke packaging ("(Karaoke Version)", copyright, "[Tune 1000]", catalogue codes) from a title.</summary>
    public static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = Copyright.Replace(title, "");
        t = CatalogueCode.Replace(t, "");
        // Only drop bracketed text that is packaging; keep real parts of a title like "(I Can't Get No) Satisfaction".
        t = Brackets.Replace(t, m => PackagingWord.IsMatch(m.Value) || Year.IsMatch(m.Value) ? "" : m.Value);
        t = Tidy(t);
        if (FixAllCapsTitles) t = TitleCaseIfAllCaps(t);
        return t.Length is >= 1 and <= 100 && t.Any(char.IsLetterOrDigit) ? t : null;
    }

    /// <summary>
    /// Convert titles written entirely in capitals ("TOO MUCH HEAVEN") to title case ("Too Much Heaven").
    /// Default true. Artists are left as written, since names like "ABBA" or "AC/DC" are meant to be capitals.
    /// </summary>
    public static bool FixAllCapsTitles { get; set; } = true;

    private static readonly HashSet<string> KeepUpper = new(StringComparer.Ordinal)
    {
        "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "OK", "USA", "UK", "DJ", "TV", "YMCA", "LA", "NYC", "MTV", "ABC", "SOS", "BMW",
    };

    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "but", "by", "for", "in", "nor", "of", "on", "or", "the", "to", "vs", "de", "la", "el", "y",
    };

    /// <summary>"ALL AROUND THE WORLD" -> "All Around the World"; text with any lower-case letters is unchanged.</summary>
    public static string TitleCaseIfAllCaps(string text)
    {
        if (!text.Any(char.IsLetter) || text.Any(char.IsLower)) return text;
        var words = text.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            var core = new string(w.Where(char.IsLetter).ToArray());
            if (core.Length == 0 || KeepUpper.Contains(core)) continue;
            bool first = i == 0, last = i == words.Length - 1;
            if (!first && !last && SmallWords.Contains(core)) { words[i] = w.ToLowerInvariant(); continue; }
            // Capitalise the first letter (after any leading punctuation such as '(' or '"'), lower the rest,
            // and capitalise after hyphens: "STAYIN'" -> "Stayin'", "HEY-HO" -> "Hey-Ho".
            var chars = w.ToLowerInvariant().ToCharArray();
            bool capNext = true;
            for (int c = 0; c < chars.Length; c++)
            {
                if (char.IsLetter(chars[c]))
                {
                    if (capNext) chars[c] = char.ToUpperInvariant(chars[c]);
                    capNext = false;
                }
                else if (chars[c] is '-' or '(' or '"' or '/') capNext = true;
            }
            words[i] = new string(chars);
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// Reduces an artist field to the performing artist, or returns null if nothing name-like is left
    /// (better no artist than a wrong one in the file name).
    /// </summary>
    /// <param name="title">The song title, so a field like "Yesterday - The Beatles" yields just "The Beatles".</param>
    public static string? CleanArtist(string? artist, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(artist)) return null;

        var a = Copyright.Replace(artist, "");
        a = Brackets.Replace(a, "");

        // Split "Artist - Words & Music by X" style fields and keep the first piece that isn't a credit,
        // the title, or packaging.
        string? chosen = null;
        foreach (var rawPart in PartSeparator.Split(a))
        {
            var part = Tidy(LeadIn.Replace(rawPart, ""));
            part = Featuring.Replace(part, "");
            part = Tidy(Year.Replace(part, ""));
            if (part.Length == 0) continue;
            if (CreditPart.IsMatch(part) && !LeadIn.IsMatch(rawPart)) continue;
            if (title != null && string.Equals(Normalize(part), Normalize(title), StringComparison.Ordinal)) continue;
            if (KaraokeLabel.IsMatch(part)) continue; // a karaoke label, not the original artist
            part = Tidy(PackagingWord.Replace(part, ""));
            if (part.Length == 0) continue;
            chosen = part;
            break;
        }

        if (chosen == null) return null;
        if (chosen.Length > MaxArtistLength) return null;
        if (chosen.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxArtistWords) return null;
        if (!chosen.Any(char.IsLetter)) return null;
        if (!SongInfoReader.IsUsable(chosen)) return null;
        return chosen;
    }

    private static string Tidy(string s)
    {
        s = Spaces.Replace(s, " ").Trim();
        return s.Trim('-', '–', '—', ',', ':', ';', '/', '\\', '|', '.', '*', '"', '\'', ' ', '_');
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
