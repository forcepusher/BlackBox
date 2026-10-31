using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Interactivity;

namespace Sloperator.App.Views;

/// <summary>
/// The title bar on top of a window, in the app's colours: Windows 10 draws its own grey whatever
/// the window asks. Windows is told the window has no caption (its frame stays on the sides and at
/// the bottom) and that the strip is its caption, so it drags, snaps, maximizes on a double-click
/// and opens the system menu on a right-click as a native one; the maximize button is Windows' too
/// (HTMAXBUTTON), so Windows 11 shows its snap layouts over it. Elsewhere (no Windows) it is hidden
/// and the system's own title bar stays.
/// </summary>
public partial class TitleBar : UserControl
{
    /// <summary>
    /// How tall the bar is, its strip (App.axaml, TitleBarHeight) and the line under it: what a window
    /// gets over the content it had under Windows' bar, and what a height saved then is short of.
    /// </summary>
    public const double Room = 33;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TitleBar, string?>(nameof(Text));

    public static readonly StyledProperty<Control?> StatusProperty =
        AvaloniaProperty.Register<TitleBar, Control?>(nameof(Status));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<TitleBar, string?>(nameof(Subtitle));

    private Window? _window;

    public TitleBar()
    {
        InitializeComponent();
        IsVisible = false;
    }

    /// <summary>The bar's title when it isn't the window's (whose title the taskbar shows).</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// What sits right after the title: the chat window's server status. It takes the
    /// pointer as the buttons do (not the caption), so its tooltips show.
    /// </summary>
    public Control? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>Last, after the status, in the title's look: the chat's title.</summary>
    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StatusProperty)
            StatusHost.Content = change.NewValue;
        else if (change.Property == SubtitleProperty)
            SubtitleText.Text = change.NewValue as string;
        else if (change.Property == TextProperty && _window is { } window)
            ShowTitle(window);
    }

    /// <summary>The bar's own title, else the window's, followed as it changes.</summary>
    private void ShowTitle(Window window)
    {
        if (Text is { } text)
            TitleText.Text = text;
        else
            TitleText[!TextBlock.TextProperty] = window[!Window.TitleProperty];
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is not Window window || window == _window)
            return;
        _window = window;
        ShowTitle(window);
        window.PropertyChanged += OnWindowChanged;
        Show(window);
#if WINDOWS
        if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != 0)
        {
            Win32Properties.AddWndProcHookCallback(window, WndProc);
            ToolTip.SetPlacement(MaximizeButton, PlacementMode.Custom);
            ToolTip.SetCustomPopupPlacementCallback(MaximizeButton, PlaceTip);
            Platform.Windows.WindowsPlatform.RefreshFrame(hwnd);
            // Windows 11 draws its line round the whole window, the top too.
            Classes.Set("frameLine", Environment.OSVersion.Version.Build < 22000);
            IsVisible = true;
        }
