using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// A sound line's volume in the Video generator moves in steps of 5%: dragged, it landed on 111%
/// (the user, 2026-10-30).
/// </summary>
public sealed class VolumeSliderTests
{
    [Fact]
    public void The_volume_snaps_to_5_percent() => HeadlessApp.Run(() =>
    {
        // The panel's own styles, on a slider as the timeline has one.
        var slider = new Slider { Minimum = 0, Maximum = 2, Value = 1.11, SmallChange = 0.05, LargeChange = 0.25 };
        slider.Classes.Add("volume");
        var panel = new VideoGeneratorPanel { Content = slider };
        var window = new Window { Width = 400, Height = 200, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(slider.IsSnapToTickEnabled);
        Assert.Equal(0.05, slider.TickFrequency);

        slider.Focus();
        slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        Assert.Equal(115, Math.Round(slider.Value * 100, 6));
        slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
        Assert.Equal(110, Math.Round(slider.Value * 100, 6));
        window.Close();
        return 0;
    });
}
