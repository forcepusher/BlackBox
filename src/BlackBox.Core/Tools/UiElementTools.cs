using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Platform;

namespace BlackBox.Core.Tools;

public sealed class ListUiElementsTool : Tool
{
    public override string Name => "list_ui_elements";
    public override string Description =>
        "List the buttons, fields, menus and other controls of the active window (or a window by title), with ids and screenshot coordinates. " +
        "Then use click_element with an id. This is more reliable than clicking pixels for small or crowded controls.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("window_title", Str("Optional part of a window title; default is the active window"), false),
        ("filter", Str("Optional text to match in control names, e.g. \"save\""), false));
    public override string Describe(JsonObject args) =>
        "List controls" + (GetString(args, "window_title") is { } w ? $" in \"{w}\"" : "") + (GetString(args, "filter") is { } f ? $" matching \"{f}\"" : "");

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var automation = ctx.Platform.Automation ?? throw new NotSupportedException("UI automation is not available on this OS.");
        var filter = GetString(args, "filter");
        var elements = automation.GetElements(GetString(args, "window_title"), 400);

        // The window may be on another monitor than the last screenshot. Coordinates are given
        // in that monitor's space, and it becomes the current one (as focus_window does), so
        // the model's next screenshot and clicks land there.
        var spaces = ctx.ScreenSpaces();
        var centres = elements.Select(e => (e.Bounds.X + e.Bounds.Width / 2, e.Bounds.Y + e.Bounds.Height / 2)).ToList();
        var screen = centres.Count > 0 && spaces.Count > 1
            ? centres.Select(c => ctx.ScreenAt(c.Item1, c.Item2)).GroupBy(s => s).MaxBy(g => g.Count())!.Key
            : ctx.CurrentScreen;
        var switched = screen != ctx.CurrentScreen;
        ctx.CurrentScreen = screen;

        var sb = new StringBuilder();
        if (switched)
            sb.AppendLine($"These controls are on screen {screen + 1}; x/y now refer to that screen.");
        var shown = 0;
        for (var i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            var label = string.IsNullOrWhiteSpace(e.Name) ? e.Value ?? "" : e.Name;
            if (filter is { Length: > 0 } && !label.Contains(filter, StringComparison.OrdinalIgnoreCase) && !e.ControlType.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var on = spaces.Count > 1 ? ctx.ScreenAt(centres[i].Item1, centres[i].Item2) : screen;
            var (cx, cy) = spaces[on].ToImage(centres[i].Item1, centres[i].Item2);
            sb.Append('#').Append(e.Id).Append(' ').Append(e.ControlType).Append(" \"").Append(Trim(label)).Append('"');
            if (e.Value is { Length: > 0 } v && v != label) sb.Append(" value=\"").Append(Trim(v)).Append('"');
            if (!e.IsEnabled) sb.Append(" (disabled)");
            sb.Append(" at (").Append(cx).Append(", ").Append(cy).Append(')');
            if (on != screen) sb.Append(" on screen ").Append(on + 1);
            sb.AppendLine();
            if (++shown >= 150)
            {
                sb.AppendLine("[more controls not shown; use filter]");
                break;
            }
        }
        return Task.FromResult(new ToolResult(shown > 0 ? sb.ToString() : "No controls found. The app may not expose accessibility info; use screenshot and mouse tools."));
    }

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 80 ? s[..80] + "…" : s;
    }
}

public sealed class ClickElementTool : DesktopActionTool
{
    public override string Name => "click_element";
    public override string Description =>
        "Press a control by its id from the latest list_ui_elements (buttons, checkboxes, menu items, list items, tabs). " +
        "Use right_click/double_click via mouse_click at the listed coordinates when you need those instead.";
    public override JsonObject Parameters => Schema(("id", Int("Control id from list_ui_elements"), true));
    public override string Describe(JsonObject args) => $"Press control #{GetNumber(args, "id")}";

    protected override Task<string> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var automation = ctx.Platform.Automation ?? throw new NotSupportedException("UI automation is not available on this OS.");
        var id = (int)RequireNumber(args, "id");
        if (automation.Activate(id))
        {
            ctx.Platform.InputOverlay.Show($"Press control #{id}");
            return Task.FromResult($"Pressed control #{id}.");
        }
        throw new ToolArgumentException($"Control #{id} could not be pressed (unknown id or no click action). Call list_ui_elements again, or click its coordinates with mouse_click.");
    }
}
