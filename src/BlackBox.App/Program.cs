using Avalonia;

namespace BlackBox.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before Avalonia touches Skia.
        PortableRuntime.Prepare(PortableRuntime.FindAppRoot());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
