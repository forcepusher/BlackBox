using System.Diagnostics;
using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsWindowManager : IWindowManager
{
    public IReadOnlyList<WindowInfo> ListWindows()
    {
        var result = new List<WindowInfo>();
        var names = new Dictionary<uint, string>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != 0)
                return true;
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE) & (nint)WS_EX_TOOLWINDOW) != 0)
                return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true; // hidden UWP windows and windows on other virtual desktops

            var title = GetTitle(hwnd);
            if (title.Length == 0)
                return true;

            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hwnd, out r);

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId)
                return true; // BlackBox itself is not something the agent should operate
            if (!names.TryGetValue(pid, out var proc))
            {
                try { proc = Process.GetProcessById((int)pid).ProcessName; }
                catch (ArgumentException) { proc = "?"; }
                names[pid] = proc;
            }

            result.Add(new WindowInfo(hwnd, title, proc, new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), IsIconic(hwnd)));
            return true;
        }, 0);

        return result;
    }

    public bool FocusWindow(long handle)
    {
        var hwnd = (nint)handle;
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);

        if (SetForegroundWindow(hwnd))
            return true;

        // Windows only lets the foreground process steal focus. A synthetic Alt tap
        // counts as user input and lifts that restriction for the next call.
        var alt = new WindowsInputController();
        alt.PressKeys(["alt"]);
        BringWindowToTop(hwnd);
        return SetForegroundWindow(hwnd) || GetForegroundWindow() == hwnd;
    }

    private static unsafe string GetTitle(nint hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var buffer = new char[len + 1];
        fixed (char* p = buffer)
        {
            var n = GetWindowText(hwnd, p, buffer.Length);
            return new string(buffer, 0, n);
        }
    }
}
