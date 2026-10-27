using System.Text.Json.Nodes;
using Sloperator.Core.Platform;

namespace Sloperator.Core.Tools;

/// <summary>Shared behaviour for tools that drive the mouse and keyboard.</summary>
public abstract class DesktopActionTool : Tool
{
    public override ToolRisk Risk => ToolRisk.Desktop;

    /// <summary>The longest an action waits for the user to finish typing a message to the agent.</summary>
    internal static TimeSpan MaxUserPause = TimeSpan.FromSeconds(60);

    /// <summary>The longest an action waits for another Sloperator window's agent to be done with the mouse and keyboard.</summary>
    internal static TimeSpan MaxTurnWait = TimeSpan.FromMinutes(5);

    public sealed override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.DesktopTurn is not { } desktop)
            return await ActAndLookAsync(args, ctx, screenChanged: false, ct).ConfigureAwait(false);
        if (ctx.ScreenStale)
            return new ToolResult("Nothing was done: an action before this one in your reply was held back, as another Sloperator window's agent " +
                                  "had used the mouse and keyboard. Look at the screen as it is now and do it again if it still applies.");
        // Another window's agent may be using the mouse and keyboard: one at a time.
        var turn = await desktop.TakeAsync(ctx, MaxTurnWait,
            waited => ctx.Progress?.Invoke($"Waiting for another Sloperator window to finish with the mouse and keyboard · {Chats.ChatTimes.Stopwatch(waited)}"), ct).ConfigureAwait(false);
        if (turn is null)
            return new ToolResult($"Nothing was done: another Sloperator window's agent has been using the mouse and keyboard for {MaxTurnWait.TotalMinutes:0} minutes. " +
                                  "Do what doesn't need them meanwhile (files, commands), or try again.");
        ctx.AfterRun("desktop turn", () =>
        {
            desktop.Release(ctx);
            return Task.CompletedTask;
        });
        // Not handed to another window while the action goes on, however long it waits for the user's typing.
        var acted = turn == false;
        try { return await ActAndLookAsync(args, ctx, screenChanged: turn == true, ct).ConfigureAwait(false); }
        // Arguments it couldn't use: nothing was pressed, for the other windows' agents to look again for.
        catch (ToolArgumentException)
        {
            acted = false;
            throw;
        }
        finally { desktop.Done(ctx, acted); }
    }

    private async Task<ToolResult> ActAndLookAsync(JsonObject args, ToolContext ctx, bool screenChanged, CancellationToken ct)
    {
        if (screenChanged)
        {
            // What the model aimed at may have moved, been covered or closed meanwhile; and the
            // rest of its reply was decided on the same old screen.
            ctx.Seen = null;
            ctx.ScreenStale = true;
            var now = ScreenshotTool.Take(ctx);
            return now with
            {
                Text = "Nothing was done: another Sloperator window's agent has used the mouse and keyboard, so the screen may have changed since you last saw it. " +
                       "This is the screen now; do it again if it still applies.\n" + now.Text,
            };
        }

        // The user is typing to the agent in its window: a click or key press now would take the
        // focus from under their typing, or their keys would land in the agent's window.
        var paused = System.Diagnostics.Stopwatch.StartNew();
        while (ctx.UserComposing?.Invoke() == true && paused.Elapsed < MaxUserPause)
            await Task.Delay(200, ct).ConfigureAwait(false);
        var (text, check, moved) = await ActAsync(args, ctx, ct).ConfigureAwait(false);
        // The screen now changes in ways the model hasn't seen; a later click in the same reply
        // is aimed at what it expects, not at what it saw.
        ctx.Seen = null;
        if (!ctx.Settings.ScreenshotAfterActions)
            return new ToolResult(moved is null ? text : text + "\n" + moved);

        // Give the UI a moment to react before looking at it.
        await Task.Delay(500, ct).ConfigureAwait(false);
        // A fresh look that misses misses the same way again, so a retry goes to the model's own x/y.
        // A click on what is already selected changes nothing either: Paint's oval tool, picked by
        // one of the first clicks, was clicked some twenty times more, each "probably missed".
        if (check is not null && await check.MissedAsync(ctx, ct).ConfigureAwait(false))
            text += "\nNothing on screen changed after this click, so it probably missed, unless what you clicked was already selected or switched on " +
                    "(a tool, a tab, a checkbox), which a click doesn't change: then go on. If the mouse pointer in the screenshot is not on what you meant, " +
                    "click again with target left out and x, y on it. If it only needs more time to respond, wait instead; " +
                    "if you clicked an empty spot only to focus a window, ignore this.";
        else if (moved is not null)
            text += "\n" + moved;
        var shot = ScreenshotTool.Take(ctx);
        return shot with { Text = text + "\n" + shot.Text };
    }

    /// <summary>
    /// What an action did; for a click, also the screen just before it, to tell a miss by (see <see cref="ClickCheck"/>),
    /// and what to tell the model when a fresh look moved the click away from its aim, unless the click missed (the miss note says it then).
    /// </summary>
    private protected readonly record struct Acted(string Text, ClickCheck? Check = null, string? Moved = null)
    {
        public static implicit operator Acted(string text) => new(text);
    }

    private protected abstract Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct);

    /// <summary>
    /// The wait <see cref="ExecuteAsync"/> makes before an action, made again inside one that takes
    /// a while (a click whose target is still being looked for, a series of presses, long text):
    /// typing to the agent that starts meanwhile must hold the rest too. Returns whether it waited:
    /// the keyboard is then in Sloperator's window, and keys meant for another need it given back.
    /// </summary>
    private protected static async Task<bool> WaitWhileUserTypesAsync(ToolContext ctx, CancellationToken ct)
    {
        if (ctx.UserComposing?.Invoke() != true)
            return false;
        var paused = System.Diagnostics.Stopwatch.StartNew();
        while (ctx.UserComposing() && paused.Elapsed < MaxUserPause)
            await Task.Delay(200, ct).ConfigureAwait(false);
        return true;
    }

    protected static (int X, int Y) Point(JsonObject args, ToolContext ctx, string xKey = "x", string yKey = "y") =>
        ctx.GetScreenSpace().ToScreen(RequireNumber(args, xKey), RequireNumber(args, yKey));

    /// <summary>Moves Sloperator's own window out of the way if the action lands on it.</summary>
    protected static IDisposable Aside(ToolContext ctx, params (int X, int Y)[] points) =>
        ctx.Platform.HostWindow.StepAside(points);

    // Invariant: with a German PC's culture an x of 512.5 read "(512,5, 300)", to the model as well.
    protected static string Fmt(JsonObject args, string xKey = "x", string yKey = "y") =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({GetNumber(args, xKey)}, {GetNumber(args, yKey)})");

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
        ctx.DesktopTurn?.Looked(ctx);
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
        "Click something on the screen. Name it in target, and say in which what tells it apart from anything else on the screen that looks like it; " +
        "Sloperator finds it on the current screen and clicks its centre. " +
        "Also give x, y where you see it, in screenshot coordinates: they are used when it can't be found by name.";
    public override JsonObject Parameters => Schema(
        ("target", Str("What to click, named so someone who hasn't seen your earlier steps can find it: its text or icon and what kind of thing it is, " +
                       "e.g. \"File menu\", \"Delete button\", \"Bold icon\". " +
                       "Leave it out only to click an exact spot that has no name (an empty area, a point in a drawing)."), true),
        ("which", Str("What tells it apart from everything else on the screen that looks like it or has the same text: the row, item, card, dialog or panel " +
                      "it belongs to and the label or text next to it, e.g. \"in the row of report-q1 (1).xlsx\", \"on the card of Wireless Mouse M330\", " +
                      $"\"in the Export dialog\". Write \"{OnlyOne}\" when nothing else on the screen looks like it."), true),
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
        return Named(args) is { } t ? $"{kind}{button} {t}" : $"{kind}{button} at {Fmt(args)}";
    }

    /// <summary>The height of the row a target with look-alikes is looked for in, as a share of the screen's; 1 or more: the whole screen. Changed only to measure (ClickAccuracyTests).</summary>
    internal static double RowShare { get; set; } = 0.25;

    /// <summary>What which says when nothing else on the screen looks like the target.</summary>
    internal const string OnlyOne = "only one";

    /// <summary>
    /// The target with what tells it apart from its look-alikes ("Delete button, in the row of
    /// report.xlsx"), as the look is asked for it; just the target when it has none; null without a target.
    /// </summary>
    internal static string? Named(JsonObject args)
    {
        if (GetString(args, "target")?.Trim() is not { Length: > 0 } target)
            return null;
        return LookAlikes(args) && GetString(args, "which")?.Trim().TrimEnd('.') is { Length: > 0 } which ? $"{target}, {which}" : target;
    }

    /// <summary>Whether which says something else on the screen looks like the target: anything but "only one".</summary>
    internal static bool LookAlikes(JsonObject args) =>
        GetString(args, "which")?.Trim().TrimEnd('.') is { Length: > 0 } which && !which.Equals(OnlyOne, StringComparison.OrdinalIgnoreCase);

    /// <summary>How far, as a share of the screen's longer side, the look may find the target from where the model aimed before the click can be held back.</summary>
    private const double FarShare = 0.08;

    /// <summary>The half-width of the square around the model's aim compared with what it saw, as a share of the screen's longer side.</summary>
    private const double AroundShare = 0.05;

    /// <summary>
    /// How far, as a share of the screen's longer side, the look may move the click from the model's aim before the result says so: about a row of a menu or a tab.
    /// Half that for a target with look-alikes, whose rows can be closer together.
    /// </summary>
    private const double MovedShare = 0.02;

    private protected override async Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var modifiers = Modifiers(args, ctx);
        var (aimed, note, moved) = await AimAsync(args, ctx, ct).ConfigureAwait(false);
        if (aimed is not { } point)
            return note; // held back: nothing was clicked
        // The look is a request to the model, seconds long: typing to the agent that started
        // meanwhile holds the click too. It went ahead mid-word, and the rest of the message
        // landed in the window it clicked.
        await WaitWhileUserTypesAsync(ctx, ct).ConfigureAwait(false);
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
        return new(note + clicks switch { 2 => "Double-clicked.", 3 => "Triple-clicked.", _ => "Clicked." }, check, moved);
    }

    /// <summary>
    /// Where a fresh look at the screen finds the target (see <see cref="Agent.FreshLook"/>), or
    /// the call's own x/y when there is no target or the look doesn't find it. Deep in a long run
    /// the model's own coordinates drift (a wrong belief about where something is, or the previous
    /// call's point copied), while the same model looking at the screen afresh gets them right.
    /// No point when the click is held back because what the model aimed at is gone (see <see cref="Gone"/>).
    /// </summary>
    /// <returns>
    /// Also, when the look moved the click away from the model's aim, a note that says so. The look
    /// can be the one that is wrong: in Blender it took the Animation tab for the "MCP for Blender"
    /// tab under it, and the model, seeing the wrong panel open, named the tab again twice, aiming
    /// lower each time, and both times the look moved the click back up. Told where the click went,
    /// the model can aim itself.
    /// </returns>
    /// <remarks>
    /// The look is shown the quarter of the screen around the model's aim, and the whole screen only
    /// when the target isn't there: reading a whole 2560x1080 screenshot took a 27B model 9 s, a
    /// quarter of it 3 s, and the model's own aim is mostly off by tens of pixels, not hundreds.
    /// A target with look-alikes (which says what tells it apart) is looked for in the screen-wide
    /// row around the aim instead, as many pixels: in a file list the quarter around an Open button
    /// held only other rows' Open buttons, not the file names, and the look moved a click the model
    /// had aimed at the right row to another one.
    /// </remarks>
    private static async Task<((int X, int Y)? Point, string Note, string? Moved)> AimAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var space = ctx.GetScreenSpace();
        (int X, int Y)? given = GetNumber(args, "x") is { } x && GetNumber(args, "y") is { } y ? space.ToScreen(x, y) : null;
        if (Named(args) is { } named && string.IsNullOrWhiteSpace(GetString(args, "which")))
            throw new ToolArgumentException($"Say in which what tells the {named} apart from anything else on the screen that looks like it " +
                                            $"(the row, card, dialog or label it belongs to), or \"{OnlyOne}\" if nothing does.");
        if (Named(args) is not { } target || ctx.Locate is not { } locate)
            return (given ?? throw new ToolArgumentException("Give target, or x and y."), "", null);
        var lookAlikes = LookAlikes(args);
        var now = ctx.Platform.Screen.Capture(space.Screen, includeCursor: false);
        async Task<(int X, int Y)?> LookAsync(ScreenSpace part, RawImage frame) =>
            await locate(part.Encode(frame), part, target, ct).ConfigureAwait(false) is { } p && p.X >= 0 && p.Y >= 0 && p.X <= part.ModelWidth && p.Y <= part.ModelHeight
                ? part.ToScreen(p.X, p.Y)
                : null;
        (int X, int Y)? near = null;
        if (given is { } aimed && Agent.FreshLook.Cropped && !(lookAlikes && RowShare >= 1))
        {
            var part = space.Part(lookAlikes && RowShare > 0 ? space.Row(aimed.Y, RowShare) : space.Around(aimed.X, aimed.Y));
            near = await LookAsync(part, now.Crop(part.Screen.X - space.Screen.X, part.Screen.Y - space.Screen.Y, part.Screen.Width, part.Screen.Height)).ConfigureAwait(false);
        }
        if ((near ?? await LookAsync(space, now).ConfigureAwait(false)) is { } found)
        {
            if (given is { } aim && Gone(ctx, space.Screen, now, aim, found))
            {
                ctx.HeldBack = aim;
                return (null, $"Nothing was clicked: the {target} was found far from where you aimed, and the screen there has changed since your " +
                              "last screenshot, so what you meant may have closed or moved. Look at the screenshot below and click again if it is still what you want.", null);
            }
            ctx.HeldBack = null;
            string? moved = null;
            if (given is { } from && Distance(from, found) >= Math.Max(space.Screen.Width, space.Screen.Height) * (lookAlikes ? MovedShare / 2 : MovedShare))
            {
                var (mx, my) = space.ToModel(found.X, found.Y);
                moved = $"A fresh look found the {target} at ({mx}, {my}), not where you aimed {Fmt(args)}, and the click went there. " +
                        "If the mouse pointer in the screenshot is not on what you meant, click again with target left out and x, y on it: a look would find the same thing again.";
            }
            return (found, "", moved);
        }
        ctx.HeldBack = null;
        return given is { } g
            ? (g, $"Sloperator could not find the {target} on the screen by name, so it clicked at your x, y. ", null)
            : throw new InvalidOperationException($"Sloperator could not find the {target} on the screen. Name it differently, or give x and y.");
    }

    private static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

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
        // In screen pixels, as apps measure what was drawn or selected: on the 0-1000 grid equal
        // x and y steps are not equal distances.
        return $"Dragged {Math.Abs(x2 - x1)} x {Math.Abs(y2 - y1)} px on screen.";
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

