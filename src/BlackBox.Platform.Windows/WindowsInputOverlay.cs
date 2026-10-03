using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using SkiaSharp;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

/// <summary>
/// The on-screen input log: two small layered windows on their own UI thread, one listing the
/// latest inputs in the bottom-right corner, one drawing a ring where the agent clicked.
/// </summary>
/// <remarks>
/// Both windows are click-through (WS_EX_TRANSPARENT), never activated (WS_EX_NOACTIVATE),
/// left out of the taskbar and Alt+Tab (WS_EX_TOOLWINDOW) and excluded from screen capture, so
/// they can't take focus, catch clicks or show up in the model's screenshots. They are drawn with
/// Skia and use a colour key plus a window-wide opacity rather than per-pixel alpha:
/// Windows refuses capture exclusion for windows drawn with UpdateLayeredWindow (error 8).
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsInputOverlay : IInputOverlay
{
    private const double FadeSeconds = 1.5, MarkerSeconds = 1.2;
    private const int MaxLines = 8, PanelWidth = 460, LineHeight = 34, MarkerSize = 72;
    private const uint WM_APP_REFRESH = WM_APP + 1;
    private const uint ColorKey = 0x00FF00FF; // magenta, as COLORREF (0x00BBGGRR)

    private readonly object _lock = new();
    private readonly List<Entry> _entries = [];
    private (int X, int Y, DateTime At)? _marker;
    private volatile bool _enabled;
    private double _logSeconds = 15;
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

    public TimeSpan Duration
    {
        get => TimeSpan.FromSeconds(_logSeconds);
        set => _logSeconds = Math.Max(FadeSeconds, value.TotalSeconds);
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
        // Colour-key transparency (not UpdateLayeredWindow) so that capture exclusion is allowed.
        SetLayeredWindowAttributes(hwnd, ColorKey, 255, LWA_COLORKEY | LWA_ALPHA);
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
            _entries.RemoveAll(e => (now - e.At).TotalSeconds >= _logSeconds);
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

    private double Fade(double age) =>
        age < _logSeconds - FadeSeconds ? 1 : Math.Clamp((_logSeconds - age) / FadeSeconds, 0, 1);

    private static readonly SKColor LineBack = new(28, 28, 32);

    private void DrawLog(List<(Entry Entry, double Alpha)> lines)
    {
        var height = lines.Count * LineHeight + 8;
        SystemParametersInfo(SPI_GETWORKAREA, 0, out var work, 0);
        var x = work.Right - PanelWidth - 16;
        var y = work.Bottom - height - 16;
        Paint(_logWindow, x, y, PanelWidth, height, 230, canvas =>
        {
            using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
            // No antialiasing on the boxes: their edges meet the colour key, and blended edge
            // pixels would show as a pink fringe.
            using var back = new SKPaint { IsAntialias = false, Color = LineBack };
            using var text = new SKPaint { IsAntialias = true, Typeface = typeface, TextSize = 17 };
            for (var i = 0; i < lines.Count; i++)
            {
                var (entry, alpha) = lines[i];
                var label = entry.Count > 1 ? $"{entry.Text}  ×{entry.Count}" : entry.Text;
                var width = Math.Min(PanelWidth, text.MeasureText(label) + 24);
                var top = 4 + i * LineHeight;
                canvas.DrawRect(new SKRect(PanelWidth - width, top, PanelWidth, top + LineHeight - 6), back);
                // A line fades by its text sinking into the box; the box goes when the line does.
                text.Color = Blend(LineBack, SKColors.White, alpha);
                canvas.DrawText(label, PanelWidth - width + 12, top + 20, text);
            }
        });
    }

    private void DrawMarker(int cx, int cy, double progress)
    {
        // The whole marker window fades, so the ring can be drawn fully opaque.
        Paint(_markerWindow, cx - MarkerSize / 2, cy - MarkerSize / 2, MarkerSize, MarkerSize, (byte)(255 * (1 - progress)), canvas =>
        {
            using var ring = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 4, Color = new SKColor(255, 64, 160) };
            using var dot = new SKPaint { IsAntialias = false, Color = new SKColor(255, 64, 160) };
            canvas.DrawCircle(MarkerSize / 2f, MarkerSize / 2f, 8 + (float)progress * 24, ring);
            canvas.DrawCircle(MarkerSize / 2f, MarkerSize / 2f, 4, dot);
        });
    }

    private static SKColor Blend(SKColor from, SKColor to, double t) => new(
        (byte)(from.Red + (to.Red - from.Red) * t),
        (byte)(from.Green + (to.Green - from.Green) * t),
        (byte)(from.Blue + (to.Blue - from.Blue) * t));

    /// <summary>
    /// Moves the window, sets its overall opacity and draws into it. Pixels left in the key colour
    /// are see-through. Layered windows keep what is drawn into them, so no WM_PAINT is needed.
    /// </summary>
    private static void Paint(nint hwnd, int x, int y, int width, int height, byte opacity, Action<SKCanvas> draw)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetLayeredWindowAttributes(hwnd, ColorKey, opacity, LWA_COLORKEY | LWA_ALPHA);
        var target = GetDC(hwnd);
        var memory = CreateCompatibleDC(target);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, // top-down
            biPlanes = 1, biBitCount = 32,
        };
        var bitmap = CreateDIBSection(target, ref header, 0, out var bits, 0, 0);
        var old = SelectObject(memory, bitmap);
        try
        {
            using (var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), bits, width * 4))
            {
                surface.Canvas.Clear(new SKColor(0xFF, 0x00, 0xFF)); // = ColorKey
                draw(surface.Canvas);
                surface.Canvas.Flush();
            }
            BitBlt(target, 0, 0, width, height, memory, 0, 0, SRCCOPY);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(hwnd, target);
        }
    }
}
