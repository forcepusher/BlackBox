using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

/// <summary>
/// Keeps BlackBox's window visible to the user but out of the agent's way:
/// <list type="bullet">
/// <item>WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+) makes screenshots show what is behind it.</item>
/// <item>When a click or drag lands on it, it is pushed to the bottom of the z-order for that action.</item>
/// <item>Before keystrokes, focus moves from it to the window underneath.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHostWindow : IHostWindow
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private static readonly nint HWND_TOP = 0;
    private static readonly nint HWND_BOTTOM = 1;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    private const uint GW_HWNDNEXT = 2;

    private nint _hwnd;

    public bool HiddenFromCapture { get; private set; }

    public void Attach(nint handle, bool hideFromCapture)
    {
        _hwnd = handle;
        SetHiddenFromCapture(hideFromCapture);
    }

    public void SetHiddenFromCapture(bool hide)
    {
        if (_hwnd == 0) return;
        HiddenFromCapture = SetWindowDisplayAffinity(_hwnd, hide ? WDA_EXCLUDEFROMCAPTURE : 0) && hide;
    }

    public IDisposable StepAside(params (int X, int Y)[] points)
    {
        if (_hwnd == 0 || IsIconic(_hwnd) || !GetWindowRect(_hwnd, out var r))
            return Restore.None;
        if (!points.Any(p => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom))
            return Restore.None;

        SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Thread.Sleep(60); // let the window underneath become the hit-test target
        return new Restore(_hwnd);
    }

    public void YieldFocus()
    {
        if (_hwnd == 0 || GetForegroundWindow() != _hwnd)
            return;
        if (NextWindowBelow(_hwnd) is var next and not 0)
        {
            SetForegroundWindow(next);
            Thread.Sleep(60);
        }
    }

    /// <summary>The next normal, visible top-level window under <paramref name="hwnd"/> in z-order.</summary>
    internal static nint NextWindowBelow(nint hwnd)
    {
        for (var w = GetWindow(hwnd, GW_HWNDNEXT); w != 0; w = GetWindow(w, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(w) || IsIconic(w) || GetWindow(w, GW_OWNER) != 0) continue;
            if ((GetWindowLongPtr(w, GWL_EXSTYLE) & (nint)WS_EX_TOOLWINDOW) != 0) continue;
            if (DwmGetWindowAttribute(w, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (GetWindowTextLength(w) == 0) continue;
            return w;
        }
        return 0;
    }

    private sealed class Restore(nint hwnd) : IDisposable
    {
        public static readonly IDisposable None = new Restore(0);

        public void Dispose()
        {
            if (hwnd == 0) return;
            // Give the target a moment to react (open a menu, gain focus) before coming back on top.
            Thread.Sleep(150);
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
