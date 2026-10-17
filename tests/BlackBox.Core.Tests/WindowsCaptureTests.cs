#if WINDOWS
using System.Runtime.InteropServices;
using BlackBox.Core.Platform;
using BlackBox.Platform.Windows;
using Xunit;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Core.Tests;

/// <summary>
/// BlackBox's own windows and its screenshots: left out of each one while it is taken, and back in
/// what screen recorders and screen sharing capture right after (there was an option for that before).
/// Puts a small red window in the screen corner for a moment.
/// </summary>
public class WindowsCaptureTests
{
    [Fact]
    public void BlackBoxs_windows_are_left_out_of_its_screenshots_only_while_one_is_taken()
    {
        var capture = new WindowsPlatform().Screen; // physical pixels from here on
        var region = new ScreenRect(40, 40, 160, 160);
        using var window = new RedWindow(region);

        Assert.True(RedShare(WindowsScreenCapture.CaptureNow(region, includeCursor: false)) > 0.9, "the test window isn't on screen");
        Assert.True(RedShare(capture.Capture(region, includeCursor: false)) < 0.1, "the window showed in BlackBox's screenshot");
        Assert.True(RedShare(WindowsScreenCapture.CaptureNow(region, includeCursor: false)) > 0.9, "the window stayed hidden from other programs' captures");
        Assert.True(GetWindowDisplayAffinity(window.Handle, out var affinity) && affinity == WDA_NONE);

        // One left out of capture for good (the click log) stays that way.
        SetWindowDisplayAffinity(window.Handle, WDA_EXCLUDEFROMCAPTURE);
        Assert.True(RedShare(capture.Capture(region, includeCursor: false)) < 0.1);
        Assert.True(GetWindowDisplayAffinity(window.Handle, out affinity) && affinity == WDA_EXCLUDEFROMCAPTURE);
    }

    private static double RedShare(RawImage image)
    {
        var red = 0;
        for (var i = 0; i < image.Bgra.Length; i += 4)
            if (image.Bgra[i] < 40 && image.Bgra[i + 1] < 40 && image.Bgra[i + 2] > 215)
                red++;
        return red / (double)(image.Width * image.Height);
    }

    /// <summary>A plain red, topmost window of this process over the region, on its own thread with a message loop.</summary>
    private sealed class RedWindow : IDisposable
    {
        private readonly Thread _thread;
        private uint _threadId;
        private readonly WndProc _proc = DefWindowProc;

        public nint Handle { get; private set; }

        public RedWindow(ScreenRect r)
        {
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
                    lpszClassName = "BlackBoxCaptureTest",
                };
                RegisterClassEx(ref wc);
                Handle = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, "capture test",
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

        public void Dispose()
        {
            // Its windows go with the thread.
            PostThreadMessage(_threadId, WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        [DllImport("gdi32.dll")]
        private static extern nint CreateSolidBrush(uint color);
    }
}
#endif
