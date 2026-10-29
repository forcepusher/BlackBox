using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>A click away from a text box takes the focus off it, in any window: on empty space, a panel, a label.</summary>
public sealed class TextBoxFocusTests
{
    private sealed record Shown(Window Window, TextBox Box, TextBlock Label, Button Button);

    private static Shown Show()
    {
        var box = new TextBox { Width = 200, Height = 32 };
        var label = new TextBlock { Text = "A label", Height = 32 };
        var button = new Button { Content = "A button", Height = 32 };
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = new StackPanel
            {
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Stretch,
                Children = { box, label, button },
            },
        };
        window.Show();
        return new Shown(window, box, label, button);
    }

    private static object? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    private static void Click(Window window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
    }

    [Fact]
    public void A_click_on_empty_space_or_a_label_takes_the_focus_off_the_box()
    {
        var (afterSpace, afterLabel) = HeadlessApp.Run(() =>
        {
            var shown = Show();
            shown.Box.Focus();
            Click(shown.Window, new Point(300, 250));
            var space = Focused(shown.Window);
            shown.Box.Focus();
            Click(shown.Window, new Point(20, 48));
            var label = Focused(shown.Window);
            shown.Window.Close();
            return (space, label);
        });
        Assert.Null(afterSpace);
        Assert.Null(afterLabel);
    }

    [Fact]
    public void A_click_in_the_box_keeps_it_and_one_on_a_button_focuses_the_button()
    {
        var (inBox, onButton, button) = HeadlessApp.Run(() =>
        {
            var shown = Show();
            shown.Box.Focus();
            Click(shown.Window, new Point(100, 16));
            var box = Focused(shown.Window) == shown.Box;
            Click(shown.Window, new Point(20, 80));
            var focused = Focused(shown.Window);
            shown.Window.Close();
            return (box, focused, shown.Button);
        });
        Assert.True(inBox);
        Assert.Same(button, onButton);
    }
}
