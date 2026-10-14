using System.Text.Json.Nodes;
using BlackBox.Core.Platform;

namespace BlackBox.Core.Tools;

/// <summary>Shared behaviour for tools that drive the mouse and keyboard.</summary>
public abstract class DesktopActionTool : Tool
{
    public override ToolRisk Risk => ToolRisk.Desktop;

    public sealed override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (text, check) = await ActAsync(args, ctx, ct).ConfigureAwait(false);
        // The screen now changes in ways the model hasn't seen; a later click in the same reply
        // is aimed at what it expects, not at what it saw.
        ctx.Seen = null;
        if (!ctx.Settings.ScreenshotAfterActions)
            return new ToolResult(text);

        // Give the UI a moment to react before looking at it.
        await Task.Delay(500, ct).ConfigureAwait(false);
        // A fresh look that misses misses the same way again, so a retry goes to the model's own x/y.
        if (check is not null && await check.MissedAsync(ctx, ct).ConfigureAwait(false))
            text += "\nNothing on screen changed after this click, so it probably missed. If the mouse pointer in the screenshot is not on what you meant, " +
                    "click again with target left out and x, y on it. If it only needs more time to respond, wait instead; " +
                    "if you clicked an empty spot only to focus a window, ignore this.";
        var shot = ScreenshotTool.Take(ctx);
        return shot with { Text = text + "\n" + shot.Text };
    }

    /// <summary>What an action did; for a click, also the screen just before it, to tell a miss by (see <see cref="ClickCheck"/>).</summary>
    private protected readonly record struct Acted(string Text, ClickCheck? Check = null)
    {
        public static implicit operator Acted(string text) => new(text);
    }

    private protected abstract Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct);

    protected static (int X, int Y) Point(JsonObject args, ToolContext ctx, string xKey = "x", string yKey = "y") =>
        ctx.GetScreenSpace().ToScreen(RequireNumber(args, xKey), RequireNumber(args, yKey));

    /// <summary>Moves BlackBox's own window out of the way if the action lands on it.</summary>
    protected static IDisposable Aside(ToolContext ctx, params (int X, int Y)[] points) =>
        ctx.Platform.HostWindow.StepAside(points);

    protected static string Fmt(JsonObject args, string xKey = "x", string yKey = "y") =>
        $"({GetNumber(args, xKey)}, {GetNumber(args, yKey)})";

    /// <summary>The schema of the keys a click or drag can hold, as a person does to select several things.</summary>
    protected static JsonObject ModifiersParameter() =>
        StrArray("Keys to hold down during it, e.g. [\"ctrl\"] to add to a selection, [\"shift\"] to select a range");

    /// <summary>The keys in modifiers ("ctrl", "shift+alt"…); an unknown name is an argument error, found before anything moves.</summary>
    protected static List<string> Modifiers(JsonObject args, ToolContext? ctx = null)
    {
        var keys = (GetStringList(args, "modifiers") ?? []).SelectMany(k => k.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (ctx is not null && keys.Where(k => !ctx.Platform.Input.IsKnownKey(k)).ToList() is { Count: > 0 } unknown)
            throw new ToolArgumentException($"Unknown key name(s) in modifiers: {string.Join(", ", unknown)}.");
        return keys;
    }

    /// <summary>Holds the keys until disposed; with none, nothing is pressed.</summary>
    protected static IDisposable? Hold(ToolContext ctx, List<string> keys) => keys.Count > 0 ? ctx.Platform.Input.HoldKeys(keys) : null;

    /// <summary>"Ctrl+Shift" for a description.</summary>
    protected static string Held(List<string> keys) => string.Join("+", keys.Select(OverlayInputController.KeyName));
}

public sealed class ScreenshotTool : Tool
{
    public override string Name => "screenshot";
    public override string Description =>
        "Capture a screen. Returns an image; the mouse tools' x/y point into it. " +
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
        ctx.LastShot = (space.Screen, frame);
        var jpeg = space.Encode(frame);
        var count = ctx.Screens.Count;
        var which = count > 1 ? $"Screenshot of screen {ctx.CurrentScreen + 1} of {count}" : "Screenshot";
        // The image size only matters when x/y are its pixels.
        if (space.Grid is null)
            which += (count > 1 ? ", " : " ") + $"{space.ImageWidth}x{space.ImageHeight}";
        which += ".";
        var cursor = ctx.Platform.Input.GetCursorPosition();
        var cursorScreen = ctx.ScreenAt(cursor.X, cursor.Y);
        var (cx, cy) = space.ToModel(cursor.X, cursor.Y);
        var where = cursorScreen == ctx.CurrentScreen ? $"Mouse cursor at ({cx}, {cy})." : $"Mouse cursor is on screen {cursorScreen + 1}.";
        return new ToolResult(which + " " + where, jpeg);
    }
}

