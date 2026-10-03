using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using BlackBox.Core.Platform;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatformServices
{
    private readonly WindowsInputController _input = new();
    private readonly WindowsWindowManager _windows = new();
    private readonly WindowsInputOverlay _overlay = new();
    private readonly Lazy<WindowsUiAutomation> _automation;

    public WindowsPlatform()
    {
        // Coordinates must be physical pixels. The app manifest already asks for this;
        // calling it again is harmless and covers hosts without the manifest (tests, tools).
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _automation = new(() => new WindowsUiAutomation(_windows, _input));
        Input = new OverlayInputController(_input, _overlay);
    }

    public string Name => $"Windows {Environment.OSVersion.Version}";
    public IScreenCapture Screen { get; } = new WindowsScreenCapture();
    public IInputController Input { get; }
    public IWindowManager Windows => _windows;
    public IUiAutomation? Automation => _automation.Value;
    public IHostWindow HostWindow { get; } = new WindowsHostWindow();
    public IInputOverlay InputOverlay => _overlay;

    public ProcessStartInfo CreateShellStartInfo(string command)
    {
        // -EncodedCommand sidesteps every quoting problem; forcing UTF-8 output keeps
        // non-ASCII file names and text intact when we read the redirected stream.
        var script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'\n" + command;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded },
        };
    }
}
