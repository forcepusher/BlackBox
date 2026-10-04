using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

/// <summary>
/// The Break key (Pause, or Ctrl+Pause) as a system-wide stop, through a low-level keyboard hook
/// on its own message-loop thread. It sees the key whatever window has focus, which is the point:
/// while the agent works, focus is wherever it clicked last.
/// </summary>
/// <remarks>
/// The agent's own key presses are tagged (<see cref="WindowsInputController.InputTag"/>) and
/// ignored, so a model pressing "pause" never stops itself. A press that stopped a run is
/// swallowed; otherwise it goes through to the focused app as usual.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsStopKey : IStopKey
{
    public IDisposable Watch(Func<bool> onPressed) => new Hook(onPressed);

    private sealed class Hook : IDisposable
    {
        private readonly Func<bool> _onPressed;
        private readonly LowLevelKeyboardProc _proc; // kept alive for as long as the hook is installed
        private nint _hook;
        private uint _threadId;

        public Hook(Func<bool> onPressed)
        {
            _onPressed = onPressed;
            _proc = Callback;
            using var ready = new ManualResetEventSlim();
            new Thread(() => Run(ready)) { IsBackground = true, Name = "BlackBox stop key" }.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        private void Run(ManualResetEventSlim ready)
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            ready.Set();
            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(_hook);
        }

        private nint Callback(int code, nint wParam, nint lParam)
        {
            if (code >= 0 && (uint)wParam is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (key.vkCode is VK_PAUSE or VK_CANCEL && key.dwExtraInfo != WindowsInputController.InputTag)
                {
                    bool stopped;
                    try { stopped = _onPressed(); }
                    catch { stopped = false; } // a hook callback must never throw into the system
                    if (stopped)
                        return 1;
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        }
    }
}
