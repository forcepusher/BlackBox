using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Platform;

namespace BlackBox.Core.Tools;

public sealed class FailsafeTriggeredException()
    : Exception("Stopped: the mouse was moved to the top-left corner of the screen (failsafe).");

/// <summary>Shared behaviour for tools that drive the mouse and keyboard.</summary>
public abstract class DesktopActionTool : Tool
{
    public override ToolRisk Risk => ToolRisk.Desktop;

    public sealed override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        CheckFailsafe(ctx);
        var text = await ActAsync(args, ctx, ct).ConfigureAwait(false);
        if (!ctx.Settings.ScreenshotAfterActions)
            return new ToolResult(text);

        // Give the UI a moment to react before looking at it.
        await Task.Delay(500, ct).ConfigureAwait(false);
        var shot = ScreenshotTool.Take(ctx);
        return shot with { Text = text + "\n" + shot.Text };
    }

    protected abstract Task<string> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct);

    internal static void CheckFailsafe(ToolContext ctx)
    {
        if (!ctx.Settings.FailsafeCorner)
            return;
        var screen = ctx.Platform.Screen.GetPrimaryScreen();
        var (x, y) = ctx.Platform.Input.GetCursorPosition();
        if (x <= screen.X + 1 && y <= screen.Y + 1)
            throw new FailsafeTriggeredException();
    }

    protected static (int X, int Y) Point(JsonObject args, ToolContext ctx, string xKey = "x", string yKey = "y") =>
        ctx.GetScreenSpace().ToScreen(RequireNumber(args, xKey), RequireNumber(args, yKey));

    /// <summary>Moves BlackBox's own window out of the way if the action lands on it.</summary>
    protected static IDisposable Aside(ToolContext ctx, params (int X, int Y)[] points) =>
        ctx.Platform.HostWindow.StepAside(points);

    protected static string Fmt(JsonObject args, string xKey = "x", string yKey = "y") =>
        $"({GetNumber(args, xKey)}, {GetNumber(args, yKey)})";
}

public sealed class ScreenshotTool : Tool
{
    public override string Name => "screenshot";
    public override string Description =>
        "Capture a screen. Returns an image; all x/y coordinates for mouse tools are in this image's pixel space. " +
        "With several monitors, pass screen to look at another one; from then on x/y refer to that screen.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("screen", Int("Which screen to capture: 1 = primary, 2 = the next one... Default: the screen shown last."), false));
    public override string Describe(JsonObject args) =>
        GetNumber(args, "screen") is { } n ? $"Take a screenshot of screen {n}" : "Take a screenshot";

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (GetNumber(args, "screen") is { } n)
        {
            var count = ctx.Screens.Count;
            if (n < 1 || n > count)
                throw new ToolArgumentException(count == 1 ? "There is only one screen." : $"There are {count} screens, numbered 1 to {count}.");
            ctx.CurrentScreen = (int)n - 1;
        }
        return Task.FromResult(Take(ctx));
    }

    internal static ToolResult Take(ToolContext ctx)
    {
        var space = ctx.GetScreenSpace();
        var frame = ctx.Platform.Screen.Capture(space.Screen, includeCursor: true);
        var jpeg = space.Encode(frame);
        var count = ctx.Screens.Count;
        var which = count > 1 ? $"Screenshot of screen {ctx.CurrentScreen + 1} of {count}, {space.ImageWidth}x{space.ImageHeight}." : $"Screenshot {space.ImageWidth}x{space.ImageHeight}.";
        var cursor = ctx.Platform.Input.GetCursorPosition();
        var cursorScreen = ctx.ScreenAt(cursor.X, cursor.Y);
        var (cx, cy) = space.ToImage(cursor.X, cursor.Y);
        var where = cursorScreen == ctx.CurrentScreen ? $"Mouse cursor at ({cx}, {cy})." : $"Mouse cursor is on screen {cursorScreen + 1}.";
        return new ToolResult(which + " " + where, jpeg);
    }
}

