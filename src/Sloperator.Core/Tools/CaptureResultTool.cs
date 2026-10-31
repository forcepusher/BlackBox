using System.Globalization;
using System.Text.Json.Nodes;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using SkiaSharp;

namespace Sloperator.Core.Tools;

/// <summary>
/// A picture of what the agent made, for the user: a window, a box on the screen, or the whole
/// screen. In test chats no model ever showed its finished work in its final reply: there was no
/// tool for it, and the screen an action left is folded on its card. The user gets it as a PNG at
/// the screen's own resolution, kept with the chat; the model sees the same crop, to check it.
/// </summary>
public sealed class CaptureResultTool : Tool
{
    /// <summary>How many windows an error lists by name, the frontmost first.</summary>
    private const int ListedWindows = 8;

    public override string Name => "capture_result";
    public override string Description =>
        "Capture what you made for the user: a picture of a window or of a box on the screen goes in your answer, and you see it too. " +
        "Use it when you're done, cropped to the work (its window, or a box around the cells, the dialog, the drawing); " +
        "leave both out only when the whole screen is the point.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("window", Str("Part of the title of the window to capture, e.g. \"Paint\" or \"Budget.xlsx\""), false),
        ("x", Num("Left edge of the box to capture, in screenshot coordinates"), false),
        ("y", Num("Top edge of the box, in screenshot coordinates"), false),
        ("width", Num("The box's width, in screenshot coordinates"), false),
        ("height", Num("The box's height, in screenshot coordinates"), false),
        ("caption", Str("One line for the user, shown under the picture"), false));

    public override string Describe(JsonObject args) =>
        GetString(args, "window") is { Length: > 0 } window ? $"Capture the result: {window.Trim()}"
        : HasBox(args) ? string.Create(CultureInfo.InvariantCulture,
            $"Capture the result at ({GetNumber(args, "x")}, {GetNumber(args, "y")}), {GetNumber(args, "width")}x{GetNumber(args, "height")}")
        : "Capture the result: the whole screen";

    private static bool HasBox(JsonObject args) => new[] { "x", "y", "width", "height" }.Any(k => args[k] is not null);

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var window = GetString(args, "window")?.Trim();
        if (window is { Length: > 0 } && HasBox(args))
            throw new ToolArgumentException("Give either window or a box (x, y, width, height), not both.");
        var (region, screen, what) = window is { Length: > 0 } ? WindowRegion(ctx, window)
            : HasBox(args) ? BoxRegion(ctx, args)
            : (ctx.GetScreenSpace().Screen, ctx.CurrentScreen, "the screen");

        // Fresh, without the pointer: the click marker and the log are never in a capture.
        var frame = ctx.Platform.Screen.Capture(region, includeCursor: false);
        var file = Save(frame, ctx);
        var jpeg = ForModel(frame, ctx.GetScreenSpace(screen));
        var caption = GetString(args, "caption")?.Trim();
        var text = $"Captured {what}, {frame.Width}x{frame.Height}. The user sees it at full size in the chat, the last you capture under your final reply" +
                   (caption is { Length: > 0 } ? $", captioned \"{caption}\"" : "") + ". " +
                   "If it doesn't show what they asked for, fix that and capture it again; otherwise reply with your summary.";
        return Task.FromResult(new ToolResult(text, jpeg, ImageCaption: "What the user now sees of your work:", Files: [file]));
    }

    /// <summary>The frontmost window whose title is <paramref name="title"/> or has it in it, cut to the screen it is mostly on.</summary>
    private static (ScreenRect Region, int Screen, string What) WindowRegion(ToolContext ctx, string title)
    {
        var windows = ctx.Platform.Screen.GetWindows();
        var found = windows.FirstOrDefault(w => w.Title.Trim().Equals(title, StringComparison.OrdinalIgnoreCase)) is { Title.Length: > 0 } exact ? exact
            : windows.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (found.Title is null)
            throw new ToolArgumentException(windows.Count == 0
                ? $"No window's title has \"{title}\" in it: no windows of other programs are open on the screen."
                : $"No window's title has \"{title}\" in it. Windows on the screen: " +
                  string.Join(", ", windows.Take(ListedWindows).Select(w => $"\"{w.Title}\"")) + (windows.Count > ListedWindows ? "…" : "") + ".");
        var screens = ctx.Screens;
        var (best, area) = (-1, 0L);
        for (var i = 0; i < screens.Count; i++)
            if (Intersect(found.Bounds, screens[i]) is var part && (long)part.Width * part.Height > area)
                (best, area) = (i, (long)part.Width * part.Height);
        if (best < 0)
            throw new ToolArgumentException($"The window \"{found.Title}\" is off the screen: move it onto the screen first.");
        return (Intersect(found.Bounds, screens[best]), best, $"the window \"{found.Title}\"");
    }

    /// <summary>The box the model gave, in screenshot coordinates of the screen it looks at, cut to that screen.</summary>
    private static (ScreenRect Region, int Screen, string What) BoxRegion(ToolContext ctx, JsonObject args)
    {
        var (x, y, width, height) = (GetNumber(args, "x"), GetNumber(args, "y"), GetNumber(args, "width"), GetNumber(args, "height"));
        if (x is null || y is null || width is null || height is null)
            throw new ToolArgumentException("A box needs all of x, y (its top-left corner), width and height, in screenshot coordinates.");
        if (width <= 0 || height <= 0)
            throw new ToolArgumentException("The box's width and height must be more than 0.");
        var space = ctx.GetScreenSpace();
        var region = space.ToScreen(x.Value, y.Value, width.Value, height.Value);
        if (region.Width == 0 || region.Height == 0)
            throw new ToolArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"The box ({x}, {y}), {width}x{height} is off the screen, which is {space.ImageWidth}x{space.ImageHeight} in screenshot coordinates."));
        return (region, ctx.CurrentScreen, string.Create(CultureInfo.InvariantCulture, $"the box at ({x}, {y}), {width}x{height}"));
    }

    private static ScreenRect Intersect(ScreenRect a, ScreenRect b)
    {
        var (left, top) = (Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        var (right, bottom) = (Math.Min(a.X + a.Width, b.X + b.Width), Math.Min(a.Y + a.Height, b.Y + b.Height));
        return new ScreenRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// The picture as a lossless PNG in the chat's folder (capture-1.png, capture-2.png…), and its path.
    /// Named apart from the conversation's numbered pictures, which ChatStore drops once no message names them.
    /// </summary>
    private static string Save(RawImage frame, ToolContext ctx)
    {
        var folder = ctx.ChatDirectory ?? Path.Combine(ctx.ScratchDirectory ?? Path.GetTempPath(), "captures");
        Directory.CreateDirectory(folder);
        using var image = Image(frame, out var handle);
        try
        {
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            for (var n = 1; ; n++)
            {
                var path = Path.Combine(folder, $"capture-{n}.png");
                try
                {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                    png.SaveTo(file);
                    return path;
                }
                catch (IOException) when (File.Exists(path)) { }
            }
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// The picture as the model gets it: no larger than a screenshot of its screen (so within the
    /// model's limit on picture sizes), never enlarged, JPEG at the zoom's quality.
    /// </summary>
    internal static byte[] ForModel(RawImage frame, ScreenSpace space)
    {
        var scale = Math.Min(1.0, Math.Min((double)space.ImageWidth / frame.Width, (double)space.ImageHeight / frame.Height));
        var (w, h) = (Math.Max(1, (int)Math.Round(frame.Width * scale)), Math.Max(1, (int)Math.Round(frame.Height * scale)));
        using var image = Image(frame, out var handle);
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.DrawImage(image, new SKRect(0, 0, w, h), Sampling.High(frame.Width, w));
            using var snapshot = surface.Snapshot();
            using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, ZoomTool.Quality);
            return jpeg.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>The frame as an image over its own pixels, pinned until <paramref name="handle"/> is freed.</summary>
    private static SKImage Image(RawImage frame, out System.Runtime.InteropServices.GCHandle handle)
    {
        handle = System.Runtime.InteropServices.GCHandle.Alloc(frame.Bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var pixels = new SKPixmap(info, handle.AddrOfPinnedObject(), frame.Stride);
        return SKImage.FromPixels(pixels);
    }
}
