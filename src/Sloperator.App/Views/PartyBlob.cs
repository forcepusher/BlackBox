using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>
/// The blob beside Send and Stop: the logo, still, until a prompt runs; then the party blob of the
/// blobs.gg set (Assets/partyblob.gif, github.com/Raymo111/emoji, Apache License 2.0; see
/// docs/logo/LICENSE-blobs.txt), dancing until the run ends.
/// </summary>
public sealed class PartyBlob : Control
{
    public static readonly StyledProperty<bool> DancingProperty = AvaloniaProperty.Register<PartyBlob, bool>(nameof(Dancing));

    public bool Dancing { get => GetValue(DancingProperty); set => SetValue(DancingProperty, value); }

    /// <summary>The logo, and the dance's frames with how long each shows; read once, for every window.</summary>
    private static Bitmap? _logo;
    private static List<(Bitmap Frame, int Ms)>? _dance;

    private DispatcherTimer? _timer;
    private int _at;

    static PartyBlob()
    {
        AffectsRender<PartyBlob>(DancingProperty);
    }

    public PartyBlob()
    {
        Avalonia.Automation.AutomationProperties.SetName(this, "Sloperator is idle");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DancingProperty)
            Avalonia.Automation.AutomationProperties.SetName(this, Dancing ? "Sloperator is working" : "Sloperator is idle");
        if (change.Property == DancingProperty || change.Property == IsVisibleProperty)
            Play();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Play();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Play();
    }

    /// <summary>Dances while asked to and on screen; otherwise the timer stops, so a still blob costs nothing.</summary>
    private void Play()
    {
        var dancing = Dancing && IsVisible && VisualRoot is not null;
        if (!dancing)
        {
            _timer?.Stop();
            _at = 0;
            return;
        }
        var dance = _dance ??= Load();
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Tick += (_, _) => Advance();
        }
        if (_timer.IsEnabled)
            return;
        _timer.Interval = TimeSpan.FromMilliseconds(dance[_at].Ms);
        _timer.Start();
        InvalidateVisual();
    }

    private void Advance()
    {
        var dance = _dance!;
        _at = (_at + 1) % dance.Count;
        _timer!.Interval = TimeSpan.FromMilliseconds(dance[_at].Ms);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var picture = Dancing && _dance is { } dance ? dance[_at].Frame : _logo ??= new Bitmap(AssetLoader.Open(new Uri("avares://Sloperator/Assets/sloperator.png")));
        var side = Math.Min(Bounds.Width, Bounds.Height);
        var at = new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            context.DrawImage(picture, new Rect(picture.Size), at);
    }

    /// <summary>Every frame of the GIF, whole (a frame may draw only what changed since the one before).</summary>
    private static List<(Bitmap, int)> Load()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Sloperator/Assets/partyblob.gif"));
        using var codec = SKCodec.Create(stream);
        var info = codec.FrameInfo;
        using var full = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var frames = new List<(Bitmap, int)>();
        for (var i = 0; i < Math.Max(1, codec.FrameCount); i++)
        {
            codec.GetPixels(full.Info, full.GetPixels(), new SKCodecOptions(i, codec.FrameCount > 1 ? info[i].RequiredFrame : -1));
            frames.Add((FilePreview.ToBitmap(full), codec.FrameCount > 1 ? Math.Max(info[i].Duration, 20) : 100));
        }
        return frames;
    }
}