public sealed class MouseClickTool : DesktopActionTool
{
    public override string Name => "mouse_click";
    public override string Description => "Move the mouse to (x, y) in screenshot coordinates and click.";
    public override JsonObject Parameters => Schema(
        ("x", Num("X in screenshot pixels"), true),
        ("y", Num("Y in screenshot pixels"), true),
        ("button", Str("Mouse button, default left", "left", "right", "middle"), false),
        ("clicks", Int("1 = single click (default), 2 = double click, 3 = triple click"), false));

    public override string Describe(JsonObject args)
    {
        var clicks = (int)(GetNumber(args, "clicks") ?? 1);
        var kind = clicks switch { 2 => "Double-click", 3 => "Triple-click", _ => "Click" };
        var button = GetString(args, "button") is { } b && b != "left" ? $" ({b})" : "";
        return $"{kind}{button} at {Fmt(args)}";
    }

    protected override async Task<string> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (x, y) = Point(args, ctx);
        var button = ParseButton(GetString(args, "button"));
        var clicks = Math.Clamp((int)(GetNumber(args, "clicks") ?? 1), 1, 3);
        using var aside = Aside(ctx, (x, y));
        ctx.Platform.Input.MoveMouse(x, y);
        await Task.Delay(60, ct).ConfigureAwait(false);
        ctx.Platform.Input.Click(button, clicks);
        return Describe(args) + " done.";
    }

    internal static MouseButton ParseButton(string? s) => s?.ToLowerInvariant() switch
    {
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => MouseButton.Left,
    };
}

public sealed class MouseMoveTool : DesktopActionTool
{
    public override string Name => "mouse_move";
    public override string Description => "Move the mouse to (x, y) in screenshot coordinates without clicking (e.g. to hover).";
    public override JsonObject Parameters => Schema(("x", Num("X in screenshot pixels"), true), ("y", Num("Y in screenshot pixels"), true));
    public override string Describe(JsonObject args) => $"Move mouse to {Fmt(args)}";

    protected override Task<string> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (x, y) = Point(args, ctx);
        using var aside = Aside(ctx, (x, y));
        ctx.Platform.Input.MoveMouse(x, y);
        return Task.FromResult(Describe(args) + " done.");
    }
}

public sealed class MouseDragTool : DesktopActionTool
{
    public override string Name => "mouse_drag";
    public override string Description => "Press the left button at (x, y), drag to (to_x, to_y) and release. Screenshot coordinates.";
    public override JsonObject Parameters => Schema(
        ("x", Num("Start X"), true), ("y", Num("Start Y"), true),
        ("to_x", Num("End X"), true), ("to_y", Num("End Y"), true));
    public override string Describe(JsonObject args) => $"Drag from {Fmt(args)} to {Fmt(args, "to_x", "to_y")}";

    protected override async Task<string> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (x1, y1) = Point(args, ctx);
        var (x2, y2) = Point(args, ctx, "to_x", "to_y");
        var input = ctx.Platform.Input;
        using var aside = Aside(ctx, (x1, y1), (x2, y2));
        input.MoveMouse(x1, y1);
        await Task.Delay(80, ct).ConfigureAwait(false);
        input.MouseDown(MouseButton.Left);
        try
        {
            // Move in steps so apps that track drag motion see it.
            const int steps = 12;
            for (var i = 1; i <= steps; i++)
            {
                input.MoveMouse(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
                await Task.Delay(15, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            input.MouseUp(MouseButton.Left);
        }
        return Describe(args) + " done.";
    }
}

public sealed class ScrollTool : DesktopActionTool
{
    public override string Name => "scroll";
    public override string Description => "Scroll the mouse wheel, optionally after moving to (x, y). One unit is one wheel notch (about 3 lines).";
    public override JsonObject Parameters => Schema(
        ("direction", Str("Scroll direction", "up", "down", "left", "right"), true),
        ("amount", Int("Number of wheel notches, default 3"), false),
        ("x", Num("Optional X to scroll at"), false),
        ("y", Num("Optional Y to scroll at"), false));
    public override string Describe(JsonObject args) =>
        $"Scroll {GetString(args, "direction")} x{GetNumber(args, "amount") ?? 3}" + (args["x"] is not null ? $" at {Fmt(args)}" : "");

    protected override Task<string> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var at = ctx.Platform.Input.GetCursorPosition();
        if (args["x"] is not null && args["y"] is not null)
        {
            at = Point(args, ctx);
            ctx.Platform.Input.MoveMouse(at.X, at.Y);
        }
        using var aside = Aside(ctx, at);
        var amount = Math.Clamp((int)(GetNumber(args, "amount") ?? 3), 1, 50);
        var (dx, dy) = RequireString(args, "direction").ToLowerInvariant() switch
        {
            "up" => (0, -amount),
            "down" => (0, amount),
            "left" => (-amount, 0),
            "right" => (amount, 0),
            var d => throw new ToolArgumentException($"Unknown direction '{d}'."),
        };
        ctx.Platform.Input.Scroll(dx, dy);
        return Task.FromResult(Describe(args) + " done.");
    }
}

public sealed class TypeTextTool : DesktopActionTool
{
    public override string Name => "type_text";
    public override string Description =>
        "Type text into the focused control, exactly as given. Use \\n for Enter. Click the target field first.";
    public override JsonObject Parameters => Schema(("text", Str("Text to type"), true));
    public override string Describe(JsonObject args)
    {
        var t = GetString(args, "text") ?? "";
        return $"Type \"{(t.Length > 60 ? t[..60] + "…" : t)}\"";
    }

