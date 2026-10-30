using Avalonia.Controls;
using Avalonia.Interactivity;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // A broken MCP server list is not saved: the error is shown under the box instead.
        if (DataContext is SettingsViewModel { HasMcpError: true })
        {
            McpBox.BringIntoView();
            McpBox.Focus();
            return;
        }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
