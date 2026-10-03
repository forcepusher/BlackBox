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
        var space = ctx.GetScreenSpace();
        var filter = GetString(args, "filter");
        var elements = automation.GetElements(GetString(args, "window_title"), 400);

        var sb = new StringBuilder();
        var shown = 0;
        foreach (var e in elements)
        {
            var label = string.IsNullOrWhiteSpace(e.Name) ? e.Value ?? "" : e.Name;
            if (filter is { Length: > 0 } && !label.Contains(filter, StringComparison.OrdinalIgnoreCase) && !e.ControlType.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var (cx, cy) = space.ToImage(e.Bounds.X + e.Bounds.Width / 2, e.Bounds.Y + e.Bounds.Height / 2);
            sb.Append('#').Append(e.Id).Append(' ').Append(e.ControlType).Append(" \"").Append(Trim(label)).Append('"');
            if (e.Value is { Length: > 0 } v && v != label) sb.Append(" value=\"").Append(Trim(v)).Append('"');
            if (!e.IsEnabled) sb.Append(" (disabled)");
            sb.Append(" at (").Append(cx).Append(", ").Append(cy).AppendLine(")");
            if (++shown >= 150)
            {
                sb.AppendLine("[more controls not shown; use filter]");
                break;
            }
        }
        return Task.FromResult(new ToolResult(sb.Length > 0 ? sb.ToString() : "No controls found. The app may not expose accessibility info; use screenshot and mouse tools."));
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
