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
/// How often the real model hits what it is asked to click. A fake 2560x1080 screen (the size of
/// the user's main monitor) shows small labelled buttons; the model gets the normal system prompt
/// and tools, is asked to click one button, and the click is checked against the button's bounds.
/// Opt-in like <see cref="ServerIntegrationTests"/>: set BB_SERVER_URL and BB_SERVER_MODEL.
/// BB_CLICK_TRIALS sets the number of trials (default 16); BB_CLICK_COORDS=pixels or grid overrides the coordinates the model is given (default: what BlackBox picks for the model). The report goes to bb-click-accuracy.txt in the temp folder.
/// </summary>
public class ClickAccuracyTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("BB_SERVER_URL");
    private static readonly string? Model = Environment.GetEnvironmentVariable("BB_SERVER_MODEL");
    private static readonly string? Coords = Environment.GetEnvironmentVariable("BB_CLICK_COORDS");

    private static readonly string[] Words =
    [
        "Save", "Open", "Close", "Export", "Import", "Print", "Undo", "Redo", "Copy", "Paste", "Delete", "Rename",
        "Share", "Upload", "Refresh", "Search", "Filter", "Sort", "Apply", "Cancel", "Help", "Settings", "Profile",
        "Logout", "Archive", "Restore", "Merge", "Split", "Rotate", "Crop", "Zoom", "Preview", "Publish", "Draft",
        "Send", "Reply", "Forward", "Attach", "Insert", "Format", "Tools", "View", "Window", "Edit", "File", "Run",
    ];

    private sealed record Target(string Label, ScreenRect Bounds);

    [Fact]
    public async Task Model_clicks_the_named_button()
    {
        Assert.SkipWhen(Url is null || Model is null, "Set BB_SERVER_URL and BB_SERVER_MODEL to run.");
        var server = new ServerSettings { BaseUrl = Url!, Model = Model! };
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(10));
        var context = await ServerProbe.EnsureLoadedAsync(http, server, null, CancellationToken.None);
        var trials = int.TryParse(Environment.GetEnvironmentVariable("BB_CLICK_TRIALS"), out var n) ? n : 16;

        var report = new List<string>();
        void Log(string line) { report.Add(line); TestContext.Current.SendDiagnosticMessage(line); }
        var hits = 0;
        var screen = new ScreenRect(0, 0, 2560, 1080);
        for (var trial = 0; trial < trials; trial++)
        {
            var rng = new Random(1000 + trial);
            var targets = Layout(rng, screen);
            var target = targets[rng.Next(targets.Count)];
            var platform = new FakePlatform { ScreenBounds = screen, Desktop = Render(screen, targets) };
            var settings = new AgentSettings();
            var ctx = new ToolContext { Platform = platform, Settings = settings, WorkingDirectory = Path.GetTempPath(), CoordinateGrid = Coords switch { "pixels" => null, "grid" => 1000, _ => ModelCoordinates.GridFor(Model) } };
            if (trial == 0)
                File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "bb-click-accuracy.jpg"), ctx.GetScreenSpace().Encode(platform.Desktop));
            var connection = BackendConnection.Create(http, server);
            var prompt = SystemPrompt.Build(platform.Name, ctx.ScreenSpaces(), Path.GetTempPath(), "");
            var session = new AgentSession(connection.Client, connection.Options, ToolRegistry.CreateDefault(true), ctx, prompt, contextTokens: context ?? 0);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var observer = new FirstClickObserver(cts);
            await session.RunAsync($"Click the \"{target.Label}\" button.", observer, cts.Token);

            var click = platform.Actions.LastOrDefault(a => a.StartsWith("move "));
            if (click is null || observer.Args is null)
            {
                Log($"#{trial,2} {target.Label,-9} no click ({observer.Calls})");
                continue;
            }
            var p = click[5..].Split(',').Select(int.Parse).ToArray();
            var hit = target.Bounds.Contains(p[0], p[1]);
            hits += hit ? 1 : 0;
            var cx = target.Bounds.X + target.Bounds.Width / 2;
            var cy = target.Bounds.Y + target.Bounds.Height / 2;
            Log($"#{trial,2} {target.Label,-9} {(hit ? "HIT " : "miss")} model said {observer.Args} -> screen ({p[0]}, {p[1]}), " +
                $"button centre ({cx}, {cy}) off by ({p[0] - cx}, {p[1] - cy}) [{observer.Calls}]");
        }
        Log($"{hits}/{trials} hits with {Model}, coordinates: {Coords ?? "default"}");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "bb-click-accuracy.txt"), report);
    }

    /// <summary>Scattered buttons that don't overlap, each with a different label.</summary>
    private static List<Target> Layout(Random rng, ScreenRect screen)
    {
        var words = Words.OrderBy(_ => rng.Next()).Take(30).ToList();
        var placed = new List<Target>();
        using var font = new SKPaint { Typeface = SKTypeface.FromFamilyName("Segoe UI"), TextSize = 15, IsAntialias = true, Color = new SKColor(20, 22, 26) };
        foreach (var word in words)
        {
            var w = (int)font.MeasureText(word) + 24;
            const int h = 26;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var r = new ScreenRect(rng.Next(20, screen.Width - w - 20), rng.Next(60, screen.Height - h - 20), w, h);
                if (placed.Any(t => Overlaps(t.Bounds, r, 24)))
                    continue;
                placed.Add(new Target(word, r));
                break;
            }
        }
        return placed;
    }

    private static bool Overlaps(ScreenRect a, ScreenRect b, int gap) =>
        a.X < b.X + b.Width + gap && b.X < a.X + a.Width + gap && a.Y < b.Y + b.Height + gap && b.Y < a.Y + a.Height + gap;

    private static RawImage Render(ScreenRect screen, List<Target> targets)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(screen.Width, screen.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(236, 238, 242));
            using var bar = new SKPaint { Color = new SKColor(44, 48, 56) };
            canvas.DrawRect(0, 0, screen.Width, 40, bar);
            using var face = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var border = new SKPaint { Color = new SKColor(150, 156, 168), IsStroke = true, StrokeWidth = 1, IsAntialias = true };
            using var font = new SKPaint { Typeface = SKTypeface.FromFamilyName("Segoe UI"), TextSize = 15, IsAntialias = true, Color = new SKColor(20, 22, 26) };
            foreach (var t in targets)
            {
                var r = new SKRect(t.Bounds.X, t.Bounds.Y, t.Bounds.X + t.Bounds.Width, t.Bounds.Y + t.Bounds.Height);
                canvas.DrawRoundRect(r, 4, 4, face);
                canvas.DrawRoundRect(r, 4, 4, border);
                canvas.DrawText(t.Label, r.Left + 12, r.Top + 18, font);
            }
        }
        var pixels = new byte[screen.Width * screen.Height * 4];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return new RawImage(screen.Width, screen.Height, pixels);
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
