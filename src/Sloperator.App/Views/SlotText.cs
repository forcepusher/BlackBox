using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sloperator.App.Views;

/// <summary>
/// A text in a slot as wide as the widest of the texts it can show (<see cref="Slot"/>, unseen under it),
/// so a bar's button that shows it keeps its size, and the buttons beside it their place, as the text
/// changes: the zoom between − and +, the Code editor's language, encoding and line breaks (the user,
/// 2026-10-31: nothing in a panel's bar moves from one file to the next). A text wider than all of them
/// still widens it.
/// </summary>
public sealed class SlotText : Panel
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<SlotText, string?>(nameof(Text));

    public static readonly StyledProperty<IReadOnlyList<string>?> SlotProperty = AvaloniaProperty.Register<SlotText, IReadOnlyList<string>?>(nameof(Slot));

    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty = AvaloniaProperty.Register<SlotText, TextAlignment>(nameof(TextAlignment));

    private readonly TextBlock _shown = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

    public SlotText() => Children.Add(_shown);

    /// <summary>The text shown.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Every text it can show: the slot is as wide as the widest.</summary>
    public IReadOnlyList<string>? Slot
    {
        get => GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            _shown.Text = Text;
        else if (change.Property == TextAlignmentProperty)
            _shown.TextAlignment = TextAlignment;
        else if (change.Property == SlotProperty)
        {
            Children.RemoveAll(Children.Where(child => child != _shown).ToList());
            foreach (var text in Slot ?? [])
            {
                var sizer = new TextBlock { Text = text, Opacity = 0, IsHitTestVisible = false };
                AutomationProperties.SetAccessibilityView(sizer, AccessibilityView.Raw);
                Children.Insert(0, sizer);
            }
        }
    }
}
