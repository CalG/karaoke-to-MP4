using SkiaSharp;

namespace KaraokeToMp4.Lyrics;

/// <summary>
/// Draws karaoke lyric frames (title card, pages of lines with a left-to-right colour wipe)
/// into a BGRA pixel buffer using SkiaSharp.
/// </summary>
public sealed class LyricFrameRenderer : IDisposable
{
    private readonly ConversionOptions _options;
    private readonly LyricScreenPlan _plan;
    private readonly IReadOnlyList<string> _titleLines;
    private readonly SKBitmap _bitmap;
    private readonly SKCanvas _canvas;
    private readonly SKTypeface _typeface;
    private readonly SKPaint _fill;
    private readonly SKPaint _stroke;
    private readonly float _baseFontSize;
    private readonly Dictionary<LyricPage, PageLayout> _layouts = new();
    private int _pageHint;

    public LyricFrameRenderer(ConversionOptions options, LyricScreenPlan plan, IReadOnlyList<string> titleLines)
    {
        _options = options;
        _plan = plan;
        _titleLines = titleLines;
        _bitmap = new SKBitmap(new SKImageInfo(options.Width, options.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        _canvas = new SKCanvas(_bitmap);

        _typeface = LoadTypeface(options);
        _baseFontSize = options.FontSize > 0 ? options.FontSize : options.Height / 12f;
        _fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        _stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeJoin = SKStrokeJoin.Round,
            Color = options.OutlineColor,
        };
    }

    public int Width => _options.Width;
    public int Height => _options.Height;

    /// <summary>Renders the frame for <paramref name="time"/> and returns its BGRA pixels.</summary>
    /// <remarks>The returned span is only valid until the next call.</remarks>
    public ReadOnlySpan<byte> Render(TimeSpan time)
    {
        _canvas.Clear(_options.BackgroundColor);

        if (_options.ShowTitleScreen && time < _plan.TitleUntil && _titleLines.Count > 0)
        {
            DrawTitle();
        }
        else
        {
            var page = _plan.PageAt(time, ref _pageHint);
            if (page != null) DrawPage(page, time);
        }

        _canvas.Flush();
        return _bitmap.GetPixelSpan();
    }

    private void DrawTitle()
    {
        float size = _baseFontSize * 1.1f;
        float lineHeight = size * 1.5f;
        float totalHeight = lineHeight * _titleLines.Count;
        float y = (Height - totalHeight) / 2 + size;
        foreach (var text in _titleLines)
        {
            using var font = FitFont(text, size);
            float w = font.MeasureText(text);
            DrawOutlined(text, (Width - w) / 2, y, font, _options.TitleColor);
            y += lineHeight;
        }
    }

    private void DrawPage(LyricPage page, TimeSpan time)
    {
        var layout = GetLayout(page);
        foreach (var line in layout.Lines)
        {
            // Unsung text first, then the sung portion clipped to the wipe position on top.
            DrawOutlined(line.Text, line.X, line.Baseline, line.Font, _options.UnsungColor);

            float wipe = line.WipeX(time);
            if (wipe <= line.X) continue;
            _canvas.Save();
            _canvas.ClipRect(new SKRect(0, line.Baseline - line.Font.Size * 1.5f, wipe, line.Baseline + line.Font.Size));
            DrawOutlined(line.Text, line.X, line.Baseline, line.Font, _options.SungColor);
            _canvas.Restore();
        }
    }

    private void DrawOutlined(string text, float x, float y, SKFont font, SKColor color)
    {
        _stroke.StrokeWidth = Math.Max(2f, font.Size * 0.12f);
        _canvas.DrawText(text, x, y, SKTextAlign.Left, font, _stroke);
        _fill.Color = color;
        _canvas.DrawText(text, x, y, SKTextAlign.Left, font, _fill);
    }

    private PageLayout GetLayout(LyricPage page)
    {
        if (_layouts.TryGetValue(page, out var cached)) return cached;
        if (_layouts.Count > 8)
        {
            foreach (var l in _layouts.Values) l.Dispose();
            _layouts.Clear();
        }

        float lineHeight = _baseFontSize * 1.6f;
        int slots = Math.Max(page.Lines.Count, 1);
        float top = (Height - lineHeight * slots) / 2;
        var lines = new List<LineLayout>();
        for (int i = 0; i < page.Lines.Count; i++)
        {
            var line = page.Lines[i];
            string text = line.Text.TrimEnd();
            var font = FitFont(text, _baseFontSize);
            float leading = font.MeasureText(line.Text) - font.MeasureText(line.Text.TrimStart());
            float x = (Width - font.MeasureText(text.TrimStart())) / 2 - leading;
            float baseline = top + lineHeight * i + lineHeight * 0.72f;

            // Pixel position where each syllable starts/ends, for the wipe.
            var stops = new List<(TimeSpan Start, TimeSpan End, float X0, float X1)>();
            string prefix = "";
            foreach (var syl in line.Syllables)
            {
                float x0 = x + font.MeasureText(prefix);
                prefix += syl.Text;
                float x1 = x + font.MeasureText(prefix);
                stops.Add((syl.Start, syl.End, x0, x1));
            }
            lines.Add(new LineLayout(line.Text, x, baseline, font, stops));
        }
        var layout = new PageLayout(lines);
        _layouts[page] = layout;
        return layout;
    }

    private SKFont FitFont(string text, float size)
    {
        var font = new SKFont(_typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true };
        float maxWidth = Width * 0.92f;
        float w = font.MeasureText(text);
        if (w > maxWidth)
            font.Size = Math.Max(8f, size * maxWidth / w);
        return font;
    }

    private static SKTypeface LoadTypeface(ConversionOptions options)
    {
        if (!string.IsNullOrEmpty(options.FontPath))
        {
            var tf = SKTypeface.FromFile(options.FontPath);
            if (tf != null) return tf;
            throw new KaraokeConversionException($"Could not load font '{options.FontPath}'.");
        }
        return SKTypeface.FromFamilyName(options.FontFamily, SKFontStyle.Bold)
               ?? SKTypeface.FromFamilyName(null, SKFontStyle.Bold)
               ?? SKTypeface.Default;
    }

    public void Dispose()
    {
        foreach (var l in _layouts.Values) l.Dispose();
        _layouts.Clear();
        _fill.Dispose();
        _stroke.Dispose();
        _typeface.Dispose();
        _canvas.Dispose();
        _bitmap.Dispose();
    }

    private sealed record PageLayout(List<LineLayout> Lines) : IDisposable
    {
        public void Dispose()
        {
            foreach (var l in Lines) l.Font.Dispose();
        }
    }

    private sealed record LineLayout(
        string Text,
        float X,
        float Baseline,
        SKFont Font,
        List<(TimeSpan Start, TimeSpan End, float X0, float X1)> Stops)
    {
        public float WipeX(TimeSpan time)
        {
            float wipe = X;
            foreach (var (start, end, x0, x1) in Stops)
            {
                if (time < start) break;
                if (time >= end) { wipe = x1; continue; }
                double f = (time - start) / (end - start);
                return x0 + (float)((x1 - x0) * f);
            }
            return wipe;
        }
    }
}
