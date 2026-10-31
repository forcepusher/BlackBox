using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using SkiaSharp;

namespace Sloperator.Core.Tools;

/// <summary>
/// A picture of what the agent made, for the user: a window, a box on the screen, or the whole
/// screen. In test chats no model ever showed its finished work in its final reply: there was no
/// tool for it, and the screen an action left is folded on its card. The user gets it as a PNG at
/// the screen's own resolution, kept with the chat; the model sees the same crop, to check it.
/// With an earlier capture as <c>before</c>, the user gets the two side by side, labelled.
/// </summary>
public sealed partial class CaptureResultTool : Tool
{
    /// <summary>How many windows an error lists by name, the frontmost first.</summary>
    private const int ListedWindows = 8;

    public override string Name => "capture_result";
    public override string Description =>
        "Capture what you made for the user: a picture of a window or of a box on the screen goes in your answer, and you see it too. " +
        "Use it when you're done, cropped to the work (its window, or a box around the cells, the dialog, the drawing); " +
        "leave both out only when the whole screen is the point. To show a change, capture it before you start too, " +
        "and pass that capture's number as before with the result: the user gets the two side by side.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("window", Str("Part of the title of the window to capture, e.g. \"Paint\" or \"Budget.xlsx\""), false),
        ("x", Num("Left edge of the box to capture, in screenshot coordinates"), false),
        ("y", Num("Top edge of the box, in screenshot coordinates"), false),
        ("width", Num("The box's width, in screenshot coordinates"), false),
        ("height", Num("The box's height, in screenshot coordinates"), false),
        ("caption", Str("One line for the user, shown under the picture"), false),
        ("before", Str("An earlier capture's number (each capture says its number), or the path of a picture: shown beside this one as before and after"), false));

    public override string Describe(JsonObject args)
    {
        var what = GetString(args, "window") is { Length: > 0 } window ? $"Capture the result: {window.Trim()}"
            : HasBox(args) ? string.Create(CultureInfo.InvariantCulture,
                $"Capture the result at ({GetNumber(args, "x")}, {GetNumber(args, "y")}), {GetNumber(args, "width")}x{GetNumber(args, "height")}")
            : "Capture the result: the whole screen";
        return GetString(args, "before")?.Trim() is { Length: > 0 } before
            ? what + (CaptureNumber(before) is { } n ? $", beside capture {n}" : $", beside {Path.GetFileName(before)}")
            : what;
    }

    private static bool HasBox(JsonObject args) => new[] { "x", "y", "width", "height" }.Any(k => args[k] is not null);

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var window = GetString(args, "window")?.Trim();
        if (window is { Length: > 0 } && HasBox(args))
            throw new ToolArgumentException("Give either window or a box (x, y, width, height), not both.");
        var (region, screen, what) = window is { Length: > 0 } ? WindowRegion(ctx, window)
            : HasBox(args) ? BoxRegion(ctx, args)
            : (ctx.GetScreenSpace().Screen, ctx.CurrentScreen, "the screen");
        var folder = ctx.ChatDirectory ?? Path.Combine(ctx.ScratchDirectory ?? Path.GetTempPath(), "captures");
        // Read before anything is captured: a before that isn't there is the model's to fix first.
        using var before = Before(GetString(args, "before")?.Trim(), folder, ctx);

        // Fresh, without the pointer: the click marker and the log are never in a capture.
        var frame = ctx.Platform.Screen.Capture(region, includeCursor: false);
        using var image = Image(frame, out var handle);
        try
        {
            var (file, number) = SaveNext(image, folder);
            var caption = GetString(args, "caption")?.Trim();
            var captioned = caption is { Length: > 0 } ? $", captioned \"{caption}\"" : "";
            if (before is null)
            {
                var text = $"Captured {what} as capture {number}, {frame.Width}x{frame.Height}. " +
                           $"The user sees it at full size in the chat, the last you capture under your final reply{captioned}. " +
                           $"If it doesn't show what they asked for, fix that and capture it again. Taken before a change, pass before={number} " +
                           "when you capture the result, to show the two side by side.";
                return Task.FromResult(new ToolResult(text, ForModel(image, ctx.GetScreenSpace(screen)), ImageCaption: "What the user now sees of your work:", Files: [file]));
            }
            using var both = Compose(before.Picture, image);
            var compared = Path.Combine(folder, $"capture-{number}-compared.png");
            using (var png = both.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = File.Create(compared))
                png.SaveTo(stream);
            var shown = $"Captured {what} as capture {number}, {frame.Width}x{frame.Height}, and put it after {before.Name}: " +
                        $"the user sees the two side by side, labelled Before and After, the last you capture under your final reply{captioned}. " +
                        "If the change doesn't show, fix that and capture it again.";
            return Task.FromResult(new ToolResult(shown, ForModel(both, ctx.GetScreenSpace(screen)), ImageCaption: "What the user now sees of your work, before and after:", Files: [compared]));
        }
        finally
        {
            handle.Free();
        }
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

    // ---- the picture before ---------------------------------------------------------------------------

    /// <summary>The picture to show as before, and what the model calls it ("capture 2", a file's name).</summary>
    private sealed class Earlier(string name, SKBitmap picture) : IDisposable
    {
        public string Name { get; } = name;
        public SKBitmap Picture { get; } = picture;
        public void Dispose() => Picture.Dispose();
    }

    /// <summary>"2", "capture 2", "capture-2.png", "#2": the number of an earlier capture; null for anything else (a path).</summary>
    private static int? CaptureNumber(string before) =>
        CaptureName().Match(before) is { Success: true } m && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var n) ? n : null;

    [GeneratedRegex(@"^(?:capture[\s_-]*)?#?(\d+)(?:\.png)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CaptureName();

    /// <summary>The picture <paramref name="before"/> names: an earlier capture in <paramref name="folder"/>, or a picture file; null for none.</summary>
    private static Earlier? Before(string? before, string folder, ToolContext ctx)
    {
        if (before is not { Length: > 0 })
            return null;
        string path, name;
        if (CaptureNumber(before) is { } number)
        {
            path = Path.Combine(folder, $"capture-{number}.png");
            name = $"capture {number}";
            if (!File.Exists(path))
            {
                var taken = Captures(folder);
                throw new ToolArgumentException($"There is no capture {number} in this chat. " +
                    (taken.Count == 0 ? "Nothing has been captured yet: capture it before you change it." : $"Its captures: {string.Join(", ", taken)}."));
            }
        }
        else
        {
            path = ctx.ResolvePath(before);
            name = Path.GetFileName(path);
            if (!File.Exists(path))
                throw new ToolArgumentException($"There is no picture at {path}. Pass an earlier capture's number or a picture's path.");
        }
        return new Earlier(name, PictureFile.Decode(path) ?? throw new ToolArgumentException($"{path} could not be read as a picture."));
    }

    /// <summary>The numbers of the captures in <paramref name="folder"/>, in order.</summary>
    private static List<int> Captures(string folder) =>
        !Directory.Exists(folder) ? []
        : Directory.EnumerateFiles(folder, "capture-*.png").Select(f => CaptureNumber(Path.GetFileName(f))).OfType<int>().Order().ToList();

    // ---- the pictures --------------------------------------------------------------------------------

    /// <summary>
    /// The picture as a lossless PNG in the chat's folder (capture-1.png, capture-2.png…), its path and number.
    /// Named apart from the conversation's numbered pictures, which ChatStore drops once no message names them.
    /// </summary>
    private static (string Path, int Number) SaveNext(SKImage image, string folder)
    {
        Directory.CreateDirectory(folder);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(folder, $"capture-{n}.png");
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                png.SaveTo(file);
                return (path, n);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
    }

    /// <summary>
    /// Before and after in one picture, each under its label, at one scale (the smaller one's,
    /// never enlarged): side by side, or one over the other, whichever comes out nearer 4:3, as the
    /// chat shows a picture at most 480 wide and two wide windows side by side came out a strip.
    /// </summary>
    internal static SKImage Compose(SKBitmap before, SKImage after)
    {
        // The band and gap grow with the pictures, so a label on a 4K capture reads like one on a small one.
        var unit = Math.Max(1.0, Math.Max(after.Width, after.Height) / 1600.0);
        var (band, gap) = ((int)Math.Round(40 * unit), (int)Math.Round(16 * unit));

        var height = Math.Min(before.Height, after.Height);
        var (wideBefore, wideAfter) = (Scaled(before.Width, before.Height, height), Scaled(after.Width, after.Height, height));
        var (sideW, sideH) = (wideBefore + gap + wideAfter, band + height);
        var width = Math.Min(before.Width, after.Width);
        var (tallBefore, tallAfter) = (Scaled(before.Height, before.Width, width), Scaled(after.Height, after.Width, width));
        var (stackW, stackH) = (width, band + tallBefore + gap + band + tallAfter);
        var sideBySide = Off43(sideW, sideH) <= Off43(stackW, stackH);

        using var surface = SKSurface.Create(new SKImageInfo(sideBySide ? sideW : stackW, sideBySide ? sideH : stackH, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x16, 0x16, 0x16));
        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, (float)(20 * unit)) { Edging = SKFontEdging.Antialias, Subpixel = true };
        using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var beforeImage = SKImage.FromBitmap(before);

        void Draw(SKImage picture, string label, float x, float y, float w, float h)
        {
            canvas.DrawText(label, x, y + band - (float)(13 * unit), SKTextAlign.Left, font, ink);
            canvas.DrawImage(picture, new SKRect(x, y + band, x + w, y + band + h), Sampling.High(picture.Width, (int)w));
        }
        if (sideBySide)
        {
            Draw(beforeImage, "Before", 0, 0, wideBefore, height);
            Draw(after, "After", wideBefore + gap, 0, wideAfter, height);
        }
        else
        {
            Draw(beforeImage, "Before", 0, 0, width, tallBefore);
            Draw(after, "After", 0, band + tallBefore + gap, width, tallAfter);
        }
        return surface.Snapshot();

        static int Scaled(int side, int other, int otherTo) => Math.Max(1, (int)Math.Round((double)side * otherTo / other));
        static double Off43(int w, int h) => Math.Abs(Math.Log((double)w / h / (4.0 / 3)));
    }

    /// <summary>
    /// The picture as the model gets it: no larger than a screenshot of its screen (so within the
    /// model's limit on picture sizes), never enlarged, JPEG at the zoom's quality.
    /// </summary>
    internal static byte[] ForModel(SKImage image, ScreenSpace space)
    {
        var scale = Math.Min(1.0, Math.Min((double)space.ImageWidth / image.Width, (double)space.ImageHeight / image.Height));
        var (w, h) = (Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.DrawImage(image, new SKRect(0, 0, w, h), Sampling.High(image.Width, w));
        using var snapshot = surface.Snapshot();
        using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, ZoomTool.Quality);
        return jpeg.ToArray();
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
