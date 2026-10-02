using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsInputController : IInputController
{
    private const int WheelDelta = 120;
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    // Virtual-key codes for named keys. Letters, digits and punctuation are resolved separately.
    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["lctrl"] = 0xA2, ["rctrl"] = 0xA3,
        ["alt"] = 0x12, ["menu"] = 0x12, ["lalt"] = 0xA4, ["ralt"] = 0xA5, ["altgr"] = 0xA5,
        ["shift"] = 0x10, ["lshift"] = 0xA0, ["rshift"] = 0xA1,
        ["win"] = 0x5B, ["windows"] = 0x5B, ["super"] = 0x5B, ["meta"] = 0x5B, ["cmd"] = 0x5B, ["lwin"] = 0x5B, ["rwin"] = 0x5C,
        ["enter"] = 0x0D, ["return"] = 0x0D,
        ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["tab"] = 0x09, ["space"] = 0x20, ["spacebar"] = 0x20,
        ["backspace"] = 0x08, ["back"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pgup"] = 0x21, ["page_up"] = 0x21,
        ["pagedown"] = 0x22, ["pgdn"] = 0x22, ["page_down"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["arrowup"] = 0x26, ["arrowdown"] = 0x28, ["arrowleft"] = 0x25, ["arrowright"] = 0x27,
        ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["pause"] = 0x13,
        ["apps"] = 0x5D, ["contextmenu"] = 0x5D,
        ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["volumemute"] = 0xAD,
        ["playpause"] = 0xB3, ["nexttrack"] = 0xB0, ["prevtrack"] = 0xB1,
    };

    // Keys that need KEYEVENTF_EXTENDEDKEY to behave like the dedicated keys (not the numpad ones).
    private static readonly HashSet<ushort> ExtendedKeys =
        [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0xA3, 0xA5, 0x90, 0x2C, 0x6F];

    static WindowsInputController()
    {
        for (var i = 1; i <= 24; i++)
            NamedKeys["f" + i] = (ushort)(0x70 + i - 1);
    }

    public (int X, int Y) GetCursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    public void MoveMouse(int x, int y) => SetCursorPos(x, y);

    public void MouseDown(MouseButton button) => SendMouse(Flags(button).Down);

    public void MouseUp(MouseButton button) => SendMouse(Flags(button).Up);

    public void Click(MouseButton button, int count)
    {
        var (down, up) = Flags(button);
        for (var i = 0; i < count; i++)
        {
            SendMouse(down);
            SendMouse(up);
            if (i < count - 1) Thread.Sleep(40);
        }
    }

    public void Scroll(int dx, int dy)
    {
        // Positive dy means "scroll down", which is a negative wheel delta in Windows.
        for (var i = 0; i < Math.Abs(dy); i++)
        {
            SendMouse(MOUSEEVENTF_WHEEL, unchecked((uint)(-Math.Sign(dy) * WheelDelta)));
            Thread.Sleep(15);
        }
        for (var i = 0; i < Math.Abs(dx); i++)
        {
            SendMouse(MOUSEEVENTF_HWHEEL, unchecked((uint)(Math.Sign(dx) * WheelDelta)));
            Thread.Sleep(15);
        }
    }

    public void TypeText(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\r':
                    continue;
                case '\n':
                    TapVk(0x0D);
                    break;
                case '\t':
                    TapVk(0x09);
                    break;
                default:
                    // Unicode injection types the exact character regardless of keyboard layout.
                    Send(Key(0, ch, KEYEVENTF_UNICODE), Key(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
                    break;
            }
            // Small pauses keep slower apps (and remote sessions) from dropping characters.
            if (++count % 16 == 0) Thread.Sleep(10);
        }
    }

    public void PressKeys(IReadOnlyList<string> keys)
    {
        var vks = keys.Select(k => ResolveKey(k) ?? throw new ArgumentException($"Unknown key '{k}'.")).ToList();
        var inputs = new List<INPUT>();
        foreach (var vk in vks)
            inputs.Add(VkInput(vk, up: false));
        for (var i = vks.Count - 1; i >= 0; i--)
            inputs.Add(VkInput(vks[i], up: true));
        Send(inputs.ToArray());
    }

    public bool IsKnownKey(string key) => ResolveKey(key) is not null;

    private static ushort? ResolveKey(string key)
    {
        key = key.Trim();
        if (NamedKeys.TryGetValue(key, out var vk))
            return vk;
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
                return c;
            // Punctuation: ask the active keyboard layout which key produces it.
            var scan = VkKeyScan((ushort)key[0]);
            if (scan != -1)
                return (ushort)(scan & 0xFF);
        }
        return null;
    }

    private static void TapVk(ushort vk) => Send(VkInput(vk, up: false), VkInput(vk, up: true));

    private static INPUT VkInput(ushort vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;
        if (ExtendedKeys.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return Key(vk, (ushort)MapVirtualKey(vk, 0), flags);
    }

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    private static (uint Down, uint Up) Flags(MouseButton button) => button switch
    {
        MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    };

    private static void SendMouse(uint flags, uint data = 0) => Send(new INPUT
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } },
    });

    private static void Send(params INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, InputSize) != inputs.Length)
            throw new InvalidOperationException(
                "Windows blocked the input. This happens when the target window runs as administrator and BlackBox does not, or on the lock screen.");
    }
}
