using System.Text;
using KaraokeToMp4.Lyrics;

namespace KaraokeToMp4.Midi;

/// <summary>
/// Pulls timed lyrics out of .kar (Soft Karaoke) and lyric-bearing standard MIDI files.
/// </summary>
/// <remarks>
/// <para>
/// <b>KAR</b> files store lyrics as Text meta events (0x01), conventionally in a track named "Words".
/// Text starting with '@' is metadata (@K file type, @T title/artist, @L language, @I info,
/// @V version). A leading '\' starts a new paragraph (clear screen) and a leading '/' starts a new line.
/// </para>
/// <para>
/// <b>MIDI with lyrics</b> uses Lyric meta events (0x05). Line breaks are carried as CR/LF characters
/// (RP-017), and many files also use the KAR '/' and '\' conventions, so both are honoured.
/// </para>
/// </remarks>
public static class MidiLyricExtractor
{
    /// <summary>Longest time a single syllable's highlight wipe may take.</summary>
    public static TimeSpan MaxSyllableDuration { get; set; } = TimeSpan.FromSeconds(1.5);

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    public static KaraokeLyrics Extract(StandardMidiFile midi, KaraokeFormat? preferred = null)
    {
        var textEvents = midi.Events.Where(e => e.IsMeta && e.MetaType == StandardMidiFile.MetaText).ToList();
        var lyricEvents = midi.Events.Where(e => e.IsMeta && e.MetaType == StandardMidiFile.MetaLyric).ToList();

        var lyrics = new KaraokeLyrics();
        bool looksLikeKar = textEvents.Any(e => DecodeText(e.Data).StartsWith("@K", StringComparison.OrdinalIgnoreCase));

        foreach (var e in textEvents)
        {
            var t = DecodeText(e.Data);
            if (t.StartsWith("@T", StringComparison.OrdinalIgnoreCase) && t.Length > 2)
                lyrics.TitleLines.Add(t[2..].Trim());
        }

        // Decide where the sung lyrics live.
        var karCandidates = textEvents.Where(e => !DecodeText(e.Data).StartsWith('@')).ToList();
        bool useText = preferred == KaraokeFormat.Kar || looksLikeKar;
        if (!useText && lyricEvents.Count == 0) useText = true;
        if (useText && karCandidates.Count == 0 && lyricEvents.Count > 0) useText = false;

        List<MidiEvent> source;
        if (useText)
        {
            // Pick the track with the most lyric-like text events (normally the "Words" track),
            // so copyright notices and instrument comments in other tracks are ignored.
            var best = karCandidates.GroupBy(e => e.Track).OrderByDescending(g => g.Count()).FirstOrDefault();
            source = best?.ToList() ?? new List<MidiEvent>();
        }
        else
        {
            var best = lyricEvents.GroupBy(e => e.Track).OrderByDescending(g => g.Count()).FirstOrDefault();
            source = best?.ToList() ?? new List<MidiEvent>();
        }

        BuildLines(midi, source, lyrics);

        if (lyrics.TitleLines.Count == 0)
        {
            var name = midi.Events
                .Where(e => e.IsMeta && e.MetaType == StandardMidiFile.MetaTrackName && e.Track == 0)
                .Select(e => DecodeText(e.Data).Trim())
                .FirstOrDefault(s => s.Length > 0);
            if (name != null) lyrics.TitleLines.Add(name);
        }
        return lyrics;
    }

    private static void BuildLines(StandardMidiFile midi, List<MidiEvent> source, KaraokeLyrics lyrics)
    {
        // First pass: produce (time, text, breakBefore) tokens.
        var tokens = new List<(double Time, string Text, int BreakBefore)>(); // 0 none, 1 line, 2 paragraph
        int pendingBreak = 0;
        foreach (var e in source)
        {
            double time = midi.TicksToSeconds(e.Tick);
            string raw = DecodeText(e.Data);
            int i = 0;
            var sb = new StringBuilder();

            void Flush()
            {
                if (sb.Length == 0) return;
                tokens.Add((time, sb.ToString(), pendingBreak));
                pendingBreak = 0;
                sb.Clear();
            }

            for (; i < raw.Length; i++)
            {
                char c = raw[i];
                switch (c)
                {
                    case '\\':
                        Flush();
                        pendingBreak = 2;
                        break;
                    case '/':
                        Flush();
                        pendingBreak = Math.Max(pendingBreak, 1);
                        break;
                    case '\r':
                        Flush();
                        pendingBreak = Math.Max(pendingBreak, 1);
                        break;
                    case '\n':
                        // RP-017: CR ends a line, LF starts a new paragraph (new screen).
                        Flush();
                        pendingBreak = 2;
                        break;
                    case '\0':
                        break;
                    default:
                        if (!char.IsControl(c)) sb.Append(c);
                        break;
                }
            }
            Flush();
        }

        // Second pass: assign end times and group into lines.
        LyricLine? line = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            var (time, text, brk) = tokens[i];
            double next = i + 1 < tokens.Count ? tokens[i + 1].Time : time + MaxSyllableDuration.TotalSeconds;
            double end = Math.Min(next, time + MaxSyllableDuration.TotalSeconds);
            if (end <= time) end = time + 0.05;

            if (line == null || brk > 0)
            {
                line = new LyricLine { StartsParagraph = brk == 2 || lyrics.Lines.Count == 0 };
                lyrics.Lines.Add(line);
            }
            line.Syllables.Add(new LyricSyllable(TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(end), text));
        }

        // Drop whitespace-only lines and legal notices (e.g. Tune 1000's "ALL rights reserved. Not for
        // broadcast..." stored as lyrics at time 0), carrying a paragraph break forward to the next real line.
        bool carryParagraph = false;
        for (int i = 0; i < lyrics.Lines.Count; i++)
        {
            var l = lyrics.Lines[i];
            if (string.IsNullOrWhiteSpace(l.Text) || IsNotice(l.Text))
            {
                carryParagraph |= l.StartsParagraph;
                lyrics.Lines.RemoveAt(i--);
                continue;
            }
            if (carryParagraph) { l.StartsParagraph = true; carryParagraph = false; }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex NoticePattern = new(
        @"rights\s+reserved|not\s+for\s+(broadcast|rental|resale|sale)|transmission\s+of\s+any\s+kind|do\s+not\s+(duplicate|copy)|unauthori[sz]ed\s+(copying|duplication|use)|copyright|\(c\)\s*\d{4}|©|tune\s*1000",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True for copyright/usage notices that some files embed among the lyrics.</summary>
    public static bool IsNotice(string lineText) => NoticePattern.IsMatch(lineText);

    /// <summary>Decodes MIDI text as UTF-8 when valid, otherwise as Windows-1252/Latin-1.</summary>
    public static string DecodeText(byte[] data)
    {
        try { return StrictUtf8.GetString(data); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(data); }
    }
}
