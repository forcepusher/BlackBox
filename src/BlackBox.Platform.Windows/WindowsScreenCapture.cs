using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

/// <summary>
/// GDI screen capture. Works on every Windows .NET runs on and needs no extra packages.
/// BlackBox's own windows (the main window, its dialogs, menus and tooltips) are left out of each capture,
/// so the agent sees the desktop behind them, but only while it is taken: screen recorders and
/// screen sharing show them the rest of the time. WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+) takes
/// effect at once for a GDI capture and can be set from any thread of the process; on older
/// Windows it blacks the window out instead.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsScreenCapture : IScreenCapture
{
    // One capture at a time: one that finishes must not show a window to another still taking its picture.
    private static readonly object Gate = new();

    public IReadOnlyList<ScreenRect> GetScreens()
    {
        var monitors = new List<(ScreenRect Rect, bool Primary)>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint hdc, ref RECT rect, nint lParam) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var m = info.rcMonitor;
                monitors.Add((new ScreenRect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top), (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, 0);
        if (monitors.Count == 0)
            return [new(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN))];
        // The primary first, then left to right and top to bottom: the same numbering every call.
        return monitors.OrderByDescending(m => m.Primary).ThenBy(m => m.Rect.X).ThenBy(m => m.Rect.Y).Select(m => m.Rect).ToList();
    }

    public RawImage Capture(ScreenRect region, bool includeCursor)
    {
        lock (Gate)
        {
            var hidden = HideOwnWindows(region);
            try
            {
                return CaptureNow(region, includeCursor);
            }
            finally
            {
                foreach (var hwnd in hidden)
                    SetWindowDisplayAffinity(hwnd, WDA_NONE);
            }
        }
    }

    /// <summary>
    /// Leaves this process's visible windows over the region out of screen capture and returns
    /// them. Windows that are left out already (the click log) are not touched.
    /// </summary>
    private static List<nint> HideOwnWindows(ScreenRect region)
    {
        var hidden = new List<nint>();
        var process = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out var owner) != 0 && owner == process && IsWindowVisible(hwnd) && !IsIconic(hwnd)
                && GetWindowRect(hwnd, out var r) && r.Left < region.X + region.Width && r.Right > region.X && r.Top < region.Y + region.Height && r.Bottom > region.Y
                && GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == WDA_NONE
                && SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
                hidden.Add(hwnd);
            return true;
        }, 0);
        return hidden;
    }

    /// <summary>The screen as any program captures it, with whatever windows are not left out of capture.</summary>
    internal static unsafe RawImage CaptureNow(ScreenRect region, bool includeCursor)
    {
        var screenDc = GetDC(0);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, region.Width, region.Height);
        var old = SelectObject(memDc, bitmap);
        var selected = true;
        try
        {
            if (!BitBlt(memDc, 0, 0, region.Width, region.Height, screenDc, region.X, region.Y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("Screen capture failed (BitBlt). The screen may be locked or on a secure desktop.");

            if (includeCursor)
                DrawCursor(memDc, region);

            SelectObject(memDc, old);
            selected = false;
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = region.Width,
                biHeight = -region.Height, // negative = top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            var pixels = new byte[region.Width * region.Height * 4];
            fixed (byte* p = pixels)
            {
                if (GetDIBits(memDc, bitmap, 0, (uint)region.Height, p, ref header, DIB_RGB_COLORS) == 0)
                    throw new InvalidOperationException("Screen capture failed (GetDIBits).");
            }

            // GDI leaves the alpha channel undefined; make it opaque.
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;

            return new RawImage(region.Width, region.Height, pixels);
        }
        finally
        {
            // A bitmap still selected into a DC isn't deleted: after a failed BitBlt (the screen
            // locked, a UAC prompt up) every capture left a screen-sized bitmap and a GDI handle behind.
            if (selected)
                SelectObject(memDc, old);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    private static void DrawCursor(nint dc, ScreenRect region)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == 0)
            return;
        if (!GetIconInfo(info.hCursor, out var icon))
            return;
        try
        {
            DrawIconEx(dc,
                info.ptScreenPos.X - icon.xHotspot - region.X,
                info.ptScreenPos.Y - icon.yHotspot - region.Y,
                info.hCursor, 0, 0, 0, 0, DI_NORMAL);
        }
        finally
        {
            if (icon.hbmMask != 0) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != 0) DeleteObject(icon.hbmColor);
        }
    }
}
