using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using BlackBox.Core.Platform;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatformServices
{
    private readonly WindowsInputController _input = new();
    private readonly WindowsInputOverlay _overlay = new();

    public WindowsPlatform()
    {
        // Coordinates must be physical pixels. The app manifest already asks for this;
        // calling it again is harmless and covers hosts without the manifest (tests, tools).
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Input = new OverlayInputController(_input, _overlay);
    }

    public string Name => $"Windows {Environment.OSVersion.Version}";
    public IScreenCapture Screen { get; } = new WindowsScreenCapture();
    public IInputController Input { get; }
    public IHostWindow HostWindow { get; } = new WindowsHostWindow();
    public IInputOverlay InputOverlay => _overlay;
    public IStopKey StopKey { get; } = new WindowsStopKey();
    public IDesktopTurn DesktopTurn { get; } = new WindowsDesktopTurn();
    public Core.Media.IMediaDecoder? Media { get; } = new MediaFoundationDecoder();
    public Core.Media.IAudioOutput? CreateAudioOutput() => new WaveOutPlayer();

    public ProcessStartInfo CreateShellStartInfo(string command)
    {
        // -EncodedCommand sidesteps every quoting problem; forcing UTF-8 output keeps
        // non-ASCII file names and text intact when we read the redirected stream. Windows
        // PowerShell's curl is an alias of Invoke-WebRequest, which rejects curl's options;
        // models mean the real curl.exe (in Windows since 10 1803), so the alias goes.
        var script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " +
                     "Remove-Item Alias:curl -Force -ErrorAction SilentlyContinue\n" + command + "\n" + ExitCode;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" } };
        if (encoded.Length <= MaxEncodedCommand)
        {
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(encoded);
            return psi;
        }
        // Windows starts nothing with a command line over 32,767 characters, which a command of
        // about 12,000 is once encoded: a long script the model wrote failed with "The filename or
        // extension is too long". Such a one runs from a file, which deletes itself once read, with
        // a BOM (Windows PowerShell reads a .ps1 without one in the ANSI code page), and ends with
        // the same exit code (see ExitCode).
        var file = Path.Combine(Path.GetTempPath(), $"blackbox-command-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue\n" + script + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(file);
        return psi;
    }

    /// <summary>What fits on a command line with powershell.exe and its other arguments.</summary>
    private const int MaxEncodedCommand = 32_000;

    /// <summary>
    /// The exit code after the command, when its last statement failed: a program's own (git's 128,
    /// not just 1), or 0 when all that failed was a program's messages on stderr taken in with 2>&1,
    /// which Windows PowerShell 5.1 counts as errors although the program succeeded: every ffmpeg
    /// call (it writes its progress there) came back as "Exit code 1" and counted as a failure.
    /// </summary>
    private const string ExitCode =
        "if (-not $?) { if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; " +
        "if ($Error.Count -gt 0 -and \"$($Error[0].FullyQualifiedErrorId)\" -like 'NativeCommandError*') { exit 0 }; exit 1 }";
}
