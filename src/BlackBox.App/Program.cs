using Avalonia;

namespace BlackBox.App;

internal static class Program
{
    /// <summary>Set once the window is up: a failure before that kept BlackBox from starting.</summary>
    internal static bool Started { get; set; }

    [STAThread]
    public static void Main(string[] args)
    {
        var paths = PortableRuntime.FindPaths();
        // BlackBox runs from this folder already: that one opens another window, and this one is done.
        if (SingleInstance.HandedOff(paths.DataDir))
            return;
        CrashLog.Watch(paths);
        try
        {
            // Must run before Avalonia touches Skia.
            PortableRuntime.Prepare(paths);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
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