public sealed class MouseClickTool : DesktopActionTool
{
    public override string Name => "mouse_click";
    public override string Description =>
        "Click something on the screen. Name it in target; BlackBox finds it on the current screen and clicks its centre. " +
        "Also give x, y where you see it, in screenshot coordinates: they are used when it can't be found by name.";
    public override JsonObject Parameters => Schema(
        ("target", Str("What to click, named so someone who hasn't seen your earlier steps can find it: its text or icon, " +
                       "and where it is if there are several, e.g. \"File menu\", \"Save button in the Export dialog\". " +
                       "Leave it out only to click an exact spot that has no name (an empty area, a point in a drawing)."), true),
        ("x", Num("X in screenshot coordinates"), true),
        ("y", Num("Y in screenshot coordinates"), true),
        ("button", Str("Mouse button, default left", "left", "right", "middle"), false),
        ("clicks", Int("1 = single click (default), 2 = double click, 3 = triple click"), false),
        ("modifiers", ModifiersParameter(), false));

    public override string Describe(JsonObject args)
    {
        var clicks = (int)(GetNumber(args, "clicks") ?? 1);
        var kind = clicks switch { 2 => "Double-click", 3 => "Triple-click", _ => "Click" };
        if (Modifiers(args) is { Count: > 0 } held)
            kind = Held(held) + "+" + kind.ToLowerInvariant();
        var button = GetString(args, "button") is { } b && b != "left" ? $" ({b})" : "";
        return GetString(args, "target") is { Length: > 0 } t ? $"{kind}{button} {t}" : $"{kind}{button} at {Fmt(args)}";
    }

    /// <summary>How far, as a share of the screen's longer side, the look may find the target from where the model aimed before the click can be held back.</summary>
    private const double FarShare = 0.08;

    /// <summary>The half-width of the square around the model's aim compared with what it saw, as a share of the screen's longer side.</summary>
    private const double AroundShare = 0.05;

    private protected override async Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var modifiers = Modifiers(args, ctx);
        var (aimed, note) = await AimAsync(args, ctx, ct).ConfigureAwait(false);
        if (aimed is not { } point)
            return note; // held back: nothing was clicked
        var button = ParseButton(GetString(args, "button"));
        var clicks = Math.Clamp((int)(GetNumber(args, "clicks") ?? 1), 1, 3);
        // The screen just before the click, not before the look: what changed while the model
        // looked (some 15 s) is no reaction to the click.
        var check = ClickCheck.Enabled && ctx.Settings.ScreenshotAfterActions ? ClickCheck.Start(ctx) : null;
        using var aside = Aside(ctx, point);
        ctx.Platform.Input.MoveMouse(point.X, point.Y);
        using (Hold(ctx, modifiers))
        {
            await Task.Delay(60, ct).ConfigureAwait(false);
            ctx.Platform.Input.Click(button, clicks);
        }
        return new(note + clicks switch { 2 => "Double-clicked.", 3 => "Triple-clicked.", _ => "Clicked." }, check);
    }

    /// <summary>
    /// Where a fresh look at the screen finds the target (see <see cref="Agent.FreshLook"/>), or
    /// the call's own x/y when there is no target or the look doesn't find it. Deep in a long run
    /// the model's own coordinates drift (a wrong belief about where something is, or the previous
    /// call's point copied), while the same model looking at the screen afresh gets them right.
    /// No point when the click is held back because what the model aimed at is gone (see <see cref="Gone"/>).
    /// </summary>
    private static async Task<((int X, int Y)? Point, string Note)> AimAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var space = ctx.GetScreenSpace();
        (int X, int Y)? given = GetNumber(args, "x") is { } x && GetNumber(args, "y") is { } y ? space.ToScreen(x, y) : null;
        if (GetString(args, "target")?.Trim() is not { Length: > 0 } target || ctx.Locate is not { } locate)
            return (given ?? throw new ToolArgumentException("Give target, or x and y."), "");
        var now = ctx.Platform.Screen.Capture(space.Screen, includeCursor: false);
        if (await locate(space.Encode(now), target, ct).ConfigureAwait(false) is { } p && p.X >= 0 && p.Y >= 0 && p.X <= space.ModelWidth && p.Y <= space.ModelHeight)
        {
            var found = space.ToScreen(p.X, p.Y);
            if (given is { } aim && Gone(ctx, space.Screen, now, aim, found))
            {
                ctx.HeldBack = aim;
                return (null, $"Nothing was clicked: the {target} was found far from where you aimed, and the screen there has changed since your " +
                              "last screenshot, so what you meant may have closed or moved. Look at the screenshot below and click again if it is still what you want.");
            }
            ctx.HeldBack = null;
            return (found, "");
        }
        ctx.HeldBack = null;
        return given is { } g
            ? (g, $"BlackBox could not find the {target} on the screen by name, so it clicked at your x, y. ")
            : throw new InvalidOperationException($"BlackBox could not find the {target} on the screen. Name it differently, or give x and y.");
    }

    /// <summary>
    /// Whether what the model aimed at has gone since it looked: the fresh look finds the target
    /// far from where the model aimed, and there the screen is no longer what the model saw. The
    /// common case is a notification that closed by itself in the seconds before the click: the
    /// look then finds something else that fits the words, such as the close button of the window
    /// under it, and clicking that would do harm. A click the model asks for again after seeing the
    /// new screen goes ahead.
    /// </summary>
    private static bool Gone(ToolContext ctx, ScreenRect screen, RawImage now, (int X, int Y) aim, (int X, int Y) found)
    {
        if (ctx.Seen is not { } seen || seen.Screen != screen)
            return false;
        var side = Math.Max(screen.Width, screen.Height);
        double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        if (Distance(aim, found) < side * FarShare || (ctx.HeldBack is { } held && Distance(held, aim) < side * FarShare / 2))
            return false;
        return ClickCheck.ChangedAround(seen.Frame, now, aim.X - screen.X, aim.Y - screen.Y, (int)(side * AroundShare));
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
    public override JsonObject Parameters => Schema(("x", Num("X in screenshot coordinates"), true), ("y", Num("Y in screenshot coordinates"), true));
    public override string Describe(JsonObject args) => $"Move mouse to {Fmt(args)}";

    private protected override Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (x, y) = Point(args, ctx);
        using var aside = Aside(ctx, (x, y));
        ctx.Platform.Input.MoveMouse(x, y);
        return Task.FromResult<Acted>("Moved the mouse.");
    }
}

