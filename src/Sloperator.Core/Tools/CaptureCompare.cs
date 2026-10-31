using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using SkiaSharp;

namespace Sloperator.Core.Tools;

/// <summary>
/// Pictures compared in one, for capture_result's <c>compare</c>: any number, each under its label, at
/// its own pixels (never scaled: an upscale beside its original shows its detail, and the user
/// clicks for full size), in the grid whose shape comes out nearest 4:3, as the chat shows a picture
/// at most 480 wide and two wide windows side by side came out a strip. Labels are the model's, in
/// the user's language: shaped with HarfBuzz (Arabic joins and runs right to left) in a font that
/// has the script (Segoe UI, or what Windows picks for CJK and the rest).
/// </summary>
internal static class CaptureCompare
{
    /// <summary>The longest label kept, in characters; the rest is cut to "…", and again to fit its picture's width.</summary>
    private const int LongestLabel = 40;

    private static readonly SKColor Background = new(0x16, 0x16, 0x16), Accent = new(0xFC, 0xC2, 0x1B);

    /// <summary>The labels when the model gave none: Before and After for two, numbers for more.</summary>
    public static List<string> DefaultLabels(int count) => count == 2 ? ["Before", "After"] : Enumerable.Range(1, count).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();

    /// <summary>A label as drawn: its first line, at most <see cref="LongestLabel"/> characters.</summary>
    public static string Tidy(string label)
    {
        var line = label.Trim().Split('\n')[0].Trim();
        return line.Length <= LongestLabel ? line : line[..(LongestLabel - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// The pictures in one, each under its label, in the order given; <paramref name="marks"/> are boxes
    /// on the last one around what changed, in its own pixels (only two pictures of one size have them).
    /// <paramref name="columns"/>, the model's, is how many go in a row; null for the grid nearest 4:3.
    /// </summary>
    public static SKImage Compose(IReadOnlyList<(RawImage Picture, string Label)> pictures, IReadOnlyList<SKRectI>? marks = null, int? columns = null)
    {
        var count = pictures.Count;
        // The band and gap grow with the pictures, so a label on a 4K capture reads like one on a small one.
        var unit = Math.Max(1.0, pictures.Max(p => Math.Max(p.Picture.Width, p.Picture.Height)) / 1600.0);
        var (band, gap) = ((int)Math.Round(40 * unit), (int)Math.Round(16 * unit));

        var (across, widths, heights) = columns is { } chosen ? Grid(Math.Clamp(chosen, 1, count)) : Enumerable.Range(1, count).Select(c => Grid(c)).MinBy(g => Off43(g.Widths.Sum() + gap * (g.Widths.Length - 1), g.Heights.Sum() + gap * (g.Heights.Length - 1)));
        (int Columns, int[] Widths, int[] Heights) Grid(int c)
        {
            var rows = (count + c - 1) / c;
            var w = new int[c];
            var h = new int[rows];
            for (var i = 0; i < count; i++)
            {
                w[i % c] = Math.Max(w[i % c], pictures[i].Picture.Width);
                h[i / c] = Math.Max(h[i / c], band + pictures[i].Picture.Height);
            }
            return (c, w, h);
        }

        using var surface = SKSurface.Create(new SKImageInfo(widths.Sum() + gap * (widths.Length - 1), heights.Sum() + gap * (heights.Length - 1), SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(Background);
        using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var size = (float)(20 * unit);
        for (var i = 0; i < count; i++)
        {
            var (picture, label) = pictures[i];
            var x = widths.Take(i % across).Sum() + gap * (i % across);
            var y = heights.Take(i / across).Sum() + gap * (i / across);
            using (var shaped = Label(label, size, picture.Width))
                if (shaped.Blob is not null)
                    canvas.DrawText(shaped.Blob, x, y + band - (float)(13 * unit), ink);
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(picture.Bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                using var pixels = new SKPixmap(new SKImageInfo(picture.Width, picture.Height, SKColorType.Bgra8888, SKAlphaType.Premul), handle.AddrOfPinnedObject(), picture.Stride);
                using var image = SKImage.FromPixels(pixels);
                canvas.DrawImage(image, x, y + band, SKSamplingOptions.Default);
            }
            finally
            {
                handle.Free();
            }
            if (i == count - 1)
                Mark(x, y + band);
        }
        return surface.Snapshot();

        // On the last picture, at its own pixels: 2 px of the accent, on whole pixels, between 1 px of
        // near-black, to show on light and on dark.
        void Mark(int x, int y)
        {
            if (marks is not { Count: > 0 })
                return;
            var line = 2 * (int)Math.Round(unit);
            using var dark = new SKPaint { Color = Background, Style = SKPaintStyle.Stroke, StrokeWidth = line + 2 * (int)Math.Round(unit), IsAntialias = false };
            using var accent = new SKPaint { Color = Accent, Style = SKPaintStyle.Stroke, StrokeWidth = line, IsAntialias = false };
            foreach (var m in marks)
            {
                var r = new SKRect(x + m.Left, y + m.Top, x + m.Right, y + m.Bottom);
                canvas.DrawRect(r, dark);
                canvas.DrawRect(r, accent);
            }
        }

        static double Off43(int w, int h) => Math.Abs(Math.Log((double)w / h / (4.0 / 3)));
    }

    // ---- labels --------------------------------------------------------------------------------------

    /// <summary>A label shaped to draw: its glyphs in a font that has them, laid out left to right as they show.</summary>
    internal sealed class Shaped(SKTextBlob? blob, float width, ushort[] glyphs, string text) : IDisposable
    {
        public SKTextBlob? Blob { get; } = blob;
        public float Width { get; } = width;

        /// <summary>The glyphs drawn; 0 is the font's empty box, a character it doesn't have.</summary>
        public ushort[] Glyphs { get; } = glyphs;

        /// <summary>The text drawn, cut to fit if it had to be.</summary>
        public string Text { get; } = text;

        public void Dispose() => Blob?.Dispose();
    }

    private static readonly Lazy<SKTypeface> Bold = new(() => SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default);

    /// <summary>A font with the label's characters: Segoe UI, or the one the system picks for the first it lacks (CJK, Thai…).</summary>
    internal static SKTypeface TypefaceFor(string text)
    {
        var bold = Bold.Value;
        using var font = new SKFont(bold);
        if (font.ContainsGlyphs(text))
            return bold;
        foreach (var rune in text.EnumerateRunes())
            if (!font.ContainsGlyph(rune.Value) && SKFontManager.Default.MatchCharacter(bold.FamilyName, SKFontStyle.Bold, null, rune.Value) is { } match)
                return match;
        return bold;
    }

    /// <summary>The label shaped at <paramref name="size"/>, cut with "…" until it fits <paramref name="width"/>.</summary>
    internal static Shaped Label(string text, float size, float width)
    {
        text = Tidy(text);
        var shaped = Shape(text, size);
        while (shaped.Width > width && text.Length > 1)
        {
            shaped.Dispose();
            text = text.TrimEnd('…')[..^1].TrimEnd() + "…";
            shaped = Shape(text, size);
        }
        return shaped;
    }

    /// <summary>The text shaped by HarfBuzz in a font that has it, from x 0 on its baseline.</summary>
    internal static Shaped Shape(string text, float size)
    {
        if (text.Length == 0)
            return new Shaped(null, 0, [], text);
        var typeface = TypefaceFor(text);
        byte[] data;
        int index;
        using (var stream = typeface.OpenStream(out index))
        {
            data = new byte[stream.Length];
            stream.Read(data, data.Length);
        }
        using var blob = HarfBuzzSharp.Blob.FromStream(new MemoryStream(data));
        using var face = new HarfBuzzSharp.Face(blob, index);
        using var hb = new HarfBuzzSharp.Font(face);
        const int scale = 1024;
        hb.SetScale(scale, scale);
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        hb.Shape(buffer);
        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;

        using var font = new SKFont(typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true };
        using var builder = new SKTextBlobBuilder();
        var run = builder.AllocatePositionedRun(font, infos.Length);
        var glyphs = new ushort[infos.Length];
        var points = run.Positions;
        var (k, x) = (size / scale, 0f);
        for (var i = 0; i < infos.Length; i++)
        {
            glyphs[i] = (ushort)infos[i].Codepoint;
            points[i] = new SKPoint(x + positions[i].XOffset * k, -positions[i].YOffset * k);
            x += positions[i].XAdvance * k;
        }
        glyphs.CopyTo(run.Glyphs);
        return new Shaped(builder.Build(), x, glyphs, text);
    }
}
