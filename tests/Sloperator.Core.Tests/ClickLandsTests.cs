#if WINDOWS
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Sloperator.Platform.Windows;
using SkiaSharp;
using Xunit;
using static Sloperator.Platform.Windows.Native;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in (set BB_DESKTOP_TESTS=1, uses the real mouse): a click lands on what the model aimed at in
/// its screenshot, whatever the display scaling. A small red square is found in Sloperator's own
/// screenshot, as a model finds a button, and clicked through mouse_click, in screenshot pixels and on
/// the 0-1000 grid, with the screenshot scaled down. Screenshots, screens and the cursor must all be in
/// the same pixels: on a 1920x1200 screen at 125%, coordinates read off a 1536x960 picture landed every
/// click 25% too far right and down (2026-10-28). Before clicking, the spot is checked to be the
/// square's, so a miss fails without clicking anything else.
/// </summary>
[Collection(RealScreen.Name)]
public class ClickLandsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1000)]
    public async Task A_click_lands_on_what_the_screenshot_shows_there(int? grid)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("BB_DESKTOP_TESTS") is null, "Set BB_DESKTOP_TESTS=1 to run (uses the real mouse).");
        var platform = new WindowsPlatform();
        var screen = platform.Screen.GetScreens()[0];
        var ctx = new ToolContext
        {
            Platform = platform,
            // Smaller than any screen, so the screenshot is scaled down too.
            Settings = new AgentSettings { ScreenshotAfterActions = false, ScreenshotMaxWidth = 1024, ScreenshotMaxHeight = 640 },
            WorkingDirectory = Path.GetTempPath(),
            CoordinateGrid = grid,
        };
        var cursorBefore = platform.Input.GetCursorPosition();
        // Away from the middle, where a wrong scale moves a point the most.
        using var square = new RedSquare(screen.X + screen.Width * 7 / 10, screen.Y + screen.Height * 7 / 10, 40);
        try
        {
            var shot = await new ScreenshotTool().ExecuteAsync(new JsonObject(), ctx, CancellationToken.None);
            using var bitmap = SKBitmap.Decode(shot.Image!);
            var (cx, cy) = RedCentre(bitmap);
            var space = ctx.GetScreenSpace();
            var (x, y) = grid is { } g ? (cx * g / bitmap.Width, cy * g / bitmap.Height) : (cx, cy);

            var aim = space.ToScreen(x, y);
            Assert.True(GetAncestor(WindowFromPoint(new POINT { X = aim.X, Y = aim.Y }), GA_ROOT) == square.Handle,
                $"({x:0}, {y:0}) in the screenshot is {aim} on the screen, not on the square at {square.Rect}");
            await new MouseClickTool().ExecuteAsync(new JsonObject { ["target"] = "", ["which"] = MouseClickTool.OnlyOne, ["x"] = x, ["y"] = y }, ctx, CancellationToken.None);
            Assert.True(square.Clicked.Wait(TimeSpan.FromSeconds(2)), "the square got no click");
        }
        finally
        {
            platform.Input.MoveMouse(cursorBefore.X, cursorBefore.Y);
        }
    }

    /// <summary>The centre of the red pixels, in the picture's pixels.</summary>
    private static (double X, double Y) RedCentre(SKBitmap bitmap)
    {
        double sx = 0, sy = 0;
        var n = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red > 200 && c.Green < 50 && c.Blue < 50) { sx += x; sy += y; n++; }
            }
        Assert.True(n > 0, "the square isn't in the screenshot");
        return (sx / n + 0.5, sy / n + 0.5);
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    /// <summary>
    /// A topmost red square in another process (Windows PowerShell), as screenshots leave Sloperator's
    /// own windows out; it writes "clicked" when clicked.
    /// </summary>
    private sealed class RedSquare : IDisposable
    {
        private readonly System.Diagnostics.Process _process;

        public nint Handle { get; }
        public Core.Platform.ScreenRect Rect { get; }
        public ManualResetEventSlim Clicked { get; } = new();

        public RedSquare(int x, int y, int size)
        {
            Rect = new(x, y, size, size);
            var script = $$"""
                Add-Type -AssemblyName System.Windows.Forms
                $f = New-Object Windows.Forms.Form
                $f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.TopMost = $true; $f.ShowInTaskbar = $false
                $f.BackColor = [Drawing.Color]::Red
                $f.Add_Load({ $f.Bounds = New-Object Drawing.Rectangle({{x}}, {{y}}, {{size}}, {{size}}) })
                $f.Add_MouseDown({ [Console]::Out.WriteLine('clicked'); [Console]::Out.Flush() })
                $f.Add_Shown({ [Console]::Out.WriteLine('shown ' + $f.Handle); [Console]::Out.Flush() })
                [Windows.Forms.Application]::Run($f)
                """;
            _process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var shown = new TaskCompletionSource<nint>();
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data?.StartsWith("shown ") == true) shown.TrySetResult(nint.Parse(e.Data[6..]));
                else if (e.Data == "clicked") Clicked.Set();
            };
            _process.BeginOutputReadLine();
            Assert.True(shown.Task.Wait(TimeSpan.FromSeconds(30)), "the square didn't show");
            Handle = shown.Task.Result;
            Thread.Sleep(300); // painted and composed
        }

        public void Dispose()
        {
            try { _process.Kill(); } catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }
}
#endif
