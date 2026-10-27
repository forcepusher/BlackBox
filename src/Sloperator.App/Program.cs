using Avalonia;

namespace Sloperator.App;

internal static class Program
{
    /// <summary>Set once the window is up: a failure before that kept Sloperator from starting.</summary>
    internal static bool Started { get; set; }

    [STAThread]
    public static void Main(string[] args)
    {
        var paths = PortableRuntime.FindPaths();
        // Sloperator runs from this folder already: that one opens another window, and this one is done.
        if (SingleInstance.HandedOff(paths.DataDir))
            return;
        CrashLog.Watch(paths);
        Core.ErrorLog.Start(paths);
        try
        {
            // Must run before Avalonia touches Skia.
            PortableRuntime.Prepare(paths);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            SingleInstance.Ending();
            if (Started) CrashLog.Ended(ex);
            else CrashLog.ShowCouldNotStart(ex);
        }
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Classic redirection surface instead of DirectComposition: windows excluded from
            // screen capture (WDA_EXCLUDEFROMCAPTURE) render reliably with it, while partial
            // DirectComposition updates could leave regions unpainted.
            .With(new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.RedirectionSurface] })
            .WithInterFont()
            .LogToTrace();
}