    protected override Task<string> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var text = RequireString(args, "text");
        ctx.Platform.HostWindow.YieldFocus();
        ctx.Platform.Input.TypeText(text);
        return Task.FromResult($"Typed {text.Length} characters.");
    }
}

public sealed class PressKeysTool : DesktopActionTool
{
    /// <summary>Pause between the presses of a sequence, so apps (and game animations) keep up.</summary>
    private const int DefaultGapMs = 150;

    public override string Name => "press_keys";
    public override string Description =>
        "Press a key or shortcut, or a whole series of them in one go. For one chord give its keys in order, e.g. [\"ctrl\",\"c\"], [\"alt\",\"tab\"], [\"enter\"]. " +
        "For several presses one after another, give sequence instead, e.g. [\"left\",\"down\",\"left\",\"tab\",\"ctrl+s\"]: much faster than one call per key. " +
        "Names: ctrl, alt, shift, win, enter, esc, tab, backspace, delete, space, up, down, left, right, home, end, pageup, pagedown, f1-f12, a-z, 0-9.";
    public override JsonObject Parameters => Schema(
        ("keys", StrArray("Keys of one chord, modifiers first"), false),
        ("sequence", StrArray("Presses to make one after another; each is a key or a chord like ctrl+s. Use instead of keys."), false),
        ("repeat", Int("How many times to press the chord or run the sequence, default 1"), false),
        ("gap_ms", Int($"Pause between presses in milliseconds, default {DefaultGapMs}"), false));

    public override string Describe(JsonObject args)
    {
        var repeat = GetNumber(args, "repeat") is > 1 and var r ? $" x{r}" : "";
        var sequence = Sequence(args);
        return sequence.Count > 0
            ? $"Press {sequence.Count} keys: {string.Join(" ", sequence.Select(c => string.Join("+", c)))}{repeat}"
            : $"Press {string.Join("+", Keys(args))}{repeat}";
    }

    private static List<string> Keys(JsonObject args)
    {
        var keys = GetStringList(args, "keys") ?? [];
        // Accept "ctrl+c" as a single string as well.
        return keys.SelectMany(Split).ToList();
    }

    private static List<List<string>> Sequence(JsonObject args) =>
        (GetStringList(args, "sequence") ?? []).Select(c => Split(c).ToList()).Where(c => c.Count > 0).ToList();

    private static string[] Split(string chord) =>
        chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    protected override async Task<string> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var presses = Sequence(args);
        if (presses.Count == 0 && Keys(args) is { Count: > 0 } chord)
            presses = [chord];
        if (presses.Count == 0)
            throw new ToolArgumentException("Give 'keys' (one chord) or 'sequence' (several presses).");
        var unknown = presses.SelectMany(p => p).Where(k => !ctx.Platform.Input.IsKnownKey(k)).Distinct().ToList();
        if (unknown.Count > 0)
            throw new ToolArgumentException($"Unknown key name(s): {string.Join(", ", unknown)}.");

