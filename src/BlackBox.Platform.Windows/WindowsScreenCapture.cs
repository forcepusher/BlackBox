using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

/// <summary>GDI screen capture. Works everywhere back to Windows 7 and needs no extra packages.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsScreenCapture : IScreenCapture
{
    public ScreenRect GetPrimaryScreen() => new(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    public unsafe RawImage Capture(ScreenRect region, bool includeCursor)
    {
        var screenDc = GetDC(0);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, region.Width, region.Height);
        var old = SelectObject(memDc, bitmap);
        try
        {
            if (!BitBlt(memDc, 0, 0, region.Width, region.Height, screenDc, region.X, region.Y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("Screen capture failed (BitBlt). The screen may be locked or on a secure desktop.");

            if (includeCursor)
                DrawCursor(memDc, region);

            SelectObject(memDc, old);
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
