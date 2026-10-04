using System.Runtime.InteropServices;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using SkiaSharp;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>
/// How often the real model hits what it is asked to click. Fake screens show labelled targets in
/// hard conditions (tiny, dense, faint, blurry, compressed, dark, at the edges, near-identical
/// neighbours, text links, partly covered, a second monitor on either side, ultrawide and portrait
/// screens); the model gets the normal system prompt and tools, is asked to click one target, and
/// the click is checked against the target's bounds.
/// Opt-in like <see cref="ServerIntegrationTests"/>: set BB_SERVER_URL and BB_SERVER_MODEL. Optional:
/// BB_CLICK_SCENES (comma-separated, default all), BB_CLICK_TRIALS (per scene, default 5),
/// BB_CLICK_COORDS=pixels|grid (default: what BlackBox picks for the model), BB_CLICK_RES (a
/// <see cref="ScreenshotResolution"/> name), BB_CLICK_SEED (first layout, default 1000), BB_CLICK_TAG (report name). The report goes to
/// bb-click-TAG.txt in the temp folder, with the first screenshot of each scene next to it.
/// </summary>
public class ClickAccuracyTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
    private static readonly string? Url = Env("BB_SERVER_URL");
    private static readonly string? Model = Env("BB_SERVER_MODEL");
    private static readonly string? Coords = Env("BB_CLICK_COORDS");

    private static readonly string[] Words =
    [
        "Save", "Open", "Close", "Export", "Import", "Print", "Undo", "Redo", "Copy", "Paste", "Delete", "Rename",
        "Share", "Upload", "Refresh", "Search", "Filter", "Sort", "Apply", "Cancel", "Help", "Settings", "Profile",
        "Logout", "Archive", "Restore", "Merge", "Split", "Rotate", "Crop", "Zoom", "Preview", "Publish", "Draft",
        "Send", "Reply", "Forward", "Attach", "Insert", "Format", "Tools", "View", "Window", "Edit", "File", "Run",
        "Debug", "Build", "Deploy", "Commit", "Push", "Pull", "Fetch", "Branch", "Tag", "Stash", "Review", "Approve",
        "Reject", "Assign", "Label", "Pin", "Mute", "Block", "Report", "Invite", "Join", "Leave", "Follow", "Like",
        "Bookmark", "History", "Library", "Account", "Billing", "Security", "Privacy", "Network", "Display", "Sound",
        "Battery", "Storage", "Update", "Backup", "Sync", "Login", "Register", "Next", "Back", "Finish", "Skip",
    ];

    private static readonly string[][] SimilarGroups =
    [
        ["Save", "Save As", "Save All", "Save Copy"], ["Open", "Open Recent", "Open Folder", "Open File"],
        ["Close", "Close All", "Close Tab", "Close Others"], ["Export", "Export PDF", "Export CSV", "Export All"],
        ["Copy", "Copy Path", "Copy Link", "Copy All"], ["Delete", "Delete All", "Deleted", "Delete Row"],
        ["Item 12", "Item 21", "Item 112", "Item 121"], ["Page 1", "Page 11", "Page 7", "Page 17"],
    ];

    private sealed record Target(string Label, ScreenRect Bounds);

    private sealed record Theme(SKColor Background, SKColor Face, SKColor Border, SKColor Ink)
    {
        public static readonly Theme Light = new(new(236, 238, 242), SKColors.White, new(150, 156, 168), new(20, 22, 26));
        public static readonly Theme Dark = new(new(30, 31, 34), new(52, 54, 59), new(80, 83, 90), new(222, 224, 228));
        public static readonly Theme Faint = new(new(240, 241, 243), new(240, 241, 243), SKColors.Transparent, new(176, 179, 185));
    }

    private enum Degrade { None, Blurry, Jpeg }

    private delegate List<Target> Draw(Random rng, SKCanvas canvas, int width, int height, Theme theme, List<string> labels);

    private sealed record Scene(string Name, ScreenRect[] Screens, int On, Draw Draw, Theme? Theme = null, string Noun = "button", Degrade Degrade = Degrade.None);

    private static readonly ScreenRect Primary = new(0, 0, 2560, 1080);

    private static Draw Buttons(float font, int height, int count, int gap) =>
        (rng, c, w, h, theme, labels) => Scatter(rng, c, w, h, theme, labels.Take(count), font, height, gap);

    private static readonly Scene[] Scenes =
    [
        new("buttons", [Primary], 0, Buttons(15, 26, 30, 24)),
        new("tiny", [Primary], 0, Buttons(10, 15, 60, 10)),
        new("dense", [Primary], 0, DenseTable, Noun: "cell"),
        new("faint", [Primary], 0, Buttons(12, 18, 40, 16), Theme.Faint),
        new("blurry", [Primary], 0, Buttons(12, 18, 40, 16), Degrade: Degrade.Blurry),
        new("jpeg", [Primary], 0, Buttons(12, 18, 40, 16), Degrade: Degrade.Jpeg),
        new("dark", [Primary], 0, Buttons(12, 20, 40, 16), Theme.Dark),
        new("edges", [Primary], 0, Edges),
        new("similar", [Primary], 0, Similar),
        new("links", [Primary], 0, Links, Noun: "link"),
        new("hidden", [Primary], 0, Hidden),
        new("right", [Primary, new(2560, 0, 1920, 1080)], 1, Buttons(12, 20, 30, 20)),
        new("left", [Primary, new(-1920, 0, 1920, 1080)], 1, Buttons(12, 20, 30, 20)),
        new("ultrawide", [new(0, 0, 5120, 1440)], 0, Buttons(12, 20, 50, 20)),
        new("portrait", [new(0, 0, 1080, 1920)], 0, Buttons(12, 20, 30, 20)),
    ];

    [Fact]
    public async Task Model_clicks_the_named_target()
    {
        Assert.SkipWhen(Url is null || Model is null, "Set BB_SERVER_URL and BB_SERVER_MODEL to run.");
        var server = new ServerSettings { BaseUrl = Url!, Model = Model! };
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(10));
        var context = await ServerProbe.EnsureLoadedAsync(http, server, null, CancellationToken.None);
        var trials = int.TryParse(Env("BB_CLICK_TRIALS"), out var n) ? n : 5;
        var only = Env("BB_CLICK_SCENES")?.Split(',', StringSplitOptions.TrimEntries);
        var resolution = Env("BB_CLICK_RES") is { } res ? Enum.Parse<ScreenshotResolution>(res) : new AgentSettings().ScreenshotResolution;
        var tag = Env("BB_CLICK_TAG") ?? "accuracy";
        var seed = int.TryParse(Env("BB_CLICK_SEED"), out var s0) ? s0 : 1000;
        var grid = Coords switch { "pixels" => null, "grid" => 1000, _ => ModelCoordinates.GridFor(Model) };

        var report = new List<string>();
        var summary = new List<string>();
        void Log(string line) { report.Add(line); TestContext.Current.SendDiagnosticMessage(line); }
        var (allHits, allTrials) = (0, 0);
        foreach (var scene in Scenes.Where(s => only is null || only.Contains(s.Name)))
        {
            var hits = 0;
            var misses = new List<double>();
            for (var trial = 0; trial < trials; trial++)
            {
                var rng = new Random(seed + trial);
                var (desktop, origin, targets) = Render(scene, rng);
                var target = targets[rng.Next(targets.Count)];
                var platform = new FakePlatform { Screens = scene.Screens.ToList(), ScreenBounds = scene.Screens[0], Desktop = desktop, DesktopOrigin = origin };
                var settings = new AgentSettings { ScreenshotResolution = resolution };
                var ctx = new ToolContext { Platform = platform, Settings = settings, WorkingDirectory = Path.GetTempPath(), CoordinateGrid = grid };
                if (trial == 0)
                    File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"bb-click-{tag}-{scene.Name}.jpg"), ScreenshotTool.Take(new ToolContext
                    {
                        Platform = platform, Settings = settings, WorkingDirectory = "", CoordinateGrid = grid, CurrentScreen = scene.On,
                    }).Image!);
                var connection = BackendConnection.Create(http, server);
                var prompt = SystemPrompt.Build(platform.Name, ctx.ScreenSpaces(), Path.GetTempPath(), "");
                var session = new AgentSession(connection.Client, connection.Options, ToolRegistry.CreateDefault(true), ctx, prompt, contextTokens: context ?? 0);

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var observer = new FirstClickObserver(cts);
                var where = scene.On > 0 ? $" It is on screen {scene.On + 1}." : "";
                await session.RunAsync($"Click the \"{target.Label}\" {scene.Noun}.{where}", observer, cts.Token);

                var click = platform.Actions.LastOrDefault(a => a.StartsWith("move "));
                var label = $"{scene.Name,-9} #{trial} {target.Label,-12}";
                if (click is null || observer.Args is null)
                {
                    Log($"{label} no click [{observer.Calls}]");
                    misses.Add(double.NaN);
                    continue;
                }
                var p = click[5..].Split(',').Select(int.Parse).ToArray();
                var b = target.Bounds;
                var hit = b.Contains(p[0], p[1]);
                hits += hit ? 1 : 0;
                // How far outside the target the click landed (0 for a hit).
                var dx = Math.Max(Math.Max(b.X - p[0], p[0] - (b.X + b.Width - 1)), 0);
                var dy = Math.Max(Math.Max(b.Y - p[1], p[1] - (b.Y + b.Height - 1)), 0);
                if (!hit) misses.Add(Math.Sqrt(dx * dx + dy * dy));
                Log($"{label} {(hit ? "HIT " : "miss")} model said {observer.Args} -> ({p[0]}, {p[1]}); target {b.X},{b.Y} {b.Width}x{b.Height}" +
                    (hit ? "" : $", {dx},{dy} px outside") + $" [{observer.Calls}]");
            }
            allHits += hits;
            allTrials += trials;
            var far = misses.Count(m => double.IsNaN(m) || m > 40);
            summary.Add($"{scene.Name,-9} {hits}/{trials}" + (misses.Count > 0 ? $"  misses: {misses.Count - far} near (≤40px), {far} far or none" : ""));
        }
        foreach (var line in summary)
            Log(line);
        Log($"TOTAL {allHits}/{allTrials} with {Model}, coordinates: {grid?.ToString() ?? "pixels"}, screenshots: {resolution}");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), $"bb-click-{tag}.txt"), report);
    }

    /// <summary>The whole desktop with the scene on its screen and plain buttons on the others; targets in desktop coordinates.</summary>
    private static (RawImage Desktop, (int X, int Y) Origin, List<Target> Targets) Render(Scene scene, Random rng)
    {
        var left = scene.Screens.Min(s => s.X);
        var top = scene.Screens.Min(s => s.Y);
        var width = scene.Screens.Max(s => s.X + s.Width) - left;
        var height = scene.Screens.Max(s => s.Y + s.Height) - top;
        var labels = Words.OrderBy(_ => rng.Next()).ToList();
        var theme = scene.Theme ?? Theme.Light;
        using var desktop = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        List<Target> targets = [];
        using (var canvas = new SKCanvas(desktop))
        {
            for (var i = 0; i < scene.Screens.Length; i++)
            {
                var s = scene.Screens[i];
                using var screen = new SKBitmap(new SKImageInfo(s.Width, s.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                using (var c = new SKCanvas(screen))
                {
                    c.Clear(theme.Background);
                    if (i == scene.On)
                        targets = scene.Draw(rng, c, s.Width, s.Height, theme, labels)
                            .Select(t => t with { Bounds = t.Bounds with { X = t.Bounds.X + s.X, Y = t.Bounds.Y + s.Y } }).ToList();
                    else // other labels, so the target only appears on its own screen
                        Scatter(rng, c, s.Width, s.Height, theme, labels.Skip(60), 12, 20, 20);
                }
                using var degraded = i == scene.On ? Apply(scene.Degrade, screen) : screen.Copy();
                canvas.DrawBitmap(degraded, s.X - left, s.Y - top);
            }
        }
        var pixels = new byte[width * height * 4];
        Marshal.Copy(desktop.GetPixels(), pixels, 0, pixels.Length);
        return (new RawImage(width, height, pixels), (left, top), targets);
    }

    private static SKBitmap Apply(Degrade degrade, SKBitmap screen)
    {
        switch (degrade)
        {
            case Degrade.Blurry:
            {
                // Half resolution and back, then a slight blur: a low-res remote desktop or a scaled app.
                using var half = screen.Resize(new SKImageInfo(screen.Width / 2, screen.Height / 2), SKFilterQuality.Low);
                using var back = half.Resize(screen.Info, SKFilterQuality.Low);
                var result = new SKBitmap(screen.Info);
                using var c = new SKCanvas(result);
                using var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(1.2f, 1.2f) };
                c.DrawBitmap(back, 0, 0, blur);
                return result;
            }
            case Degrade.Jpeg:
            {
                using var image = SKImage.FromBitmap(screen);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 10);
                using var decoded = SKBitmap.Decode(data);
                return decoded.Copy(SKColorType.Bgra8888);
            }
            default:
                return screen.Copy();
        }
    }

    private static SKPaint Font(float size, SKColor color) =>
        new() { Typeface = SKTypeface.FromFamilyName("Segoe UI"), TextSize = size, IsAntialias = true, Color = color };

    private static void DrawButton(SKCanvas c, ScreenRect b, string label, Theme theme, SKPaint font)
    {
        var r = new SKRect(b.X, b.Y, b.X + b.Width, b.Y + b.Height);
        using var face = new SKPaint { Color = theme.Face, IsAntialias = true };
        using var border = new SKPaint { Color = theme.Border, IsStroke = true, StrokeWidth = 1, IsAntialias = true };
        c.DrawRoundRect(r, 3, 3, face);
        c.DrawRoundRect(r, 3, 3, border);
        c.DrawText(label, b.X + (b.Width - font.MeasureText(label)) / 2, b.Y + (b.Height + font.TextSize * 0.7f) / 2, font);
    }

    /// <summary>Buttons at random places that don't overlap.</summary>
    private static List<Target> Scatter(Random rng, SKCanvas c, int width, int height, Theme theme, IEnumerable<string> labels, float fontSize, int buttonHeight, int gap)
    {
        using var font = Font(fontSize, theme.Ink);
        var placed = new List<Target>();
        foreach (var label in labels)
        {
            var w = (int)font.MeasureText(label) + (int)fontSize;
            for (var attempt = 0; attempt < 300; attempt++)
            {
                var r = new ScreenRect(rng.Next(8, width - w - 8), rng.Next(8, height - buttonHeight - 8), w, buttonHeight);
                if (placed.Any(t => Overlaps(t.Bounds, r, gap)))
                    continue;
                placed.Add(new Target(label, r));
                DrawButton(c, r, label, theme, font);
                break;
            }
        }
        return placed;
    }

    private static bool Overlaps(ScreenRect a, ScreenRect b, int gap) =>
        a.X < b.X + b.Width + gap && b.X < a.X + a.Width + gap && a.Y < b.Y + b.Height + gap && b.Y < a.Y + a.Height + gap;

    /// <summary>A spreadsheet-like table filling the screen; every cell holds a different code.</summary>
    private static List<Target> DenseTable(Random rng, SKCanvas c, int width, int height, Theme theme, List<string> labels)
    {
        const int cellW = 110, cellH = 20, x0 = 30, y0 = 40;
        using var font = Font(12, theme.Ink);
        using var line = new SKPaint { Color = new SKColor(205, 208, 214), StrokeWidth = 1, IsStroke = true };
        var cells = new List<Target>();
        var used = new HashSet<string>();
        for (var y = y0; y + cellH <= height - 10; y += cellH)
            for (var x = x0; x + cellW <= width - 10; x += cellW)
            {
                string code;
                do code = $"{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))}-{rng.Next(100, 1000)}";
                while (!used.Add(code));
                c.DrawRect(x, y, cellW, cellH, line);
                c.DrawText(code, x + 6, y + 15, font);
                cells.Add(new Target(code, new ScreenRect(x, y, cellW, cellH)));
            }
        return cells;
    }

    /// <summary>Small buttons flush against the edges and in the corners.</summary>
    private static List<Target> Edges(Random rng, SKCanvas c, int width, int height, Theme theme, List<string> labels)
    {
        using var font = Font(12, theme.Ink);
        var placed = new List<Target>();
        var corner = 0;
        foreach (var label in labels.Take(24))
        {
            var w = (int)font.MeasureText(label) + 12;
            const int h = 20;
            for (var attempt = 0; attempt < 300; attempt++)
            {
                var r = (corner < 4 ? corner : rng.Next(4) + 4) switch
                {
                    0 => new ScreenRect(0, 0, w, h),
                    1 => new ScreenRect(width - w, 0, w, h),
                    2 => new ScreenRect(0, height - h, w, h),
                    3 => new ScreenRect(width - w, height - h, w, h),
                    4 => new ScreenRect(rng.Next(width - w), 0, w, h),
                    5 => new ScreenRect(rng.Next(width - w), height - h, w, h),
                    6 => new ScreenRect(0, rng.Next(height - h), w, h),
                    _ => new ScreenRect(width - w, rng.Next(height - h), w, h),
                };
                if (placed.Any(t => Overlaps(t.Bounds, r, 16)))
                    continue;
                corner++;
                placed.Add(new Target(label, r));
                DrawButton(c, r, label, theme, font);
                break;
            }
        }
        return placed;
    }

    /// <summary>Toolbars of near-identical buttons packed 2px apart ("Save", "Save As", "Save All"…).</summary>
    private static List<Target> Similar(Random rng, SKCanvas c, int width, int height, Theme theme, List<string> labels)
    {
        using var font = Font(12, theme.Ink);
        var placed = new List<Target>();
        var blocks = new List<ScreenRect>();
        foreach (var group in SimilarGroups)
        {
            var items = group.OrderBy(_ => rng.Next()).ToList();
            var widths = items.Select(l => (int)font.MeasureText(l) + 12).ToList();
            var total = widths.Sum() + 2 * (items.Count - 1);
            for (var attempt = 0; attempt < 300; attempt++)
            {
                var block = new ScreenRect(rng.Next(8, width - total - 8), rng.Next(8, height - 30), total, 22);
                if (blocks.Any(b => Overlaps(b, block, 30)))
                    continue;
                blocks.Add(block);
                var x = block.X;
                for (var i = 0; i < items.Count; i++)
                {
                    var r = new ScreenRect(x, block.Y, widths[i], 22);
                    placed.Add(new Target(items[i], r));
                    DrawButton(c, r, items[i], theme, font);
                    x += widths[i] + 2;
                }
                break;
            }
        }
        return placed;
    }

    /// <summary>Paragraphs of text with some words as links.</summary>
    private static List<Target> Links(Random rng, SKCanvas c, int width, int height, Theme theme, List<string> labels)
    {
        string[] filler = ["the", "report", "shows", "that", "our", "team", "will", "review", "each", "change", "before", "release", "and", "then", "update", "notes", "for", "all", "users", "who", "asked", "about", "it"];
        using var text = Font(14, theme.Ink);
        using var link = Font(14, new SKColor(26, 92, 200));
        using var underline = new SKPaint { Color = new SKColor(26, 92, 200), StrokeWidth = 1 };
        var space = text.MeasureText(" ");
        var links = new List<Target>();
        var next = 0;
        for (var y = 60f; y < height - 40; y += 24)
        {
            if (rng.Next(6) == 0) { y += 20; continue; } // paragraph break
            var x = 80f;
            while (true)
            {
                var isLink = next < 40 && rng.Next(9) == 0;
                var word = isLink ? labels[next] : filler[rng.Next(filler.Length)];
                var paint = isLink ? link : text;
                var w = paint.MeasureText(word);
                if (x + w > Math.Min(width - 80, 1800)) break;
                c.DrawText(word, x, y, paint);
                if (isLink)
                {
                    c.DrawLine(x, y + 2, x + w, y + 2, underline);
                    links.Add(new Target(word, new ScreenRect((int)x, (int)y - 15, (int)Math.Ceiling(w), 20)));
                    next++;
                }
                x += w + space;
            }
        }
        return links;
    }

    /// <summary>Buttons whose right part is covered by another window; only the visible part counts.</summary>
    private static List<Target> Hidden(Random rng, SKCanvas c, int width, int height, Theme theme, List<string> labels)
    {
        using var font = Font(13, theme.Ink);
        using var panel = new SKPaint { Color = new SKColor(214, 218, 225) };
        using var edge = new SKPaint { Color = new SKColor(120, 126, 138), IsStroke = true, StrokeWidth = 1 };
        var targets = Scatter(rng, c, width, height, theme, labels.Take(25), 13, 24, 140);
        var visible = new List<Target>();
        foreach (var t in targets)
        {
            var cover = (int)(t.Bounds.Width * 0.4);
            var px = t.Bounds.X + t.Bounds.Width - cover;
            c.DrawRect(px, t.Bounds.Y - 12, 120, t.Bounds.Height + 24, panel);
            c.DrawRect(px, t.Bounds.Y - 12, 120, t.Bounds.Height + 24, edge);
            visible.Add(t with { Bounds = t.Bounds with { Width = t.Bounds.Width - cover } });
        }
        return visible;
    }

    /// <summary>Stops the run after the first click, or after a few calls without one.</summary>
    private sealed class FirstClickObserver(CancellationTokenSource cts) : IAgentObserver
    {
        public string? Args { get; private set; }
        public string Calls { get; private set; } = "";
        private int _count;

        public void OnStepStarted(int step) { }
        public void OnDelta(StreamDelta delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public Task<bool> RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct) => Task.FromResult(true);
        public void OnNotice(string text) { }
        public void OnFinished(AgentStopReason reason, string? detail) { }

        public void OnToolStarted(ToolCall call, string summary)
        {
            Calls += (Calls.Length > 0 ? ", " : "") + summary;
            if (call.Name.Contains("click", StringComparison.OrdinalIgnoreCase))
                Args = call.Arguments;
        }

        public void OnToolFinished(ToolCall call, ToolResult result)
        {
            if (Args is not null || ++_count >= 4)
                cts.Cancel();
        }
    }
}