#endif
    }

    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowBase.IsActiveProperty || e.Property == Window.WindowStateProperty)
            Show(_window!);
    }

    /// <summary>Dim while another window is active, as Windows' own bars are; the restore sign while maximized.</summary>
    private void Show(Window window)
    {
        Classes.Set("inactive", !window.IsActive);
        Classes.Set("maximized", window.WindowState == WindowState.Maximized);
        ToolTip.SetTip(MaximizeButton, window.WindowState == WindowState.Maximized ? "Restore Down" : "Maximize");
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => _window!.WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnClose(object? sender, RoutedEventArgs e) => _window!.Close();

    private void ToggleMaximized() =>
        _window!.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

#if WINDOWS
    private const uint WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1,
        WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3, WM_NCRBUTTONDOWN = 0x00A4, WM_NCRBUTTONUP = 0x00A5, WM_SYSCOMMAND = 0x0112, WM_NCMOUSELEAVE = 0x02A2;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTMAXBUTTON = 9, SC_KEYMENU = 0xF100;

    /// <summary>Pressed on the maximize button and not yet let go.</summary>
    private bool _pressed;

    /// <summary>
    /// The frame without a caption, and what each point of the strip is: a button, or the caption.
    /// Over the maximize button the window gets no pointer events, only non-client ones, so its look
    /// under the pointer and pressed and its click are done here.
    /// </summary>
    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE when wParam != 0:
                Platform.Windows.WindowsPlatform.CaptionlessFrame(hwnd, wParam, lParam);
                handled = true;
                return 0;
            case WM_NCHITTEST:
                handled = true;
                var hit = Platform.Windows.WindowsPlatform.FrameHitTest(hwnd, lParam);
                return hit != HTCLIENT ? hit : Over(MaximizeButton, lParam) ? HTMAXBUTTON
                    : Over(this, lParam) && !Over(MinimizeButton, lParam) && !Over(CloseButton, lParam) && !Over(StatusHost, lParam) ? HTCAPTION : HTCLIENT;
            case WM_NCMOUSEMOVE:
                _pointer = new PixelPoint((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                Hover(wParam == HTMAXBUTTON);
                if (wParam == HTMAXBUTTON)
                    Platform.Windows.WindowsPlatform.TrackNonClientLeave(hwnd);
                break;
            case WM_NCMOUSELEAVE:
                Hover(false);
                break;
            case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when wParam == HTMAXBUTTON:
                Press(true);
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                var click = _pressed;
                Press(false);
                if (click)
                    ToggleMaximized();
                handled = true;
                break;
            // The system menu where the pointer is, or under the bar's left end for Alt+Space. Windows'
            // own handling of the press keeps the letting go to itself.
            case WM_NCRBUTTONDOWN when wParam == HTCAPTION:
                handled = true;
                break;
            case WM_NCRBUTTONUP when wParam == HTCAPTION:
                Platform.Windows.WindowsPlatform.ShowSystemMenu(hwnd, (short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                handled = true;
                break;
            case WM_SYSCOMMAND when (wParam & 0xFFF0) == SC_KEYMENU && lParam == ' ':
                var corner = this.PointToScreen(new Point(0, this.FindResource("TitleBarHeight") is double strip ? strip : Bounds.Height));
                Platform.Windows.WindowsPlatform.ShowSystemMenu(hwnd, corner.X, corner.Y);
                handled = true;
                break;
        }
        return 0;
    }

    /// <summary>
    /// Whether the screen point in <paramref name="lParam"/> (pixels, each half signed: a screen left
    /// of the first is negative) is on <paramref name="control"/>; on the bar, its strip (without the line under it).
    /// </summary>
    private bool Over(Control control, nint lParam)
    {
        var at = control.PointToClient(new PixelPoint((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)));
        var height = control == this && this.FindResource("TitleBarHeight") is double strip ? strip : control.Bounds.Height;
        return control.IsEffectivelyVisible && new Rect(0, 0, control.Bounds.Width, height).Contains(at);
    }

    /// <summary>Where the pointer last was over the window's frame or maximize, in screen pixels.</summary>
    private PixelPoint _pointer;

    /// <summary>
    /// Maximize's tooltip at the pointer, as every other tooltip is (<see cref="PlacementMode.Pointer"/>):
    /// Avalonia takes the pointer from the window's pointer events, which there are none of over maximize.
    /// </summary>
    private void PlaceTip(CustomPopupPlacement placement)
    {
        placement.AnchorRectangle = new Rect(_window!.PointToClient(_pointer), new Size(1, 1));
        placement.Anchor = PopupAnchor.TopLeft;
        placement.Gravity = PopupGravity.BottomRight;
    }

    /// <summary>Waits the tooltip's delay once the pointer is on maximize, then shows its tooltip.</summary>
    private Avalonia.Threading.DispatcherTimer? _tip;

    private void Hover(bool over)
    {
        if (over == MaximizeButton.Classes.Contains("hover"))
            return;
        MaximizeButton.Classes.Set("hover", over);
        _tip?.Stop();
        if (over)
        {
            if (_tip is null)
            {
                _tip = new Avalonia.Threading.DispatcherTimer();
                _tip.Tick += ShowTip;
            }
            _tip.Interval = TimeSpan.FromMilliseconds(ToolTip.GetShowDelay(MaximizeButton));
            _tip.Start();
        }
        else
        {
            ToolTip.SetIsOpen(MaximizeButton, false);
            Press(false);
        }
    }

    private void ShowTip(object? sender, EventArgs e)
    {
        _tip!.Stop();
        if (MaximizeButton.Classes.Contains("hover") && !_pressed)
            ToolTip.SetIsOpen(MaximizeButton, true);
    }

    private void Press(bool pressed)
    {
        _pressed = pressed;
        MaximizeButton.Classes.Set("pressed", pressed);
        if (pressed)
        {
            _tip?.Stop();
            ToolTip.SetIsOpen(MaximizeButton, false);
        }
    }
#endif
}
