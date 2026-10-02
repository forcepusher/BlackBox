using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BlackBox.App.ViewModels;
using BlackBox.App.Views;
using BlackBox.Core.Llm;
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
            var paths = new AppPaths(FindAppRoot());
            paths.EnsureCreated();
            var store = new SettingsStore(paths);
            var platform = CreatePlatform();
            var server = new LlamaServerHost(paths, platform);

            var vm = new MainWindowViewModel(paths, store, store.Load(), platform, server);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => server.StopAsync().GetAwaiter().GetResult();
            vm.OnStartup();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IPlatformServices CreatePlatform()
    {
        if (OperatingSystem.IsWindows())
            return new Platform.Windows.WindowsPlatform();
        return new UnsupportedPlatform();
    }

    /// <summary>
    /// The portable root is the exe folder. A BLACKBOX_HOME variable overrides it, and debug builds
    /// use the repository root so Models/, llama/ and Data/ are shared between builds.
    /// </summary>
    private static string FindAppRoot()
    {
        if (Environment.GetEnvironmentVariable("BLACKBOX_HOME") is { Length: > 0 } home)
            return home;
#if DEBUG
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("BlackBox.sln*").Any())
                return dir.FullName;
        }
#endif
        return AppContext.BaseDirectory;
    }
}
