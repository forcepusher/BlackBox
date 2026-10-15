using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

/// <summary>
/// Esc as a system-wide stop, through a low-level keyboard hook on its own message-loop thread.
/// It sees the key whatever window has focus, which is the point: while the agent works, focus
/// is wherever it clicked last.
/// </summary>
/// <remarks>
/// The agent's own key presses are tagged (<see cref="WindowsInputController.InputTag"/>) and
/// ignored, so the many Esc presses a model makes never stop it. A press that stopped a run is
/// swallowed whole, so the window the agent was working in never sees it: its key-down, the
/// repeats while it is held and its key-up, and also the keys pressed while it is held (mashed in
/// a hurry, they would land in that window). Otherwise Esc goes through as usual.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsStopKey : IStopKey
{
    public IDisposable Watch(Func<bool> onPressed) => new Hook(onPressed);

    private sealed class Hook : IDisposable
    {
        /// <summary>
        /// A held key repeats its key-down within a second (two with Filter Keys), so a swallowed
        /// key not seen for longer was let go where the hook can't see it (in an elevated window,
        /// on the secure desktop) and no longer holds the keyboard.
        /// </summary>
        private const long HeldGapMs = 2500;

        private readonly Func<bool> _onPressed;
        private readonly LowLevelKeyboardProc _proc; // kept alive for as long as the hook is installed
        // Used on the hook's thread only.
        private readonly Dictionary<uint, long> _swallowed = []; // keys swallowed until let go, and when each last went down
        private bool _stopHeld; // the Esc press that stopped a run is still held
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
            if (code >= 0)
            {
                var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (key.dwExtraInfo != WindowsInputController.InputTag && Swallow((uint)wParam, key.vkCode))
                    return 1;
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        /// <summary>Whether a key event of the user's goes no further.</summary>
        private bool Swallow(uint message, uint vk)
        {
            if (message is WM_KEYUP or WM_SYSKEYUP)
            {
                if (vk == VK_ESCAPE) _stopHeld = false;
                // Never the key-up of a key Windows has down (its key-down got past the hook after
                // all): that would leave it stuck down.
                return _swallowed.Remove(vk) && !IsDown(vk);
            }
            if (message is not (WM_KEYDOWN or WM_SYSKEYDOWN))
                return false;

            var now = Environment.TickCount64;
            if (_swallowed.TryGetValue(vk, out var last))
            {
                if (now - last < HeldGapMs)
                {
                    _swallowed[vk] = now; // a held key repeating its key-down
                    return true;
                }
                _swallowed.Remove(vk); // its key-up went where the hook can't see: a new press
            }
            if (_stopHeld && !(_swallowed.TryGetValue(VK_ESCAPE, out var held) && now - held < HeldGapMs))
                _stopHeld = false;
            if (!_stopHeld && vk != VK_ESCAPE)
                return false;
            // Down in Windows already: the repeat of a key held since before (pressed before the run,
            // or before the Esc that stopped it), or a key the agent holds down.
            if (IsDown(vk))
                return false;
            if (!_stopHeld)
            {
                if (!Stopped())
                    return false; // no run to stop: Esc does its usual job
                _stopHeld = true;
            }
            // else a key pressed while the Esc that stopped the run is held: not meant for the window
            // the agent was in either.
            _swallowed[vk] = now;
            return true;
        }

        private bool Stopped()
        {
            try { return _onPressed(); }
            catch { return false; } // a hook callback must never throw into the system
        }

        /// <summary>Whether Windows has the key down. In the hook, that is as it was before the event at hand.</summary>
        private static bool IsDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

        public void Dispose()
        {
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        }
    }
}
