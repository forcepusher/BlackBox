using System.Runtime.Versioning;
using Sloperator.Core.Platform;

namespace Sloperator.Platform.Windows;

/// <summary>
/// What a window of Sloperator's shows on screen, read off the screen: for the File inspector panel's Area,
/// which freezes what it shows to mark an area on, a previewer drawing from another process included.
/// Sloperator's other windows over it (a tooltip, a menu) are left out.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowPixels
{
    /// <summary>All of window <paramref name="hwnd"/> (a child window too); null when it isn't on screen.</summary>
    public static RawImage? Of(nint hwnd) => WindowsScreenCapture.CaptureWindow(hwnd);

    /// <summary>Part of window <paramref name="hwnd"/>, in screen pixels; null when it is empty.</summary>
    public static RawImage? In(nint hwnd, int x, int y, int width, int height) => WindowsScreenCapture.CaptureIn(hwnd, new ScreenRect(x, y, width, height));
}
