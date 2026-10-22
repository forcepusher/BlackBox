using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BlackBox.App.ViewModels;
using BlackBox.App.Views;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;

namespace BlackBox.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = PortableRuntime.FindPaths();
            paths.EnsureCreated();
            var store = new SettingsStore(paths);
            var platform = CreatePlatform();

            var settings = store.Load();
            var vm = new MainWindowViewModel(paths, store, settings, platform);
            var window = new MainWindow { DataContext = vm };
            window.Restore(settings.Window, WindowSlot.Take());
            desktop.MainWindow = window;
            // Stops the MCP server processes with the app; otherwise they would outlive it.
            desktop.Exit += (_, _) => vm.Shutdown();
            // An exception nothing caught on the window's thread is written down and said in the
            // chat, and the window goes on, with the run in progress: it closed with them before.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                CrashLog.Write("the window", e.Exception);
                e.Handled = true;
                vm.ShowProblem($"Something went wrong in BlackBox's window: {CrashLog.Summary(e.Exception)}");
            };
            vm.OnStartup();
            if (CrashLog.TakeLastCrash(paths) is { } crash)
                vm.ShowProblem($"BlackBox closed unexpectedly last time ({crash}).");
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
