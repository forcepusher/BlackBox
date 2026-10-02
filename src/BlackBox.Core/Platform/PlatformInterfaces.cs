using System.Diagnostics;

namespace BlackBox.Core.Platform;

public readonly record struct ScreenRect(int X, int Y, int Width, int Height);

/// <summary>A captured frame in 32-bit BGRA, top-down rows.</summary>
public sealed class RawImage(int width, int height, byte[] bgra)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Bgra { get; } = bgra;
    public int Stride => Width * 4;
}

public enum MouseButton { Left, Right, Middle }

public sealed record WindowInfo(long Handle, string Title, string ProcessName, ScreenRect Bounds, bool IsMinimized);

/// <summary>Screen capture in physical pixels.</summary>
public interface IScreenCapture
{
    /// <summary>Bounds of the primary monitor in physical pixels.</summary>
    ScreenRect GetPrimaryScreen();

    RawImage Capture(ScreenRect region, bool includeCursor);
}

/// <summary>Synthetic mouse and keyboard input. Coordinates are physical screen pixels.</summary>
public interface IInputController
{
    (int X, int Y) GetCursorPosition();
    void MoveMouse(int x, int y);
    void MouseDown(MouseButton button);
    void MouseUp(MouseButton button);
    void Click(MouseButton button, int count);

    /// <summary>Scrolls by whole wheel notches. Positive <paramref name="dy"/> scrolls down, positive <paramref name="dx"/> scrolls right.</summary>
    void Scroll(int dx, int dy);

    /// <summary>Types literal text (Unicode-safe, independent of keyboard layout).</summary>
    void TypeText(string text);

    /// <summary>Presses a chord such as ["ctrl", "shift", "s"] and releases it in reverse order.</summary>
    void PressKeys(IReadOnlyList<string> keys);

    /// <summary>Returns false for key names the platform does not understand.</summary>
    bool IsKnownKey(string key);
}

public interface IWindowManager
{
    IReadOnlyList<WindowInfo> ListWindows();
    bool FocusWindow(long handle);
}

/// <summary>A control found through the OS accessibility tree.</summary>
public sealed record UiElement(int Id, string ControlType, string Name, string? Value, ScreenRect Bounds, bool IsEnabled);

/// <summary>
/// Accessibility-tree access (UI Automation on Windows, AT-SPI on Linux, AX on macOS).
/// Lets the model act on controls by name instead of by pixel, which small vision models need.
/// </summary>
public interface IUiAutomation
{
    /// <summary>Snapshot of the visible controls in the foreground window (or the window whose title contains <paramref name="windowTitle"/>). Ids are valid until the next snapshot.</summary>
    IReadOnlyList<UiElement> GetElements(string? windowTitle, int maxCount);

    /// <summary>Activates a control from the last snapshot (invoke, toggle or select). Returns false if it has no such action.</summary>
    bool Activate(int id);
}

/// <summary>
/// BlackBox's own window. It stays on screen while the agent works, so it is kept out of the
/// agent's way: invisible to screenshots, and pushed behind other windows for the moment an
/// action lands on it.
/// </summary>
public interface IHostWindow
{
    /// <summary>Registers the native window handle and, if asked, hides it from screen capture where the OS allows.</summary>
    void Attach(nint handle, bool hideFromCapture);

    /// <summary>Turns capture hiding on or off after <see cref="Attach"/>.</summary>
    void SetHiddenFromCapture(bool hide);

    /// <summary>True when screenshots show what is behind the window rather than the window itself.</summary>
    bool HiddenFromCapture { get; }

    /// <summary>If any of the points lie on the window, sends it behind other windows until the result is disposed.</summary>
    IDisposable StepAside(params (int X, int Y)[] points);

    /// <summary>If the window has keyboard focus, hands focus to the window underneath it.</summary>
    void YieldFocus();
}

public sealed class NoHostWindow : IHostWindow
{
    public static readonly NoHostWindow Instance = new();
    public void Attach(nint handle, bool hideFromCapture) { }
    public void SetHiddenFromCapture(bool hide) { }
    public bool HiddenFromCapture => false;
    public IDisposable StepAside(params (int X, int Y)[] points) => Nothing.Instance;
    public void YieldFocus() { }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Everything OS-specific the agent needs. One implementation per platform.</summary>
public interface IPlatformServices
{
    string Name { get; }
    IScreenCapture Screen { get; }
    IInputController Input { get; }
    IWindowManager Windows { get; }

    /// <summary>Null where no accessibility backend exists yet.</summary>
    IUiAutomation? Automation { get; }

    IHostWindow HostWindow { get; }

    /// <summary>Makes sure a child process dies with the app (e.g. a Windows job object).</summary>
    void TieToAppLifetime(Process process);

    /// <summary>Start info for running one command string in the platform shell (no redirection set).</summary>
    ProcessStartInfo CreateShellStartInfo(string command);
}

/// <summary>Fallback for operating systems without an implementation yet.</summary>
public sealed class UnsupportedPlatform : IPlatformServices, IScreenCapture, IInputController, IWindowManager
{
    public string Name => $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} (desktop control not implemented)";
    public IScreenCapture Screen => this;
    public IInputController Input => this;
    public IWindowManager Windows => this;
    public IUiAutomation? Automation => null;
    public IHostWindow HostWindow => NoHostWindow.Instance;
    public ProcessStartInfo CreateShellStartInfo(string command) => new("/bin/sh") { ArgumentList = { "-c", command } };
    public void TieToAppLifetime(Process process) { }

    private static NotSupportedException Fail() => new("Desktop control is not implemented on this OS yet.");

    public ScreenRect GetPrimaryScreen() => throw Fail();
    public RawImage Capture(ScreenRect region, bool includeCursor) => throw Fail();
    public (int X, int Y) GetCursorPosition() => throw Fail();
    public void MoveMouse(int x, int y) => throw Fail();
    public void MouseDown(MouseButton button) => throw Fail();
    public void MouseUp(MouseButton button) => throw Fail();
    public void Click(MouseButton button, int count) => throw Fail();
    public void Scroll(int dx, int dy) => throw Fail();
    public void TypeText(string text) => throw Fail();
    public void PressKeys(IReadOnlyList<string> keys) => throw Fail();
    public bool IsKnownKey(string key) => false;
    public IReadOnlyList<WindowInfo> ListWindows() => throw Fail();
    public bool FocusWindow(long handle) => throw Fail();
}
