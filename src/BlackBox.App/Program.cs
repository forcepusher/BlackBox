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
            .WithInterFont()
            .LogToTrace();
}
