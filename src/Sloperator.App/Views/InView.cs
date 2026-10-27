using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>
/// Tells a video preview in the chat whether it is on screen (<c>InView.Video="True"</c> on its
/// template): it plays by itself there, without its sound, and pauses when it is scrolled away, the
/// window is minimized or the full-screen viewer is over the chat (see <see cref="Hold"/>).
/// </summary>
public static class InView
{
    public static readonly AttachedProperty<bool> VideoProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Video", typeof(InView));

    public static void SetVideo(Control control, bool value) => control.SetValue(VideoProperty, value);
    public static bool GetVideo(Control control) => control.GetValue(VideoProperty);

    /// <summary>The part of each watched preview the chat shows, in the preview's own coordinates.</summary>
    private static readonly Dictionary<Control, Rect> Watched = [];

    /// <summary>The windows whose videos hold still: each chat window's own, by its viewer and whether it is minimized.</summary>
    private static readonly HashSet<TopLevel> Held = [];

    static InView()
    {
        VideoProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
                Watch(control);
        });
    }

    private static void Watch(Control control)
    {
        control.AttachedToVisualTree += (_, _) =>
        {
            Watched.TryAdd(control, default);
            Preview(control)?.PlayInView();
        };
        control.DetachedFromVisualTree += (_, _) =>
        {
            Watched.Remove(control);
            Preview(control)?.SetInView(false);
        };
        control.EffectiveViewportChanged += (_, e) =>
        {
            if (!Watched.ContainsKey(control))
                return;
            Watched[control] = e.EffectiveViewport;
            Update(control);
        };
        control.DataContextChanged += (_, _) =>
        {
            Preview(control)?.PlayInView();
            Update(control);
        };
        if (control.IsAttachedToVisualTree())
            Watched.TryAdd(control, default);
    }

    private static VideoPreview? Preview(Control control) => control.DataContext as VideoPreview;

    /// <summary>
    /// On screen: a good part of it shows (a third of its height, or 120 pixels of a tall one), so a
    /// video half scrolled past doesn't start and stop at the edge.
    /// </summary>
    private static void Update(Control control)
    {
        if (Preview(control) is not { } preview || !Watched.TryGetValue(control, out var viewport))
            return;
        var shown = viewport.Intersect(new Rect(control.Bounds.Size));
        var visible = !(TopLevel.GetTopLevel(control) is { } window && Held.Contains(window)) && control.IsEffectivelyVisible && shown.Width > 0 && shown.Height >= Math.Min(120, control.Bounds.Height / 3);
        preview.SetInView(visible);
    }

    /// <summary>
    /// Holds the videos in <paramref name="window"/> still (its full-screen viewer is open, it is
    /// minimized or closed), or lets those on screen go on. Not every window's: minimizing one
    /// stopped the videos in another on screen.
    /// </summary>
    public static void Hold(TopLevel window, bool held)
    {
        if (!(held ? Held.Add(window) : Held.Remove(window)))
            return;
        foreach (var control in Watched.Keys.Where(c => TopLevel.GetTopLevel(c) == window).ToList())
            Update(control);
    }
}
