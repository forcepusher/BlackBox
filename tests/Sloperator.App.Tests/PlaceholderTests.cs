using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// An empty field's hint in the colour the app gives it, whatever the pointer or the focus does.
/// Avalonia 12 started drawing every hint at Fluent's half opacity, a combo box's too: the fields'
/// hints went from the faint grey to near the background.
/// </summary>
public sealed class PlaceholderTests
{
    private static string Hint(TextBlock block)
    {
        var opacity = 1.0;
        for (Visual? v = block; v != null && v != block.TemplatedParent; v = v.GetVisualParent())
            opacity *= v.Opacity;
        var color = ((ISolidColorBrush)block.Foreground!).Color;
        return $"{color.R:X2}{color.G:X2}{color.B:X2} at {opacity}";
    }

    private static TextBlock Part(Control control, string name) =>
        control.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == name);

    [Fact]
    public void A_fields_hint_is_the_faint_grey_at_rest_under_the_pointer_and_focused()
    {
        var (rest, over, focused, composer, combo) = HeadlessApp.Run(() =>
        {
            var box = new TextBox { Width = 200, PlaceholderText = "A hint" };
            var chat = new TextBox { Width = 200, PlaceholderText = "A hint", Classes = { "composer" } };
            var list = new ComboBox { Width = 200, PlaceholderText = "A hint" };
            var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { box, chat, list } } };
            window.Show();
            var hint = Part(box, "PART_Placeholder");
            var atRest = Hint(hint);
            window.MouseMove(new Point(100, 16));
            var underPointer = Hint(hint);
            box.Focus();
            var withFocus = Hint(hint);
            var result = (atRest, underPointer, withFocus, Hint(Part(chat, "PART_Placeholder")), Hint(Part(list, "PlaceholderTextBlock")));
            window.Close();
            return result;
        });
        Assert.Equal("808080 at 1", rest);
        Assert.Equal("808080 at 1", over);
        Assert.Equal("808080 at 1", focused);
        Assert.Equal("606060 at 1", composer);
        Assert.Equal("F0F0F0 at 1", combo);
    }
}
