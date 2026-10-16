using System.Diagnostics;

namespace BlackBox.Core.Platform;

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>A captured frame in 32-bit BGRA, top-down rows.</summary>
public sealed class RawImage(int width, int height, byte[] bgra)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Bgra { get; } = bgra;
    public int Stride => Width * 4;
}

public enum MouseButton { Left, Right, Middle }

/// <summary>Screen capture in physical pixels.</summary>
public interface IScreenCapture
{
    /// <summary>All monitors in physical pixels, the primary first, in a stable order.</summary>
    IReadOnlyList<ScreenRect> GetScreens();

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

    /// <summary>
    /// Presses keys down and keeps them down until the result is disposed, then releases them in
    /// reverse order: modifiers held through a click or drag, or a key held for a while in a game.
    /// </summary>
    IDisposable HoldKeys(IReadOnlyList<string> keys);

    /// <summary>Returns false for key names the platform does not understand.</summary>
    bool IsKnownKey(string key);
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
    public IDisposable StepAside(params (int X, int Y)[] points) => Nothing.Instance;
    public void YieldFocus() { }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Shows the user what the agent just did with the mouse and keyboard: a short log in a screen
/// corner that fades out, and a marker where it clicked. It never takes focus or clicks, and
/// it is invisible to screenshots so the model never sees it.
/// </summary>
public interface IInputOverlay
{
    bool Enabled { get; set; }

    /// <summary>How long a log line stays on screen, fade-out included.</summary>
    TimeSpan Duration { get; set; }

    /// <param name="text">What happened, e.g. "Left click" or "Keys: Ctrl+S".</param>
    /// <param name="point">Where on the screen (physical pixels), for a marker; null for keyboard input.</param>
    void Show(string text, (int X, int Y)? point = null);
}

public sealed class NoInputOverlay : IInputOverlay
{
    public static readonly NoInputOverlay Instance = new();
    public bool Enabled { get; set; }
    public TimeSpan Duration { get; set; }
    public void Show(string text, (int X, int Y)? point = null) { }
}

/// <summary>
/// A system-wide stop key (Pause/Break) that works whatever window has focus.
/// </summary>
public interface IStopKey
{
    /// <summary>
    /// Calls <paramref name="onPressed"/> (on any thread) when the user presses the key. It returns
    /// true if it stopped something, in which case the press is swallowed, with any keys pressed
    /// along with it; otherwise the key goes through to the focused app as usual.
    /// </summary>
    IDisposable Watch(Func<bool> onPressed);
}

public sealed class NoStopKey : IStopKey
{
    public static readonly NoStopKey Instance = new();
    public IDisposable Watch(Func<bool> onPressed) => new Nothing();

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>Everything OS-specific the agent needs. One implementation per platform.</summary>
public interface IPlatformServices
{
    string Name { get; }
    IScreenCapture Screen { get; }
    IInputController Input { get; }
    IStopKey StopKey { get; }

    IHostWindow HostWindow { get; }

    IInputOverlay InputOverlay { get; }

    /// <summary>Start info for running one command string in the platform shell (no redirection set).</summary>
    ProcessStartInfo CreateShellStartInfo(string command);

    /// <summary>The OS's own video and sound decoders; null where there are none (ffmpeg stands in).</summary>
    Media.IMediaDecoder? Media => null;

    /// <summary>A way to play sound through the speakers, for the chat's players; null where there is none.</summary>
    Media.IAudioOutput? CreateAudioOutput() => null;
}

/// <summary>Fallback for operating systems without an implementation yet.</summary>
public sealed class UnsupportedPlatform : IPlatformServices, IScreenCapture, IInputController
{
    public string Name => $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} (desktop control not implemented)";
    public IScreenCapture Screen => this;
    public IInputController Input => this;
    public IHostWindow HostWindow => NoHostWindow.Instance;
    public IInputOverlay InputOverlay => NoInputOverlay.Instance;
    public IStopKey StopKey => NoStopKey.Instance;
    public ProcessStartInfo CreateShellStartInfo(string command) => new("/bin/sh") { ArgumentList = { "-c", command } };

    private static NotSupportedException Fail() => new("Desktop control is not implemented on this OS yet.");

    public IReadOnlyList<ScreenRect> GetScreens() => throw Fail();
    public RawImage Capture(ScreenRect region, bool includeCursor) => throw Fail();
    public (int X, int Y) GetCursorPosition() => throw Fail();
    public void MoveMouse(int x, int y) => throw Fail();
    public void MouseDown(MouseButton button) => throw Fail();
    public void MouseUp(MouseButton button) => throw Fail();
    public void Click(MouseButton button, int count) => throw Fail();
    public void Scroll(int dx, int dy) => throw Fail();
    public void TypeText(string text) => throw Fail();
    public void PressKeys(IReadOnlyList<string> keys) => throw Fail();
    public IDisposable HoldKeys(IReadOnlyList<string> keys) => throw Fail();
    public bool IsKnownKey(string key) => false;
}
