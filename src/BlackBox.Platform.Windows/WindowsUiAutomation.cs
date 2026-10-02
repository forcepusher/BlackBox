using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

/// <summary>Windows UI Automation (UIA3) through FlaUI.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUiAutomation : IUiAutomation, IDisposable
{
    private static readonly HashSet<ControlType> Interesting =
    [
        ControlType.Button, ControlType.SplitButton, ControlType.Edit, ControlType.ComboBox, ControlType.CheckBox,
        ControlType.RadioButton, ControlType.MenuItem, ControlType.ListItem, ControlType.TabItem, ControlType.Hyperlink,
        ControlType.TreeItem, ControlType.DataItem, ControlType.Slider, ControlType.Spinner, ControlType.Document,
        ControlType.MenuBar, ControlType.Text,
    ];

    private readonly UIA3Automation _automation = new();
    private readonly WindowsWindowManager _windows;
    private readonly WindowsInputController _input;
    private readonly object _gate = new();
    private Dictionary<int, AutomationElement> _snapshot = [];

    public WindowsUiAutomation(WindowsWindowManager windows, WindowsInputController input)
    {
        _windows = windows;
        _input = input;
        // Large trees (browsers, IDEs) can take a while; never let one call hang the agent.
        _automation.ConnectionTimeout = TimeSpan.FromSeconds(5);
        _automation.TransactionTimeout = TimeSpan.FromSeconds(5);
    }

    public IReadOnlyList<UiElement> GetElements(string? windowTitle, int maxCount)
    {
        var hwnd = GetForegroundWindow();
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            var match = _windows.ListWindows().FirstOrDefault(w => w.Title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No window title contains \"{windowTitle}\". Call list_windows to see what is open.");
            hwnd = (nint)match.Handle;
        }

        var root = _automation.FromHandle(hwnd);
        var result = new List<UiElement>();
        var snapshot = new Dictionary<int, AutomationElement>();
        var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);

        // Breadth-first so the important top-level controls come first if we hit the cap.
        var queue = new Queue<(AutomationElement El, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && result.Count < maxCount && DateTime.UtcNow < deadline)
        {
            var (el, depth) = queue.Dequeue();
            if (depth > 0 && TryDescribe(el, out var type, out var name, out var value, out var rect, out var enabled))
            {
                var id = snapshot.Count + 1;
                snapshot[id] = el;
                result.Add(new UiElement(id, type, name, value, rect, enabled));
            }
            if (depth >= 25) continue;
            try
            {
                for (var child = walker.GetFirstChild(el); child is not null; child = walker.GetNextSibling(child))
                    queue.Enqueue((child, depth + 1));
            }
            catch (Exception ex) when (IsUiaFailure(ex)) { }
        }

        lock (_gate) _snapshot = snapshot;
        return result;
    }

    public bool Activate(int id)
    {
        AutomationElement? el;
        lock (_gate) _snapshot.TryGetValue(id, out el);
        if (el is null) return false;

        try
        {
            var p = el.Patterns;
            if (p.Invoke.TryGetPattern(out var invoke)) { invoke.Invoke(); return true; }
            if (p.Toggle.TryGetPattern(out var toggle)) { toggle.Toggle(); return true; }
            if (p.SelectionItem.TryGetPattern(out var select)) { select.Select(); return true; }
            if (p.ExpandCollapse.TryGetPattern(out var expand))
            {
                if (expand.ExpandCollapseState.Value == ExpandCollapseState.Expanded) expand.Collapse(); else expand.Expand();
                return true;
            }
        }
        catch (Exception ex) when (IsUiaFailure(ex)) { }

        // No usable pattern (or it failed): click the middle of the control instead.
        try
        {
            var r = el.BoundingRectangle;
            if (r.IsEmpty) return false;
            _input.MoveMouse(r.X + r.Width / 2, r.Y + r.Height / 2);
            Thread.Sleep(50);
            _input.Click(MouseButton.Left, 1);
            return true;
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            return false;
        }
    }

    private static bool TryDescribe(AutomationElement el, out string type, out string name, out string? value, out ScreenRect rect, out bool enabled)
    {
        type = name = "";
        value = null;
        rect = default;
        enabled = false;
        try
        {
            var ct = el.Properties.ControlType.ValueOrDefault;
            if (!Interesting.Contains(ct) || el.Properties.IsOffscreen.ValueOrDefault)
                return false;
            var r = el.Properties.BoundingRectangle.ValueOrDefault;
            if (r.Width <= 1 || r.Height <= 1)
                return false;
            name = el.Properties.Name.ValueOrDefault ?? "";
            if (el.Patterns.Value.TryGetPattern(out var vp))
                value = vp.Value.ValueOrDefault;
            // Plain text labels only matter when they say something.
            if (ct == ControlType.Text && string.IsNullOrWhiteSpace(name))
                return false;
            type = ct.ToString();
            enabled = el.Properties.IsEnabled.ValueOrDefault;
            rect = new ScreenRect(r.X, r.Y, r.Width, r.Height);
            return true;
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            return false;
        }
    }

    private static bool IsUiaFailure(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException or TimeoutException or InvalidOperationException or UnauthorizedAccessException
            or FlaUI.Core.Exceptions.PropertyNotSupportedException or FlaUI.Core.Exceptions.PatternNotSupportedException
            or FlaUI.Core.Exceptions.ElementNotAvailableException;

    public void Dispose() => _automation.Dispose();
}
