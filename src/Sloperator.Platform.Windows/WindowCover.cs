using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Sloperator.Platform.Windows.Native;

namespace Sloperator.Platform.Windows;

/// <summary>
/// A picture of what a window is about to show, held just over it while it is moved and resized,
/// until its program has drawn the same. Resized, an Avalonia window shows what it last drew,
/// from its top-left corner, until it draws again a frame or several later: the chat where the
/// panel was to be, or the panel's left part in a window as narrow as the chat (captured at 60 fps,
/// 2026-10-25). Filling with a colour (GDI) doesn't show: ANGLE's surface is over it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowCover
{
    private static WndProc? _proc; // kept alive for the window class
    private static nint _cover;

    /// <summary>
    /// Makes, not yet shown, the cover with <paramref name="bgra"/> (rows from the top,
    /// <paramref name="width"/> pixels across, opaque) at <paramref name="x"/>, <paramref name="y"/> on
    /// the screen, over <paramref name="owner"/> and nothing else; click-through, never activated.
    /// It is shown in the same step as the window is resized (<see cref="WindowsPlatform.MoveAndWiden"/>):
    /// shown first, the screen showed it a frame before the window changed, the chat twice side by
    /// side as the panel closed. 0 if it couldn't be made.
    /// </summary>
    internal static unsafe nint Make(nint owner, int x, int y, int width, byte[] bgra)
    {
        EnsurePhysicalPixels();
        Hide();
        var height = bgra.Length / 4 / width;
        if (width <= 0 || height <= 0)
            return 0;
        var instance = GetModuleHandle(null);
        if (_proc is null)
        {
            _proc = DefWindowProc;
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = instance,
                lpszClassName = "SloperatorCover",
            };
            RegisterClassEx(ref wc);
        }
        // Owned by the window: above it in the z-order, and only above it.
        var cover = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            "SloperatorCover", "", WS_POPUP, x, y, width, height, owner, 0, instance, 0);
        if (cover == 0)
            return 0;
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(memory, ref header, 0, out var bits, 0, 0);
        var shown = false;
        if (bitmap != 0)
        {
            Marshal.Copy(bgra, 0, bits, width * height * 4);
            var old = SelectObject(memory, bitmap);
            var to = new POINT { X = x, Y = y };
            var from = new POINT();
            var size = new SIZE { cx = width, cy = height };
            var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            shown = UpdateLayeredWindow(cover, screen, ref to, ref size, memory, ref from, 0, ref blend, ULW_ALPHA);
            SelectObject(memory, old);
            DeleteObject(bitmap);
        }
        DeleteDC(memory);
        ReleaseDC(0, screen);
        if (!shown)
        {
            DestroyWindow(cover);
            return 0;
        }
        _cover = cover;
        return cover;
    }

    /// <summary>Takes the picture away: the window under it shows what it now draws itself.</summary>
    public static void Hide()
    {
        if (_cover == 0)
            return;
        DestroyWindow(_cover);
        _cover = 0;
    }
}