public sealed class MouseDragTool : DesktopActionTool
{
    public override string Name => "mouse_drag";
    public override string Description => "Press the left button at (x, y), drag to (to_x, to_y) and release. Screenshot coordinates.";
    public override JsonObject Parameters => Schema(
        ("x", Num("Start X"), true), ("y", Num("Start Y"), true),
        ("to_x", Num("End X"), true), ("to_y", Num("End Y"), true),
        ("modifiers", ModifiersParameter(), false));
    public override string Describe(JsonObject args) =>
        $"Drag from {Fmt(args)} to {Fmt(args, "to_x", "to_y")}" + (Modifiers(args) is { Count: > 0 } held ? $" holding {Held(held)}" : "");

    private protected override async Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var modifiers = Modifiers(args, ctx);
        var (x1, y1) = Point(args, ctx);
        var (x2, y2) = Point(args, ctx, "to_x", "to_y");
        var input = ctx.Platform.Input;
        using var aside = Aside(ctx, (x1, y1), (x2, y2));
        input.MoveMouse(x1, y1);
        using var held = Hold(ctx, modifiers);
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
        return "Dragged.";
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

    private protected override Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
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
        return Task.FromResult<Acted>(Describe(args) + " done.");
    }
}

public sealed class TypeTextTool : DesktopActionTool
{
    public override string Name => "type_text";
    public override string Description =>
        "Type text into the focused control, exactly as given. Use \\n for Enter. Click the target field first.";
    public override JsonObject Parameters => Schema(("text", Str("Text to type"), true));
    public override string Describe(JsonObject args) => $"Type \"{(GetString(args, "text") ?? "").Clip(60)}\"";

    private protected override Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var text = RequireString(args, "text");
        ctx.Platform.HostWindow.YieldFocus();
        ctx.Platform.Input.TypeText(text);
        return Task.FromResult<Acted>($"Typed {text.Length} characters.");
    }
}

public sealed class PressKeysTool : DesktopActionTool
{
    /// <summary>Pause between the presses of a sequence, so apps (and game animations) keep up.</summary>
    private const int DefaultGapMs = 150;

    /// <summary>Longest a press may be held.</summary>
    private const int MaxHoldSeconds = 60;

    public override string Name => "press_keys";
    public override string Description =>
        "Press a key or shortcut, or a whole series of them in one go. For one chord give its keys in order, e.g. [\"ctrl\",\"c\"], [\"alt\",\"tab\"], [\"enter\"]. " +
        "For several presses one after another, give sequence instead, e.g. [\"left\",\"down\",\"left\",\"tab\",\"ctrl+s\"]: much faster than one call per key. " +
        "To keep keys down for a while (walking in a game, a long press), give hold_seconds. " +
        "Names: ctrl, alt, shift, win, enter, esc, tab, backspace, delete, space, up, down, left, right, home, end, pageup, pagedown, f1-f12, a-z, 0-9.";
    public override JsonObject Parameters => Schema(
        ("keys", StrArray("Keys of one chord, modifiers first"), false),
        ("sequence", StrArray("Presses to make one after another; each is a key or a chord like ctrl+s. Use instead of keys."), false),
        ("repeat", Int("How many times to press the chord or run the sequence, default 1"), false),
        ("gap_ms", Int($"Pause between presses in milliseconds, default {DefaultGapMs}"), false),
        ("hold_seconds", Num($"Keep each press held down this long before letting go, e.g. [\"w\"] for 2 seconds to walk forward (max {MaxHoldSeconds}); default: a quick press"), false));

