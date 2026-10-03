using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using SkiaSharp;

namespace BlackBox.Platform.Windows;

/// <summary>
/// The on-screen input log: two small layered windows on their own UI thread, one listing the
/// latest inputs in the bottom-right corner, one drawing a ring where the agent clicked.
/// </summary>
/// <remarks>
/// Both windows are click-through (WS_EX_TRANSPARENT), never activated (WS_EX_NOACTIVATE),
/// left out of the taskbar and Alt+Tab (WS_EX_TOOLWINDOW) and excluded from screen capture, so
/// they can't take focus, catch clicks or show up in the model's screenshots. They are drawn with
/// Skia into a DIB and pushed with UpdateLayeredWindow, which needs no compositor.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsInputOverlay : IInputOverlay
{
    private const double LogSeconds = 5, FadeSeconds = 1.5, MarkerSeconds = 1.2;
    private const int MaxLines = 8, PanelWidth = 460, LineHeight = 34, MarkerSize = 72;
    private const uint WM_APP_REFRESH = 0x8001, WM_TIMER = 0x0113;

    private readonly object _lock = new();
    private readonly List<Entry> _entries = [];
    private (int X, int Y, DateTime At)? _marker;
    private volatile bool _enabled;
    private Thread? _thread;
    private uint _threadId;
    private nint _logWindow, _markerWindow;
    private bool _timerOn;
    private WndProc? _wndProc; // kept alive for the window class

    private sealed record Entry(string Text, int Count, DateTime At);

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value)
                EnsureStarted();
            else
            {
                lock (_lock) { _entries.Clear(); _marker = null; }
                Refresh();
            }
        }
    }

    public void Show(string text, (int X, int Y)? point = null)
    {
        if (!_enabled)
            return;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            // "← ×3" instead of three lines of the same key.
            if (_entries.Count > 0 && _entries[^1].Text == text && (now - _entries[^1].At).TotalSeconds < 2)
                _entries[^1] = new Entry(text, _entries[^1].Count + 1, now);
            else
                _entries.Add(new Entry(text, 1, now));
            if (_entries.Count > MaxLines)
                _entries.RemoveAt(0);
            if (point is { } p)
                _marker = (p.X, p.Y, now);
        }
        Refresh();
    }

    private void Refresh()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_APP_REFRESH, 0, 0);
    }

    private void EnsureStarted()
    {
        if (_thread is not null)
            return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "BlackBox input overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _wndProc = DefWindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "BlackBoxInputOverlay",
        };
        RegisterClassEx(ref wc);
        _logWindow = CreateOverlayWindow(wc.hInstance);
        _markerWindow = CreateOverlayWindow(wc.hInstance);
        ready.Set();

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message is WM_APP_REFRESH or WM_TIMER)
            {
                try { Render(); }
                catch { /* a drawing hiccup must never take the agent down */ }
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static nint CreateOverlayWindow(nint instance)
    {
        var hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            "BlackBoxInputOverlay", "", WS_POPUP, 0, 0, 1, 1, 0, 0, instance, 0);
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        return hwnd;
    }

    private void Render()
    {
        var now = DateTime.UtcNow;
        List<(Entry Entry, double Alpha)> lines;
        (int X, int Y, double Age)? marker = null;
        lock (_lock)
        {
            _entries.RemoveAll(e => (now - e.At).TotalSeconds >= LogSeconds);
            lines = _entries.Select(e => (e, Fade((now - e.At).TotalSeconds))).ToList();
            if (_marker is { } m && (now - m.At).TotalSeconds < MarkerSeconds)
                marker = (m.X, m.Y, (now - m.At).TotalSeconds);
        }

        if (lines.Count > 0) DrawLog(lines); else ShowWindow(_logWindow, SW_HIDE);
        if (marker is { } mk) DrawMarker(mk.X, mk.Y, mk.Age / MarkerSeconds); else ShowWindow(_markerWindow, SW_HIDE);

        // Animate only while something is on screen.
        var active = lines.Count > 0 || marker is not null;
        if (active && !_timerOn) { SetTimer(_logWindow, 1, 40, 0); _timerOn = true; }
        else if (!active && _timerOn) { KillTimer(_logWindow, 1); _timerOn = false; }
    }

    private static double Fade(double age) =>
        age < LogSeconds - FadeSeconds ? 1 : Math.Clamp((LogSeconds - age) / FadeSeconds, 0, 1);

    private void DrawLog(List<(Entry Entry, double Alpha)> lines)
    {
        var height = lines.Count * LineHeight + 8;
        SystemParametersInfo(SPI_GETWORKAREA, 0, out var work, 0);
        var x = work.Right - PanelWidth - 16;
        var y = work.Bottom - height - 16;
        Paint(_logWindow, x, y, PanelWidth, height, canvas =>
        {
            using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
            using var back = new SKPaint { IsAntialias = true };
            using var text = new SKPaint { IsAntialias = true, Typeface = typeface, TextSize = 17 };
            for (var i = 0; i < lines.Count; i++)
            {
                var (entry, alpha) = lines[i];
                var label = entry.Count > 1 ? $"{entry.Text}  ×{entry.Count}" : entry.Text;
                var width = Math.Min(PanelWidth, text.MeasureText(label) + 24);
                var top = 4 + i * LineHeight;
                back.Color = new SKColor(20, 20, 24, (byte)(200 * alpha));
                canvas.DrawRoundRect(new SKRect(PanelWidth - width, top, PanelWidth, top + LineHeight - 6), 6, 6, back);
                text.Color = new SKColor(255, 255, 255, (byte)(255 * alpha));
                canvas.DrawText(label, PanelWidth - width + 12, top + 20, text);
            }
        });
    }

    private void DrawMarker(int cx, int cy, double progress)
    {
        Paint(_markerWindow, cx - MarkerSize / 2, cy - MarkerSize / 2, MarkerSize, MarkerSize, canvas =>
        {
            var alpha = (byte)(255 * (1 - progress));
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, Color = new SKColor(255, 64, 160, alpha) };
            using var dot = new SKPaint { IsAntialias = true, Color = new SKColor(255, 64, 160, alpha) };
            canvas.DrawCircle(MarkerSize / 2f, MarkerSize / 2f, 8 + (float)progress * 24, ring);
            canvas.DrawCircle(MarkerSize / 2f, MarkerSize / 2f, 4, dot);
        });
    }

    /// <summary>Draws into a premultiplied BGRA DIB and hands it to the layered window.</summary>
    private static unsafe void Paint(nint hwnd, int x, int y, int width, int height, Action<SKCanvas> draw)
    {
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, // top-down
            biPlanes = 1, biBitCount = 32,
        };
        var bitmap = CreateDIBSection(screen, ref header, 0, out var bits, 0, 0);
        var old = SelectObject(memory, bitmap);
        try
        {
            using (var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), bits, width * 4))
            {
                surface.Canvas.Clear(SKColors.Transparent);
                draw(surface.Canvas);
                surface.Canvas.Flush();
            }
            var dst = new POINT { X = x, Y = y };
            var size = new SIZE { cx = width, cy = height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(hwnd, screen, ref dst, ref size, memory, ref src, 0, ref blend, ULW_ALPHA);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    // ---- interop ---------------------------------------------------------------------

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOPMOST = 0x00000008,
        WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11, ULW_ALPHA = 2, SPI_GETWORKAREA = 0x0030;
    private const int SW_HIDE = 0;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetTimer(nint hWnd, nint id, uint ms, nint proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hWnd, nint id);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hWnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, out RECT rect, uint winIni);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint hWnd, nint hdcDst, ref POINT dst, ref SIZE size, nint hdcSrc, ref POINT src, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
