using System.Globalization;
using System.Text;

namespace KaraokeToMp4;

/// <summary>Song title and artist read from a karaoke file (either may be missing).</summary>
/// <param name="Title">Song title, or null if the file doesn't say.</param>
/// <param name="Artist">Artist, or null if the file doesn't say.</param>
/// <param name="Source">Where the values came from (e.g. "KAR @T tags", "ID3 tags"), for display/debugging.</param>
/// <param name="RawTitle">The title text exactly as found in the file, before cleaning.</param>
/// <param name="RawArtist">The artist text exactly as found in the file, before cleaning.</param>
public sealed record SongInfo(string? Title, string? Artist, string? Source = null, string? RawTitle = null, string? RawArtist = null)
{
    /// <summary>The same info without the artist (for "song title only" naming/metadata).</summary>
    public SongInfo WithoutArtist() => this with { Artist = null };

    public static SongInfo Empty { get; } = new(null, null);

    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);
    public bool IsEmpty => !HasTitle && !HasArtist;

    /// <summary>
    /// Suggests an output file name (same folder as <paramref name="inputPath"/>, .mp4 extension).
    /// The input's own name is kept; the artist and/or title are appended only if the name doesn't
    /// already contain them, e.g. "SC8127-05.mp3" + Beatles/Yesterday -> "SC8127-05 - Beatles - Yesterday.mp4",
    /// while "Beatles - Yesterday.cdg" stays "Beatles - Yesterday.mp4".
    /// </summary>
    public string SuggestOutputPath(string inputPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
        return Path.Combine(dir, SuggestOutputFileName(Path.GetFileNameWithoutExtension(inputPath)) + ".mp4");
    }

    /// <summary>The file name part of <see cref="SuggestOutputPath"/> (without extension).</summary>
    public string SuggestOutputFileName(string baseName)
    {
        var missing = new List<string>();
        if (HasArtist && !NameContains(baseName, Artist!)) missing.Add(Artist!.Trim());
        if (HasTitle && !NameContains(baseName, Title!)) missing.Add(Title!.Trim());

        var name = missing.Count == 0 ? baseName : baseName.TrimEnd(' ', '-', '_') + " - " + string.Join(" - ", missing);
        return SanitizeFileName(name);
    }

    /// <summary>
    /// True when <paramref name="fileName"/> already mentions <paramref name="value"/>, ignoring case,
    /// punctuation, spacing, accents, a leading "The", "&amp;" vs "and", and bracketed extras such as
    /// "(Remastered)".
    /// </summary>
    public static bool NameContains(string fileName, string value)
    {
        var name = Normalize(fileName);
        if (name.Length == 0) return false;
        foreach (var variant in Variants(value))
        {
            var v = Normalize(variant);
            if (v.Length > 0 && name.Contains(v, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static IEnumerable<string> Variants(string value)
    {
        var v = value.Trim();
        yield return v;

        // Drop "(Live)", "[Karaoke Version]" etc.
        var noBrackets = System.Text.RegularExpressions.Regex.Replace(v, @"\s*[\(\[\{][^\)\]\}]*[\)\]\}]", "").Trim();
        if (noBrackets.Length > 0 && noBrackets != v) yield return noBrackets;

        foreach (var s in new[] { v, noBrackets })
        {
            if (s.StartsWith("the ", StringComparison.OrdinalIgnoreCase) && s.Length > 4)
                yield return s[4..];
        }
    }

    private static string Normalize(string s)
    {
        s = s.Replace("&", " and ");
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Makes a string safe as a file name on Windows, macOS and Linux.</summary>
    public static string SanitizeFileName(string name)
    {
        const string invalid = "<>:\"/\\|?*";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsControl(c) || invalid.Contains(c) ? '_' : c);

        var result = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (result.Length > 180) result = result[..180].TrimEnd('.', ' ');
        return result.Length == 0 ? "karaoke" : result;
    }
}
