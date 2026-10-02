using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using BlackBox.Core.Platform;
using SkiaSharp;

namespace BlackBox.Core.Tools;

/// <summary>
/// Shows a part of the screen magnified, with rulers and grid lines labelled in screenshot
/// coordinates, so the model can read small text and aim at small buttons without doing math.
/// </summary>
public sealed class ZoomTool : Tool
{
    private const int Margin = 34;

    public override string Name => "zoom";
    public override string Description =>
        "Look at a region of the screen magnified, to read small text or find small buttons precisely. " +
        "Give the region in screenshot coordinates. The zoomed image has rulers and grid lines labelled in screenshot coordinates: " +
        "use those numbers directly with mouse tools.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("x", Num("Left edge of the region (screenshot coordinates)"), true),
        ("y", Num("Top edge of the region"), true),
        ("width", Num("Region width (e.g. 300)"), true),
        ("height", Num("Region height (e.g. 200)"), true));
    public override string Describe(JsonObject args) =>
        $"Zoom into ({GetNumber(args, "x")}, {GetNumber(args, "y")}) {GetNumber(args, "width")}x{GetNumber(args, "height")}";

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var space = ctx.GetScreenSpace();
        double ix = RequireNumber(args, "x"), iy = RequireNumber(args, "y");
        double iw = Math.Max(16, RequireNumber(args, "width")), ih = Math.Max(16, RequireNumber(args, "height"));
        ix = Math.Clamp(ix, 0, space.ImageWidth - 16);
        iy = Math.Clamp(iy, 0, space.ImageHeight - 16);
        iw = Math.Min(iw, space.ImageWidth - ix);
        ih = Math.Min(ih, space.ImageHeight - iy);

        var (sx, sy) = space.ToScreen(ix, iy);
        var (sx2, sy2) = space.ToScreen(ix + iw, iy + ih);
        var region = new ScreenRect(sx, sy, Math.Max(1, sx2 - sx), Math.Max(1, sy2 - sy));
        var frame = ctx.Platform.Screen.Capture(region, includeCursor: true);

        // Magnify up to 4x, within the same pixel budget as a normal screenshot.
        var budget = ctx.Settings.ScreenshotMaxPixels;
        var zoom = Math.Min(4.0, Math.Sqrt(budget / (iw * ih)));
        zoom = Math.Max(zoom, 1.0);
        var outW = (int)Math.Round(iw * zoom);
        var outH = (int)Math.Round(ih * zoom);

        var jpeg = Render(frame, outW, outH, ix, iy, iw, ih);
        return Task.FromResult(new ToolResult(
            $"Zoomed {zoom:0.#}x into region ({ix:0},{iy:0})-({ix + iw:0},{iy + ih:0}). " +
            "Ruler and grid labels are screenshot coordinates; pass them straight to mouse tools.", jpeg));
    }

    private static byte[] Render(RawImage frame, int outW, int outH, double ix, double iy, double iw, double ih)
    {
        using var src = new SKBitmap();
        var handle = GCHandle.Alloc(frame.Bgra, GCHandleType.Pinned);
        try
        {
            src.InstallPixels(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul), handle.AddrOfPinnedObject(), frame.Stride);
            using var surface = SKSurface.Create(new SKImageInfo(outW + Margin, outH + Margin, SKColorType.Bgra8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(32, 32, 36));
            using (var img = SKImage.FromBitmap(src))
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
                canvas.DrawImage(img, new SKRect(Margin, Margin, Margin + outW, Margin + outH), paint);

            var step = NiceStep(Math.Max(iw, ih) / 8);
            using var grid = new SKPaint { Color = new SKColor(255, 0, 200, 90), StrokeWidth = 1 };
            using var tick = new SKPaint { Color = SKColors.White, StrokeWidth = 1 };
            using var text = new SKPaint { Color = SKColors.White, TextSize = 13, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) };

            for (var v = Math.Ceiling(ix / step) * step; v <= ix + iw; v += step)
            {
                var px = Margin + (float)((v - ix) / iw * outW);
                canvas.DrawLine(px, Margin, px, Margin + outH, grid);
                canvas.DrawLine(px, Margin - 6, px, Margin, tick);
                var label = v.ToString("0");
                canvas.DrawText(label, px - text.MeasureText(label) / 2, Margin - 10, text);
            }
            for (var v = Math.Ceiling(iy / step) * step; v <= iy + ih; v += step)
            {
                var py = Margin + (float)((v - iy) / ih * outH);
                canvas.DrawLine(Margin, py, Margin + outW, py, grid);
                canvas.DrawLine(Margin - 6, py, Margin, py, tick);
                var label = v.ToString("0");
                canvas.Save();
                canvas.RotateDegrees(-90, Margin - 10, py);
                canvas.DrawText(label, Margin - 10 - text.MeasureText(label) / 2, py, text);
                canvas.Restore();
            }

            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Rounds a raw step to 5, 10, 20, 25, 50, 100… so labels are easy to read.</summary>
    internal static double NiceStep(double raw)
    {
        foreach (var s in new[] { 5, 10, 20, 25, 50, 100, 200, 250, 500 })
            if (s >= raw) return s;
        return 1000;
    }
}
