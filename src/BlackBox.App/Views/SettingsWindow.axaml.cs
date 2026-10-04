using Avalonia.Controls;
using Avalonia.Interactivity;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // A broken MCP server list is not saved: the error is shown under the box instead.
        if (DataContext is SettingsViewModel { HasMcpError: true })
        {
            Tabs.SelectedIndex = 1;
            return;
        }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
