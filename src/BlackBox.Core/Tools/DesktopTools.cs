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
        "Capture the primary screen. Returns an image; all x/y coordinates for mouse tools are in this image's pixel space.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema();
    public override string Describe(JsonObject args) => "Take a screenshot";

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) => Task.FromResult(Take(ctx));

    internal static ToolResult Take(ToolContext ctx)
    {
        var space = ctx.GetScreenSpace();
        var frame = ctx.Platform.Screen.Capture(space.Screen, includeCursor: true);
        var jpeg = space.Encode(frame);
        var (cx, cy) = space.ToImage(ctx.Platform.Input.GetCursorPosition().X, ctx.Platform.Input.GetCursorPosition().Y);
        return new ToolResult($"Screenshot {space.ImageWidth}x{space.ImageHeight}. Mouse cursor at ({cx}, {cy}).", jpeg);
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
    public override string Name => "press_keys";
    public override string Description =>
        "Press a key or shortcut. Give the keys of one chord in order, e.g. [\"ctrl\",\"c\"], [\"alt\",\"tab\"], [\"enter\"], [\"win\",\"r\"]. " +
        "Names: ctrl, alt, shift, win, enter, esc, tab, backspace, delete, space, up, down, left, right, home, end, pageup, pagedown, f1-f12, a-z, 0-9.";
    public override JsonObject Parameters => Schema(
        ("keys", StrArray("Keys of one chord, modifiers first"), true),
        ("repeat", Int("How many times to press the chord, default 1"), false));
    public override string Describe(JsonObject args) =>
        $"Press {string.Join("+", Keys(args))}" + (GetNumber(args, "repeat") is > 1 and var r ? $" x{r}" : "");

    private static List<string> Keys(JsonObject args)
    {
        var keys = GetStringList(args, "keys") ?? [];
        // Accept "ctrl+c" as a single string as well.
        return keys.SelectMany(k => k.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
    }

    protected override async Task<string> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var keys = Keys(args);
        if (keys.Count == 0)
            throw new ToolArgumentException("'keys' must list at least one key.");
        var unknown = keys.Where(k => !ctx.Platform.Input.IsKnownKey(k)).ToList();
        if (unknown.Count > 0)
            throw new ToolArgumentException($"Unknown key name(s): {string.Join(", ", unknown)}.");

        var repeat = Math.Clamp((int)(GetNumber(args, "repeat") ?? 1), 1, 100);
        ctx.Platform.HostWindow.YieldFocus();
        for (var i = 0; i < repeat; i++)
        {
            ctx.Platform.Input.PressKeys(keys);
            if (i < repeat - 1)
                await Task.Delay(40, ct).ConfigureAwait(false);
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
        var space = ctx.GetScreenSpace();
        var sb = new StringBuilder();
        foreach (var w in ctx.Platform.Windows.ListWindows())
        {
            var (x, y) = space.ToImage(w.Bounds.X, w.Bounds.Y);
            var (r, b) = space.ToImage(w.Bounds.X + w.Bounds.Width, w.Bounds.Y + w.Bounds.Height);
            sb.AppendLine($"handle={w.Handle} process={w.ProcessName} title=\"{w.Title}\" bounds=({x},{y})-({r},{b}){(w.IsMinimized ? " minimized" : "")}");
        }
        return Task.FromResult(new ToolResult(sb.Length > 0 ? sb.ToString() : "No windows found."));
    }
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

        return Task.FromResult(ctx.Platform.Windows.FocusWindow(target.Handle)
            ? $"Focused \"{target.Title}\"."
            : $"Windows refused to focus \"{target.Title}\"; try clicking its taskbar button.");
    }
}