    public override string Describe(JsonObject args)
    {
        var repeat = GetNumber(args, "repeat") is > 1 and var r ? $" x{r}" : "";
        var hold = GetNumber(args, "hold_seconds") is > 0 and var h ? $", held {h}s" : "";
        var sequence = Sequence(args);
        return sequence.Count > 0
            ? $"Press {sequence.Count} keys: {string.Join(" ", sequence.Select(c => string.Join("+", c)))}{repeat}{hold}"
            : $"Press {string.Join("+", Keys(args))}{repeat}{hold}";
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

    private protected override async Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
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
        var hold = TimeSpan.FromSeconds(Math.Clamp(GetNumber(args, "hold_seconds") ?? 0, 0, MaxHoldSeconds));
        ctx.Platform.HostWindow.YieldFocus();
        var total = repeat * presses.Count;
        for (var i = 0; i < total; i++)
        {
            var press = presses[i % presses.Count];
            if (hold > TimeSpan.Zero)
            {
                // Released on Stop too, so no key is left held down.
                using (ctx.Platform.Input.HoldKeys(press))
                    await Task.Delay(hold, ct).ConfigureAwait(false);
            }
            else
                ctx.Platform.Input.PressKeys(press);
            if (i < total - 1)
                await Task.Delay(gap, ct).ConfigureAwait(false);
        }
        return Describe(args) + " done.";
    }
}

public sealed class WaitTool : Tool
{
    private const int MaxSeconds = 600;

    /// <summary>How often the screen is looked at while waiting for a change.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    public override string Name => "wait";
    public override string Description =>
        $"Wait, then take a screenshot. With until_change it stops as soon as the screen changes (a reply arrives in a chat, a window or page finishes loading), " +
        $"waiting at most seconds; without it, it waits the whole time. Max {MaxSeconds} seconds.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("seconds", Num($"Seconds to wait, at most {MaxSeconds}"), true),
        ("until_change", Bool("Stop waiting as soon as something on the screen changes (default false)"), false));
    public override string Describe(JsonObject args) =>
        GetBool(args, "until_change") ? $"Wait up to {GetNumber(args, "seconds")}s for the screen to change" : $"Wait {GetNumber(args, "seconds")}s";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var seconds = TimeSpan.FromSeconds(Math.Clamp(GetNumber(args, "seconds") ?? 1, 0.1, MaxSeconds));
        string waited;
        if (GetBool(args, "until_change"))
            waited = await WaitForChangeAsync(ctx, seconds, ct).ConfigureAwait(false) switch
            {
                { } after when after == TimeSpan.Zero => "The screen had already changed since your last screenshot, so there was no wait.",
                { } after => $"The screen changed after {after.TotalSeconds:0.#}s.",
                null => $"Waited {seconds.TotalSeconds:0.#}s; nothing on the screen changed.",
            };
        else
        {
            await Task.Delay(seconds, ct).ConfigureAwait(false);
            waited = $"Waited {seconds.TotalSeconds:0.#}s.";
        }
        var shot = ScreenshotTool.Take(ctx);
        return shot with { Text = $"{waited}\n{shot.Text}" };
    }

    /// <summary>
    /// How long it took the screen to change, or null if it didn't within <paramref name="limit"/>;
    /// zero when it had already changed since the screenshot the model decided on. A chat message
    /// that arrived while the model was thinking about waiting is a change too: compared only with
    /// the screen at the start, it went unseen for the whole wait (three minutes in a chat run).
    /// </summary>
    private static async Task<TimeSpan?> WaitForChangeAsync(ToolContext ctx, TimeSpan limit, CancellationToken ct)
    {
        var screen = ctx.GetScreenSpace().Screen;
        var first = ctx.Platform.Screen.Capture(screen, includeCursor: false);
        if (ctx.Seen is { } seen && seen.Screen == screen && ClickCheck.Differ(seen.Frame, first))
            return TimeSpan.Zero;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (limit - clock.Elapsed is var left && left > TimeSpan.Zero)
        {
            await Task.Delay(left < Poll ? left : Poll, ct).ConfigureAwait(false);
            if (ClickCheck.Differ(first, ctx.Platform.Screen.Capture(screen, includeCursor: false)))
                return clock.Elapsed;
        }
        return null;
    }
}
