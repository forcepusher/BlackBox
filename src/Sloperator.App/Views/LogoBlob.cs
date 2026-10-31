using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace Sloperator.App.Views;

/// <summary>
/// The logo over the start screen's heading, drawn from its shapes (docs/logo/blob.svg) at the size
/// it is shown, with the body's top, bottom and sides on whole pixels of the screen. Assets/sloperator.png
/// (256 px) shrunk to 40 came out soft: its edges smeared over two or three pixels, and its crown and
/// base fell partway into a row (the user, 2026-10-30: "looks a tad blurry", at 100%).
/// </summary>
public sealed class LogoBlob : Control
{
    // docs/logo/blob.svg, in its 128 x 128 box.
    private static readonly Geometry Body = Geometry.Parse(
        "M64.3 10.8C108 10.4 128 46 127.6 92C128.5 101 113 118.2 64.3 118.2C15.6 118.2 0.1 101 1 92C0.6 46 20.6 10.4 64.3 10.8Z");
    private static readonly Geometry Mouth = Geometry.Parse(
        "M36 50C44 51 52 54.5 60 55L71 55.2C73 55.3 73.5 56.5 73.5 58.5C73.5 69 64 77.5 54 77.5C42 77.5 34.5 68 32.2 57C31.6 53 33 49.8 36 50Z");
    private static readonly (Point Center, double Rx, double Ry, double Turn)[] Eyes =
    [
        (new Point(36.96, 35.94), 8.54, 10.25, 27.3),
        (new Point(79.61, 43.74), 8.68, 10.52, 10.2),
    ];
    private static readonly IBrush Yellow = new ImmutableSolidColorBrush(Color.Parse("#FCC21B"));
    private static readonly IBrush Dark = new ImmutableSolidColorBrush(Color.Parse("#2F2F2F"));
    private static readonly IBrush Orange = new ImmutableSolidColorBrush(Color.Parse("#ED6C30"));

    // The body's furthest points in the SVG's units (its curves bulge a little past their ends).
    private const double Left = 0.96, Right = 127.64, Top = 10.8, Bottom = 118.2, Box = 128;

    private static Bitmap? _picture;

    public LogoBlob()
    {
        // Copy image gives the logo as the app has it.
        FileMenu.SetImage(this, _picture ??= new Bitmap(AssetLoader.Open(new Uri("avares://Sloperator/Assets/sloperator.png"))));
    }

    public override void Render(DrawingContext context)
    {
        // The layout puts the control itself on whole pixels (UseLayoutRounding), so its own
        // pixels are whole ones of the screen too.
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var side = Math.Min(Bounds.Width, Bounds.Height) * scale;
        var x = (Bounds.Width * scale - side) / 2;
        var y = (Bounds.Height * scale - side) / 2;
        double Snap(double at, double offset) => Math.Round(offset + at * side / Box);
        var (l, r, t, b) = (Snap(Left, x), Snap(Right, x), Snap(Top, y), Snap(Bottom, y));
        var sx = (r - l) / (Right - Left);
        var sy = (b - t) / (Bottom - Top);
        var map = new Matrix(sx / scale, 0, 0, sy / scale, (l - Left * sx) / scale, (t - Top * sy) / scale);
        using (context.PushTransform(map))
        {
            context.DrawGeometry(Yellow, null, Body);
            foreach (var (center, rx, ry, turn) in Eyes)
            {
                var around = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(turn * Math.PI / 180)
                    * Matrix.CreateTranslation(center.X, center.Y);
                using (context.PushTransform(around))
                    context.DrawEllipse(Dark, null, center, rx, ry);
            }
            context.DrawGeometry(Orange, null, Mouth);
        }
    }
}
