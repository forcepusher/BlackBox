using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace BlackBox.App.Views;

/// <summary>
/// A window of Windows' own in the Inspect file panel, in which the previewer Windows has for a
/// file draws it (Word, Excel, PowerPoint, the PDF reader's, Windows' for HTML), as Explorer's preview
/// pane does (<see cref="Platform.Windows.ShellPreview"/>). Elsewhere than on Windows it shows nothing.
/// </summary>
public sealed class ShellPreviewHost : NativeControlHost
{
    public static readonly StyledProperty<string?> PathProperty = AvaloniaProperty.Register<ShellPreviewHost, string?>(nameof(Path));

    /// <summary>The file shown; null for none.</summary>
    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    /// <summary>The previewer couldn't show the file: why.</summary>
    public event Action<string>? Failed;

    /// <summary>Whether Windows has a previewer for files like <paramref name="path"/>.</summary>
    public static bool CanShow(string path)
    {
#if WINDOWS
        return OperatingSystem.IsWindows() && Platform.Windows.ShellPreview.HandlerFor(path) is not null;
#else
        return false;
#endif
    }

#if WINDOWS
    private nint _hwnd;
    private Platform.Windows.ShellPreview? _preview;
    private int _opening;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _hwnd = handle.Handle;
        Reopen();
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Close();
        _hwnd = 0;
        base.DestroyNativeControlCore(control);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PathProperty)
            Reopen();
        else if (change.Property == BoundsProperty)
            _preview?.Resize(Pixels(Bounds.Width), Pixels(Bounds.Height));
    }

    private int Pixels(double dips) => Math.Max(1, (int)Math.Round(dips * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1)));

    /// <summary>
    /// The file in the window, in place of the one before. After the panel has been drawn: the
    /// previewer starts in Windows' preview host and loads the file meanwhile, which can take a second.
    /// </summary>
    private void Reopen()
    {
        Close();
        if (Path is not { } path || _hwnd == 0 || !OperatingSystem.IsWindows())
            return;
        var opening = ++_opening;
        Dispatcher.UIThread.Post(() =>
        {
            if (opening != _opening || _hwnd == 0 || Path != path)
                return;
            try
            {
                _preview = Platform.Windows.ShellPreview.Open(path, _hwnd, Pixels(Bounds.Width), Pixels(Bounds.Height));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Failed?.Invoke(ex.Message);
            }
        }, DispatcherPriority.Background);
    }

    private void Close()
    {
        _opening++;
        var preview = _preview;
        _preview = null;
        preview?.Dispose();
    }
#endif
}
