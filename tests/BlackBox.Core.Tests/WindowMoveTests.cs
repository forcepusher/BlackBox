#if WINDOWS
using System.Runtime.InteropServices;
using BlackBox.Core.Platform;
using BlackBox.Platform.Windows;
using Xunit;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Core.Tests;

/// <summary>
/// The chat window making room for a tool on its left (Video editor): moved and widened in one step,
/// and what it gained filled at once rather than black until it draws there. Moved, then widened at
/// the next layout, the chat jumped sideways and back. Puts a small red window near the screen
/// corner for a moment.
/// </summary>
[Collection(RealScreen.Name)]
public class WindowMoveTests
{
    [Fact]
    public void A_window_moved_and_widened_does_it_in_one_step_and_fills_what_it_gained()
    {
        _ = new WindowsPlatform(); // physical pixels from here on
        using var window = new LateWindow(new ScreenRect(200, 40, 120, 120));
        Assert.True(GetWindowRect(window.Handle, out var before));
        window.Moves.Clear();

        Assert.True(WindowsPlatform.MoveAndWiden(window.Handle, -100, 160, 0x00FF00));

        Assert.True(GetWindowRect(window.Handle, out var after));
        Assert.Equal((before.Left - 100, before.Top, before.Right + 60, before.Bottom), (after.Left, after.Top, after.Right, after.Bottom));
        // One move, already at the new place and size: never moved and not yet widened.
        var move = Assert.Single(window.Moves);
        Assert.Equal((after.Left, after.Right), move);

        Thread.Sleep(200); // composed
        Assert.True(Share(WindowsScreenCapture.CaptureNow(new ScreenRect(after.Left + 10, after.Top + 20, 80, 80), includeCursor: false), green: true) > 0.9,
            "what the window gained on the left isn't filled");
        Assert.True(Share(WindowsScreenCapture.CaptureNow(new ScreenRect(before.Left + 20, after.Top + 20, 80, 80), includeCursor: false), green: false) > 0.9,
            "where the window was is no longer what it drew");
        Assert.True(Share(WindowsScreenCapture.CaptureNow(new ScreenRect(before.Right + 10, after.Top + 20, 40, 80), includeCursor: false), green: true) > 0.9,
            "what the window gained on the right isn't filled");
    }

    private static double Share(RawImage image, bool green)
    {
        var count = 0;
        for (var i = 0; i < image.Bgra.Length; i += 4)
        {
            var (b, g, r) = (image.Bgra[i], image.Bgra[i + 1], image.Bgra[i + 2]);
            if (green ? g > 215 && r < 40 && b < 40 : r > 215 && g < 40 && b < 40)
                count++;
        }
        return count / (double)(image.Width * image.Height);
    }

    /// <summary>
    /// A topmost window of this process that drew itself red once and then draws nothing more, as
    /// Avalonia hasn't yet right after a resize; on its own thread with a message loop. Records
    /// where each move or resize put its left and right edges.
    /// </summary>
    private sealed class LateWindow : IDisposable
    {
        private const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_WINDOWPOSCHANGED = 0x0047;
        private readonly Thread _thread;
        private uint _threadId;
        private readonly WndProc _proc;
        private bool _drawn;

        public nint Handle { get; private set; }
        public List<(int Left, int Right)> Moves { get; } = [];

        public LateWindow(ScreenRect r)
        {
            _proc = Proc;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                    hInstance = GetModuleHandle(null),
                    hbrBackground = CreateSolidBrush(0x000000FF),
                    lpszClassName = "BlackBoxMoveTest",
                };
                RegisterClassEx(ref wc);
                Handle = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, "move test",
                    WS_POPUP | 0x10000000 /* WS_VISIBLE */, r.X, r.Y, r.Width, r.Height, 0, 0, wc.hInstance, 0);
                ready.Set();
                while (GetMessage(out var msg, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }) { IsBackground = true };
            _thread.Start();
            ready.Wait();
            Thread.Sleep(300); // painted and composed
        }

        private nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
        {
            switch (msg)
            {
                case WM_ERASEBKGND when _drawn:
                    return 1;
                case WM_PAINT when _drawn:
                    ValidateRect(hwnd, 0);
                    return 0;
                case WM_PAINT:
                    var painted = DefWindowProc(hwnd, msg, wParam, lParam);
                    _drawn = true;
                    return painted;
                case WM_WINDOWPOSCHANGED:
                    GetWindowRect(hwnd, out var rect);
                    Moves.Add((rect.Left, rect.Right));
                    break;
            }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            // Its windows go with the thread.
            PostThreadMessage(_threadId, WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        [DllImport("user32.dll")]
        private static extern bool ValidateRect(nint hwnd, nint rect);
    }
}
#endif
