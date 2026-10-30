using Avalonia.Controls;

namespace Sloperator.App.Views;

/// <summary>
/// Where every list opened by a click hangs: under its button, flush with the button's left edge.
/// The dropdowns (App.axaml) and <see cref="MenuButton"/>'s menu take it from here. History and
/// Servers (<c>{x:Static views:Dropdown.FromRight}</c>) hang flush with their button's right edge
/// instead, growing leftwards. A right-click's menu opens at the pointer.
/// </summary>
public static class Dropdown
{
    public const PlacementMode Placement = PlacementMode.BottomEdgeAlignedLeft;

    /// <summary>Under the button, the list's right edge on the button's right edge.</summary>
    public const PlacementMode FromRight = PlacementMode.BottomEdgeAlignedRight;
}

/// <summary>
/// A button whose menu hangs from it as a dropdown's list does, made afresh each time it opens; a
/// click while it is open closes it. A menu filled once it was shown showed empty, so each opening
/// gets a menu of its own.
/// </summary>
public sealed class MenuButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The menu's entries, asked for each time it opens.</summary>
    public Func<IEnumerable<Control>>? Entries { get; set; }

    protected override void OpenFlyout()
    {
        if (Entries is { } entries)
        {
            var menu = new MenuFlyout { Placement = Dropdown.Placement };
            foreach (var entry in entries())
                menu.Items.Add(entry);
            Flyout = menu;
        }
        base.OpenFlyout();
    }
}
