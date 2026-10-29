using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.Core.Platform;

namespace Sloperator.App;

public partial class App : Application
{
    public override void Initialize()
    {
        // Every scrollbar is the full one, arrows and thumb, never the thin line that grows under the
        // pointer (the user, 2026-10-26). A default, not a style: the theme's ScrollViewers take it from
        // their owner (a ListBox, a TextBox) by template binding, which a style setter loses to.
        // Each ScrollViewer hands it on to its bars.
        ScrollViewer.AllowAutoHideProperty.OverrideDefaultValue<TemplatedControl>(false);
        // A click away from the text box being typed in takes the focus off it, in every window and
        // for every box (the user, 2026-10-29). A button, a list or a dropdown takes the focus by
        // itself; a panel, a label or empty space takes nothing, and the box kept it, caret and all.
        // Bubbling, handled or not: by then whatever the click focuses has been focused.
        InputElement.PointerPressedEvent.AddClassHandler<Window>(LeaveTextBox, RoutingStrategies.Bubble, handledEventsToo: true);
        AvaloniaXamlLoader.Load(this);
    }

    private static void LeaveTextBox(Window window, PointerPressedEventArgs e)
    {
        if (window.FocusManager?.GetFocusedElement() is TextBox box
            && !(e.Source is Visual source && (source == box || box.IsVisualAncestorOf(source))))
            window.FocusManager.ClearFocus();
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
                windows.Front()?.Vm.ShowProblem($"Something went wrong in Sloperator's window: {CrashLog.Summary(e.Exception)}", e.Exception);
            };
            if (CrashLog.TakeLastCrash(paths) is { } crash)
                windows.Front()?.Vm.ShowProblem($"Sloperator closed unexpectedly last time ({crash}).");
            // Sloperator started again: another window here.
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
