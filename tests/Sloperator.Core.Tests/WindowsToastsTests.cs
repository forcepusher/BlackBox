#if WINDOWS
using Sloperator.Platform.Windows;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in (set BB_TOAST_TESTS=1: a notification shows on the screen for a moment): a real Windows
/// notification through WinRT's vtables (ComAbi), shown, taken down, and its app id forgotten.
/// </summary>
public class WindowsToastsTests
{
    [Fact]
    public void A_toast_is_shown_and_taken_down()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("BB_TOAST_TESTS") is null, "Set BB_TOAST_TESTS=1 to run (shows a notification).");
        // A Sloperator of the user's with notifications on keeps its app id: only one made here is forgotten.
        var name = @"Software\Classes\AppUserModelId\" + WindowsToasts.AppId;
        bool Registered() { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(name); return key is not null; }
        var before = Registered();
        var toasts = new WindowsToasts();
        Assert.True(toasts.Show("test", "Sloperator test", "Taken down at once.", null, () => { }));
        Assert.True(Registered());
        toasts.Hide("test");
        if (before)
            return;
        toasts.Forget();
        Assert.False(Registered());
    }
}
#endif
