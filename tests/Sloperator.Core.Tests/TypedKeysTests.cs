#if WINDOWS
using System.Runtime.InteropServices;
using Sloperator.Platform.Windows;
using Xunit;
using static Sloperator.Platform.Windows.Native;

namespace Sloperator.Core.Tests;

/// <summary>
/// type_text presses the keys that make each character on the keyboard layout, as a person does:
/// Blender 5.2's text fields drop Unicode injection, every character of it. Only what no key makes
/// goes as Unicode. Sends nothing; the layouts are loaded, not switched to.
/// </summary>
public class TypedKeysTests
{
    [DllImport("user32.dll", EntryPoint = "LoadKeyboardLayoutW", CharSet = CharSet.Unicode)]
    private static extern nint LoadKeyboardLayout(string id, uint flags);

    private static readonly nint Us = LoadKeyboardLayout("00000409", 0), German = LoadKeyboardLayout("00000407", 0);

    /// <summary>The presses as "vk" down and "vk^" up, or "U+00E9" for a Unicode one.</summary>
    private static string Presses(char ch, nint layout, bool capsLock = false) => string.Join(" ",
        WindowsInputController.CharInputs(ch, layout, capsLock).Select(i =>
            (i.u.ki.dwFlags & KEYEVENTF_UNICODE) != 0
                ? (i.u.ki.dwFlags & KEYEVENTF_KEYUP) != 0 ? $"U+{i.u.ki.wScan:X4}^" : $"U+{i.u.ki.wScan:X4}"
                : $"{i.u.ki.wVk:X2}{((i.u.ki.dwFlags & KEYEVENTF_KEYUP) != 0 ? "^" : "")}"));

    [Fact]
    public void A_letter_is_its_key_and_a_capital_holds_shift() =>
        Assert.Equal(("41 41^", "10 41 41^ 10^"), (Presses('a', Us), Presses('A', Us)));

    [Fact]
    public void Caps_lock_on_turns_shift_round_for_letters_only() =>
        Assert.Equal(("41 41^", "10 41 41^ 10^", "10 31 31^ 10^"), (Presses('A', Us, capsLock: true), Presses('a', Us, capsLock: true), Presses('!', Us, capsLock: true)));

    [Fact]
    public void A_character_behind_altgr_holds_ctrl_and_alt() =>
        Assert.Equal("11 12 51 51^ 12^ 11^", Presses('@', German)); // AltGr+Q

    [Fact]
    public void What_no_key_makes_goes_as_unicode() =>
        Assert.Equal(("U+00E9 U+00E9^", "U+65E5 U+65E5^"), (Presses('é', Us), Presses('日', Us)));
}
#endif
