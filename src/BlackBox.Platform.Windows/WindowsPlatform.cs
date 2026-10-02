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
    private readonly Lazy<nint> _job = new(CreateKillOnCloseJob);

    public WindowsPlatform()
    {
        // Coordinates must be physical pixels. The app manifest already asks for this;
        // calling it again is harmless and covers hosts without the manifest (tests, tools).
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _automation = new Lazy<WindowsUiAutomation>(() => new WindowsUiAutomation(_windows, _input));
    }

    private readonly WindowsInputController _input = new();
    private readonly WindowsWindowManager _windows = new();
    private readonly Lazy<WindowsUiAutomation> _automation;

    public string Name => $"Windows {Environment.OSVersion.Version}";
    public IScreenCapture Screen { get; } = new WindowsScreenCapture();
    public IInputController Input => _input;
    public IWindowManager Windows => _windows;
    public IUiAutomation? Automation => _automation.Value;
    public IHostWindow HostWindow { get; } = new WindowsHostWindow();

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

    /// <summary>
    /// Puts the process in a job that Windows kills when BlackBox exits, even on a crash,
    /// so a multi-gigabyte llama-server never lingers in VRAM.
    /// </summary>
    public void TieToAppLifetime(Process process)
    {
        if (_job.Value != 0)
            AssignProcessToJobObject(_job.Value, process.Handle);
    }

    private static nint CreateKillOnCloseJob()
    {
        var job = CreateJobObject(0, 0);
        if (job == 0) return 0;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        return SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()) ? job : 0;
    }
}
