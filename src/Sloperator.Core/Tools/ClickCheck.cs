using Sloperator.Core.Platform;

namespace Sloperator.Core.Tools;

/// <summary>
/// Notices a click that changed nothing on screen, which almost always means it missed its target
/// (a menu a few pixels lower, a tab next to the spot). Without this the result reads like a
/// success, and the model clicks the same spot again and again. The screen is compared before and
/// after the click (without the mouse cursor); a change near the click, or a big one anywhere
/// (a dialog opening), counts as a reaction. Small changes elsewhere (a clock, a blinking caret,
/// the hover highlight under the old mouse position) don't.
/// </summary>
internal sealed class ClickCheck
{
    /// <summary>Radius around the click, in screen pixels, where any change counts as a reaction.</summary>
    private const int NearRadius = 60;

    /// <summary>Changed pixels near the click that count as a reaction (a checkbox tick, a caret).</summary>
    private const int NearPixels = 6;

    /// <summary>Changed share of the screen elsewhere that counts as a reaction (about 55x50 px on 2560x1080).</summary>
    private const double FarShare = 0.001;

    /// <summary>A pixel counts as changed when a colour channel moves by more than this.</summary>
    private const int Threshold = 24;

    /// <summary>When nothing has changed yet, how long to keep looking for a slow reaction.</summary>
    private static readonly TimeSpan[] Rechecks = [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(700)];

    /// <summary>How a click's result starts telling the model it probably missed; the agent looks for it too.</summary>
    internal const string MissedNote = "Nothing on screen changed after this click";

    /// <summary>Off only to measure what the check adds (ClickAccuracyTests).</summary>
    internal static bool Enabled { get; set; } = true;

    private readonly ScreenRect _screen;
    private readonly RawImage _before;
    private readonly (int X, int Y) _oldCursor;

    private ClickCheck(ScreenRect screen, RawImage before, (int X, int Y) oldCursor)
    {
        _screen = screen;
        _before = before;
        _oldCursor = oldCursor;
    }

    /// <summary>Remembers the screen the click is about to happen on.</summary>
    public static ClickCheck Start(ToolContext ctx)
    {
        var screen = ctx.GetScreenSpace().Screen;
        return new(screen, ctx.Platform.Screen.Capture(screen, includeCursor: false), ctx.Platform.Input.GetCursorPosition());
    }

    /// <summary>
    /// After the click (and the usual pause for the UI to react): whether nothing changed,
    /// rechecking a couple of times for slow reactions.
    /// </summary>
    public async Task<bool> MissedAsync(ToolContext ctx, CancellationToken ct)
    {
        var click = ctx.Platform.Input.GetCursorPosition();
        if (!_screen.Contains(click.X, click.Y))
            return false;
        if (Reacted(ctx.Platform.Screen.Capture(_screen, includeCursor: false), click))
            return false;
        foreach (var delay in Rechecks)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            if (Reacted(ctx.Platform.Screen.Capture(_screen, includeCursor: false), click))
                return false;
        }
        return true;
    }

    private bool Reacted(RawImage after, (int X, int Y) click)
    {
        if (after.Width != _before.Width || after.Height != _before.Height)
            return true;
        var (cx, cy) = (click.X - _screen.X, click.Y - _screen.Y);
        var (ox, oy) = (_oldCursor.X - _screen.X, _oldCursor.Y - _screen.Y);
        const int r2 = NearRadius * NearRadius;
        var hover2 = 2 * r2; // the old hover highlight can be a bit bigger than the click's surroundings
        var farLimit = (long)(after.Width * (long)after.Height * FarShare);
        long near = 0, far = 0;
        var a = _before.Bgra;
        var b = after.Bgra;
        for (var y = 0; y < after.Height; y++)
        {
            var row = y * after.Stride;
            var dyc = (y - cy) * (y - cy);
            var dyo = (y - oy) * (y - oy);
            for (var x = 0; x < after.Width; x++)
            {
                var i = row + x * 4;
                if (!Changed(a, b, i))
                    continue;
                if ((x - cx) * (x - cx) + dyc <= r2)
                {
                    if (++near >= NearPixels) return true;
                }
                else if ((x - ox) * (x - ox) + dyo > hover2 && ++far >= farLimit)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether two pictures of a screen differ by more than a sliver: what counts as a big change
    /// for a click (a reply in a chat, a window opening), while a clock or a blinking caret doesn't.
    /// </summary>
    internal static bool Differ(RawImage before, RawImage after)
    {
        if (after.Width != before.Width || after.Height != before.Height)
            return true;
        var limit = (long)(after.Width * (long)after.Height * FarShare);
        long changed = 0;
        for (var i = 0; i < after.Bgra.Length; i += 4)
            if (Changed(before.Bgra, after.Bgra, i) && ++changed >= limit)
                return true;
        return false;
    }

    private static bool Changed(byte[] a, byte[] b, int i) =>
        Math.Abs(a[i] - b[i]) > Threshold || Math.Abs(a[i + 1] - b[i + 1]) > Threshold || Math.Abs(a[i + 2] - b[i + 2]) > Threshold;
}
