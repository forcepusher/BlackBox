using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Sloperator.Core.Platform;
using static Sloperator.Platform.Windows.Native;

namespace Sloperator.Platform.Windows;

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
    public Core.Media.IMediaDecoder? Media { get; } = new MediaFoundationDecoder();
    public Core.Media.IAudioOutput? CreateAudioOutput() => new WaveOutPlayer();

    /// <summary>
    /// Lets <paramref name="processId"/> bring its window to the front: called by the program the
    /// user just started, which has the foreground, for the one it hands over to.
    /// </summary>
    public static void AllowToFront(int processId) => AllowSetForegroundWindow((uint)processId);

    /// <summary>
    /// Moves <paramref name="hwnd"/> sideways by <paramref name="dx"/> pixels and makes it
    /// <paramref name="wider"/> pixels wider (narrower when negative) in one step, so the screen never
    /// shows it moved and not yet resized. With a <paramref name="cover"/> (a picture of all it is about
    /// to show, BGRA rows from the top, <paramref name="coverWidth"/> pixels across), that is held over
    /// it (<see cref="WindowCover"/>) until <see cref="WindowCover.Hide"/>; without, what it gains on
    /// either side is filled with <paramref name="fill"/> (0xRRGGBB), though under Avalonia's ANGLE
    /// surface that didn't show in a capture at 60 fps (2026-10-25). False when it couldn't.
    /// </summary>
    public static bool MoveAndWiden(nint hwnd, int dx, int wider, uint fill, byte[]? cover = null, int coverWidth = 0)
    {
        EnsurePhysicalPixels();
        if (!GetWindowRect(hwnd, out var r) || !ClientOnScreen(hwnd, out var before))
            return false;
        var shield = cover is not null && coverWidth > 0 ? WindowCover.Make(hwnd, before.Left + dx, before.Top, coverWidth, cover) : 0;
        // The cover shown and the window resized in one step.
        var step = BeginDeferWindowPos(2);
        if (step != 0 && shield != 0)
            step = DeferWindowPos(step, shield, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        if (step != 0)
            step = DeferWindowPos(step, hwnd, 0, r.Left + dx, r.Top, r.Right - r.Left + wider, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOACTIVATE);
        if (step == 0 || !EndDeferWindowPos(step))
        {
            WindowCover.Hide();
            return false;
        }
        if (shield != 0 || !ClientOnScreen(hwnd, out var after))
            return true;
        var dc = GetDC(hwnd);
        var brush = CreateSolidBrush(((fill & 0xFF) << 16) | (fill & 0xFF00) | ((fill >> 16) & 0xFF));
        // In the window's own pixels, left of where it was and right of it.
        var left = new RECT { Left = 0, Top = 0, Right = before.Left - after.Left, Bottom = after.Bottom - after.Top };
        var right = new RECT { Left = before.Right - after.Left, Top = 0, Right = after.Right - after.Left, Bottom = after.Bottom - after.Top };
        if (left.Right > 0)
            FillRect(dc, ref left, brush);
        if (right.Right > right.Left)
            FillRect(dc, ref right, brush);
        DeleteObject(brush);
        ReleaseDC(hwnd, dc);
        return true;
    }

    /// <summary>A window's client area where it is on the screen.</summary>
    private static bool ClientOnScreen(nint hwnd, out RECT rect)
    {
        rect = default;
        var corner = new POINT();
        if (!GetClientRect(hwnd, out var client) || !ClientToScreen(hwnd, ref corner))
            return false;
        rect = new RECT { Left = corner.X, Top = corner.Y, Right = corner.X + client.Right, Bottom = corner.Y + client.Bottom };
        return true;
    }

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
        var file = Path.Combine(Path.GetTempPath(), $"sloperator-command-{Guid.NewGuid():N}.ps1");
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
    /// not just 1), or 0 when all that failed was a program's messages on stderr taken in with 2&gt;&amp;1,
    /// which Windows PowerShell 5.1 counts as errors although the program succeeded: every ffmpeg
    /// call (it writes its progress there) came back as "Exit code 1" and counted as a failure.
    /// </summary>
    private const string ExitCode =
        "if (-not $?) { if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; " +
        "if ($Error.Count -gt 0 -and \"$($Error[0].FullyQualifiedErrorId)\" -like 'NativeCommandError*') { exit 0 }; exit 1 }";
}
