using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BlackBox.Core.Platform;

namespace BlackBox.App;

public partial class App : Application
{
    public override void Initialize()
    {
        // Every scrollbar is the full one, arrows and thumb, never the thin line that grows under the
        // pointer (the user, 2026-10-26). A default, not a style: the theme's ScrollViewers take it from
        // their owner (a ListBox, a TextBox) by template binding, which a style setter loses to.
        // Each ScrollViewer hands it on to its bars.
        ScrollViewer.AllowAutoHideProperty.OverrideDefaultValue<TemplatedControl>(false);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = PortableRuntime.FindPaths();
            paths.EnsureCreated();
            // Ends with the last chat window, not with whichever window was opened first.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var windows = new ChatWindows(paths, CreatePlatform(), desktop);
            var first = windows.Open();
            desktop.MainWindow = first;
            // Stops the MCP server processes with the app; otherwise they would outlive it.
            desktop.Exit += (_, _) => windows.Exit();
            // An exception nothing caught on the windows' thread is written down and said in the
            // chat in front, and the windows go on, with their runs: they closed with them before.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                CrashLog.Write("the window", e.Exception);
                e.Handled = true;
                windows.Front()?.Vm.ShowProblem($"Something went wrong in BlackBox's window: {CrashLog.Summary(e.Exception)}", e.Exception);
            };
            if (CrashLog.TakeLastCrash(paths) is { } crash)
                windows.Front()?.Vm.ShowProblem($"BlackBox closed unexpectedly last time ({crash}).");
            // BlackBox started again: another window here.
            SingleInstance.OnOpenWindow(() => Dispatcher.UIThread.Post(() => windows.Open()));
            Program.Started = true;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IPlatformServices CreatePlatform()
    {
#if WINDOWS
        return new Platform.Windows.WindowsPlatform();
#else
        // Linux and macOS backends go here.
        return new UnsupportedPlatform();
#endif
    }
}