        var repeat = Math.Clamp((int)(GetNumber(args, "repeat") ?? 1), 1, 100);
        var gap = Math.Clamp((int)(GetNumber(args, "gap_ms") ?? (presses.Count > 1 ? DefaultGapMs : 40)), 0, 5000);
        ctx.Platform.HostWindow.YieldFocus();
        var total = repeat * presses.Count;
        for (var i = 0; i < total; i++)
        {
            ctx.Platform.Input.PressKeys(presses[i % presses.Count]);
            if (i < total - 1)
                await Task.Delay(gap, ct).ConfigureAwait(false);
        }
        return Describe(args) + " done.";
    }
}

public sealed class WaitTool : Tool
{
    public override string Name => "wait";
    public override string Description => "Wait for a number of seconds (max 30), e.g. for an app to load, then take a screenshot.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(("seconds", Num("Seconds to wait"), true));
    public override string Describe(JsonObject args) => $"Wait {GetNumber(args, "seconds")}s";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var seconds = Math.Clamp(GetNumber(args, "seconds") ?? 1, 0.1, 30);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        var shot = ScreenshotTool.Take(ctx);
        return shot with { Text = $"Waited {seconds:0.#}s.\n{shot.Text}" };
    }
}

public sealed class ListWindowsTool : Tool
{
    public override string Name => "list_windows";
    public override string Description => "List visible top-level windows with their handle, title, process and bounds (in screenshot coordinates).";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema();
    public override string Describe(JsonObject args) => "List open windows";

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var spaces = ctx.ScreenSpaces();
        var sb = new StringBuilder();
        foreach (var w in ctx.Platform.Windows.ListWindows())
        {
            // Bounds in the coordinates of the screen the window is on (where its centre is).
            var screen = ScreenOf(ctx, w);
            var space = spaces[screen];
            var (x, y) = space.ToImage(w.Bounds.X, w.Bounds.Y);
            var (r, b) = space.ToImage(w.Bounds.X + w.Bounds.Width, w.Bounds.Y + w.Bounds.Height);
            var on = spaces.Count > 1 ? $" screen={screen + 1}" : "";
            sb.AppendLine($"handle={w.Handle} process={w.ProcessName} title=\"{w.Title}\"{on} bounds=({x},{y})-({r},{b}){(w.IsMinimized ? " minimized" : "")}");
        }
        return Task.FromResult(new ToolResult(sb.Length > 0 ? sb.ToString() : "No windows found."));
    }

    internal static int ScreenOf(ToolContext ctx, WindowInfo w) =>
        ctx.ScreenAt(w.Bounds.X + w.Bounds.Width / 2, w.Bounds.Y + w.Bounds.Height / 2);
}

public sealed class FocusWindowTool : DesktopActionTool
{
    public override string Name => "focus_window";
    public override string Description => "Bring a window to the front and restore it if minimized. Pass the handle from list_windows, or part of its title.";
    public override JsonObject Parameters => Schema(
        ("handle", Int("Window handle from list_windows"), false),
        ("title", Str("Case-insensitive part of the window title"), false));
    public override string Describe(JsonObject args) =>
        "Focus window " + (GetString(args, "title") is { } t ? $"\"{t}\"" : GetNumber(args, "handle")?.ToString());

    protected override Task<string> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var windows = ctx.Platform.Windows.ListWindows();
        WindowInfo? target = null;
        if (GetNumber(args, "handle") is { } h)
            target = windows.FirstOrDefault(w => w.Handle == (long)h);
        if (target is null && GetString(args, "title") is { Length: > 0 } title)
            target = windows.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            throw new ToolArgumentException("No matching window. Call list_windows to see what is open.");

        if (!ctx.Platform.Windows.FocusWindow(target.Handle))
            return Task.FromResult($"Windows refused to focus \"{target.Title}\"; try clicking its taskbar button.");
        // Look at the screen the window is on, so the screenshot that follows shows it.
        var screen = ListWindowsTool.ScreenOf(ctx, target);
        var moved = ctx.Screens.Count > 1 && screen != ctx.CurrentScreen;
        ctx.CurrentScreen = screen;
        return Task.FromResult($"Focused \"{target.Title}\"." + (moved ? $" It is on screen {screen + 1}; x/y now refer to that screen." : ""));
    }
}
