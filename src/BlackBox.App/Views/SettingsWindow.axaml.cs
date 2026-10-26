using Avalonia.Controls;
using Avalonia.Interactivity;
using BlackBox.App.ViewModels;
using BlackBox.Core.Settings;

namespace BlackBox.App.Views;

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

    /// <summary>A screenshot size field left empty shows the default again, which is what it saves.</summary>
    private void OnSizeLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is NumericUpDown { Value: null } box)
            box.Value = box == ScreenshotWidthBox ? AgentSettings.DefaultScreenshotWidth : AgentSettings.DefaultScreenshotHeight;
    }
}
