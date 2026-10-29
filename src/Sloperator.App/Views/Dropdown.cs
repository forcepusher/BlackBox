using Avalonia.Controls;

namespace Sloperator.App.Views;

/// <summary>
/// Where every list opened by a click hangs: under its button, flush with the button's left edge.
/// The dropdowns (App.axaml), History, Servers (<c>{x:Static views:Dropdown.Placement}</c>) and
/// <see cref="MenuButton"/>'s menu all take it from here. A right-click's menu opens at the pointer instead.
/// </summary>
public static class Dropdown
{
    public const PlacementMode Placement = PlacementMode.BottomEdgeAlignedLeft;
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
