using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // A broken MCP server list is not saved: the error is shown under the box instead, on
        // Advanced, once that page is laid out.
        if (DataContext is SettingsViewModel { HasMcpError: true })
        {
            Tabs.SelectedIndex = 1;
            Dispatcher.UIThread.Post(() =>
            {
                McpBox.BringIntoView();
                McpBox.Focus();
            }, DispatcherPriority.Loaded);
            return;
        }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