public sealed partial class TypeTextTool : DesktopActionTool
{
    public override string Name => "type_text";
    // Not "use \n for Enter": read as the two characters, Gemma 4 typed "gemma.txt\n" into a
    // Save As dialog's file name, backslash and all.
    public override string Description =>
        "Type text into the focused control, exactly as given; a line break in the text presses Enter. Click the target field first. " +
        "Into a Save As or Open dialog's file name box, type the whole path (C:\\folder\\name.txt), whatever folder the dialog shows.";
    public override JsonObject Parameters => Schema(("text", Str("Text to type"), true));
    public override string Describe(JsonObject args) => $"Type \"{(GetString(args, "text") ?? "").Clip(60)}\"";

    /// <summary>How much is typed at a time (about 0.2 s) before checking that the user hasn't started typing to the agent.</summary>
    internal const int Chunk = 200;

    private protected override async Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var text = EnterForEscapedBreaks(RequireString(args, "text"));
        ctx.Platform.HostWindow.YieldFocus();
        // Long text takes seconds: typing to the agent that starts meanwhile holds the rest, which
        // then goes back to the window it was meant for, not into the user's message.
        for (var at = 0; at < text.Length; at += Chunk)
        {
            // Stop ends long text between pieces: it went on typing the rest, Enter for every
            // line break, into whatever window had the focus.
            ct.ThrowIfCancellationRequested();
            if (at > 0 && await WaitWhileUserTypesAsync(ctx, ct).ConfigureAwait(false))
                ctx.Platform.HostWindow.YieldFocus();
            ctx.Platform.Input.TypeText(text.Substring(at, Math.Min(Chunk, text.Length - at)));
        }
        return $"Typed {text.Length} characters.";
    }

    /// <summary>
    /// A "\n" written out as a backslash and an n at the very end of the text means Enter
    /// ("Hello from Gemma\n"), when nothing else in it is a backslash or a line break: code
    /// (printf("hi\n");) and paths are typed as written.
    /// </summary>
    internal static string EnterForEscapedBreaks(string text) =>
        EscapedBreaksAtEnd().Match(text) is { Success: true } m && !text.AsSpan(0, m.Index).ContainsAny('\\', '\n')
            ? text[..m.Index] + new string('\n', m.Length / 2)
            : text;

    [System.Text.RegularExpressions.GeneratedRegex(@"(\\n)+$")]
    private static partial System.Text.RegularExpressions.Regex EscapedBreaksAtEnd();
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
            // A series takes a while: typing to the agent that starts meanwhile holds the rest, which
            // then goes back to the window it was meant for. It went into the user's message.
            if (i > 0 && await WaitWhileUserTypesAsync(ctx, ct).ConfigureAwait(false))
                ctx.Platform.HostWindow.YieldFocus();
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
        // A message from the user ends the wait: a ten-minute watch shouldn't keep them waiting.
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, ctx.UserMessage);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string waited;
        try
        {
            if (GetBool(args, "until_change"))
                waited = await WaitForChangeAsync(ctx, seconds, wait.Token).ConfigureAwait(false) switch
                {
                    { } after when after == TimeSpan.Zero => "The screen had already changed since your last screenshot, so there was no wait.",
                    { } after => $"The screen changed after {after.TotalSeconds:0.#}s.",
                    null => $"Waited {seconds.TotalSeconds:0.#}s; nothing on the screen changed.",
                };
            else
            {
                await Task.Delay(seconds, wait.Token).ConfigureAwait(false);
                waited = $"Waited {seconds.TotalSeconds:0.#}s.";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && ctx.UserMessage.IsCancellationRequested)
        {
            waited = $"The wait ended after {clock.Elapsed.TotalSeconds:0.#}s: the user sent you a message, which follows.";
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
