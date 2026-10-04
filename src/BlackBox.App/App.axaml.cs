using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

            var vm = new MainWindowViewModel(paths, store, store.Load(), platform);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // Stops the MCP server processes with the app; otherwise they would outlive it.
            desktop.Exit += (_, _) => vm.Shutdown();
            vm.OnStartup();
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
