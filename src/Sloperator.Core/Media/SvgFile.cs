using Sloperator.Core.Tools;
using SkiaSharp;
using Svg.Skia;

namespace Sloperator.Core.Media;

/// <summary>
/// An SVG drawn by Svg.Skia: <see cref="Picture"/> records it in its own units (CSS pixels),
/// <see cref="Width"/> by <see cref="Height"/> of them. Its own recording, so it outlives the parser and
/// can be drawn on any thread; nothing disposes it on the UI's side, as the render thread may still be
/// drawing it (the finalizer lets it go).
/// </summary>
public sealed record SvgPicture(SKPicture Picture, float Width, float Height)
{
    /// <summary>Drawn at <paramref name="scale"/> pixels a unit, on transparent; the scale is kept within <see cref="SvgFile.MaxPixels"/>.</summary>
    public SKBitmap Render(double scale)
    {
        scale = Math.Min(scale, Math.Sqrt(SvgFile.MaxPixels / Math.Max(1.0, (double)Width * Height)));
        scale = Math.Min(scale, SvgFile.MaxSide / Math.Max(Width, Height));
        var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, (int)Math.Round(Width * scale)), Math.Max(1, (int)Math.Round(Height * scale)), SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)(bitmap.Width / (double)Width), (float)(bitmap.Height / (double)Height));
        canvas.DrawPicture(Picture);
        return bitmap;
    }

    /// <summary>
    /// Drawn with its longer side at least <paramref name="longSide"/> pixels (bigger than it is, as a
    /// drawing stays sharp), and at its own size when that is bigger.
    /// </summary>
    public SKBitmap RenderAtLeast(int longSide) => Render(Math.Max(1, longSide / Math.Max(Width, Height)));
}

/// <summary>
/// SVG files (a drawing, or a design mockup the model wrote) read as pictures: shown in the chat and
/// File inspector, and to the model by read_file. Svg.Skia reads them (MIT; its SVG model is MS-PL).
/// An SVG may name pictures on the web; those are not fetched (opening a file makes no request), but
/// pictures next to the file are drawn. Its scripts don't run (Svg.Skia ships no JavaScript engine).
/// </summary>
public static class SvgFile
{
    /// <summary>The most pixels an SVG is drawn with, and its longest side.</summary>
    public const double MaxPixels = 64.0 * 1024 * 1024, MaxSide = 16384;

    public static bool Handles(string path) => Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>The drawing in the file; null when it can't be read or draws nothing.</summary>
    public static SvgPicture? Open(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            using var stream = TextFiles.OpenRead(full);
            return Open(stream, new Uri(full), Svg.SvgExternalResourcePolicy.SameOrigin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>An SVG's bytes (an MCP server's image) as a drawing; null when they aren't one. Nothing outside the bytes is read.</summary>
    public static SvgPicture? Open(byte[] data)
    {
        var start = data.AsSpan(0, Math.Min(data.Length, 1024));
        // Only bytes that look like markup are parsed: the parser is slow to give up on a PNG.
        if (start.IndexOf("<svg"u8) < 0)
            return null;
        using var stream = new MemoryStream(data, writable: false);
        return Open(stream, null, Svg.SvgExternalResourcePolicy.SameDocumentAndDataOnly);
    }

    private static SvgPicture? Open(Stream stream, Uri? baseUri, Svg.SvgExternalResourcePolicy external)
    {
        try
        {
            using var svg = new SKSvg();
            var parameters = new Svg.Model.SvgParameters(null, null, null, new Svg.SvgDocumentLoadOptions { ExternalResources = external });
            if (svg.Load(stream, parameters, baseUri) is not { } picture)
                return null;
            // One with no size takes its shapes' (an empty one 1x1, so one with none isn't taken).
            var bounds = picture.CullRect;
            if (svg.SourceDocument?.Descendants().OfType<Svg.SvgVisualElement>().Any() != true
                || bounds.Width <= 0 || bounds.Height <= 0 || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height))
                return null;
            // Recorded again, from 0,0, into a picture of its own: the parser's goes with it.
            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(new SKRect(0, 0, bounds.Width, bounds.Height));
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            return new SvgPicture(recorder.EndRecording(), bounds.Width, bounds.Height);
        }
        // A third party's parser of any file at all: whatever it throws means the file can't be shown.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
