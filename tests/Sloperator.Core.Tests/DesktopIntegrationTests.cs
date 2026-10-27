#if WINDOWS
using System.Text.Json.Nodes;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Sloperator.Platform.Windows;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in (set BB_DESKTOP_TESTS=1): moves the real mouse on this PC for a moment to check that
/// screenshots and mouse coordinates agree across monitors. Needs two monitors; skipped otherwise.
/// </summary>
[Collection(RealScreen.Name)]
public class DesktopIntegrationTests
{
    [Fact]
    public async Task Screens_and_mouse_agree_across_two_monitors()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("BB_DESKTOP_TESTS") is null, "Set BB_DESKTOP_TESTS=1 to run (uses the real mouse).");
        var platform = new WindowsPlatform();
        var screens = platform.Screen.GetScreens();
        Assert.SkipWhen(screens.Count < 2, "Needs two monitors.");
        // Also written to bb-desktop-test.txt in the temp folder, where the test runner does not show diagnostics.
        var logFile = Path.Combine(Path.GetTempPath(), "bb-desktop-test.txt");
        File.WriteAllText(logFile, "");
        var log = (string s) => { TestContext.Current.SendDiagnosticMessage(s); File.AppendAllText(logFile, s + "\n"); };
        log("Screens: " + string.Join("; ", screens));

        var ctx = new ToolContext
        {
            Platform = platform,
            Settings = new AgentSettings { ScreenshotAfterActions = false },
            WorkingDirectory = Path.GetTempPath(),
        };
        var cursorBefore = platform.Input.GetCursorPosition();
        try
        {
            // 1. The primary is first and at the origin; screens do not overlap.
            Assert.Equal((0, 0), (screens[0].X, screens[0].Y));
            for (var i = 0; i < screens.Count; i++)
                for (var j = i + 1; j < screens.Count; j++)
                    Assert.False(Overlap(screens[i], screens[j]), $"screens {i + 1} and {j + 1} overlap");

            // 2. Each screen's screenshot has the size the prompt promises for it.
            var spaces = ctx.ScreenSpaces();
            for (var i = 0; i < screens.Count; i++)
            {
                var shot = await new ScreenshotTool().ExecuteAsync(new JsonObject { ["screen"] = i + 1 }, ctx, CancellationToken.None);
                Assert.Contains($"screen {i + 1} of {screens.Count}", shot.Text);
                using var bitmap = SKBitmap.Decode(shot.Image!);
                Assert.Equal((spaces[i].ImageWidth, spaces[i].ImageHeight), (bitmap.Width, bitmap.Height));
                Assert.Equal(i, ctx.CurrentScreen);
            }

            // 3. The mouse lands where screen-2 coordinates say, and screenshots report the right screen for it.
            var second = spaces[1];
            await new MouseMoveTool().ExecuteAsync(new JsonObject { ["x"] = 200, ["y"] = 150 }, ctx, CancellationToken.None);
            var cursor = platform.Input.GetCursorPosition();
            var expected = second.ToScreen(200, 150);
            log($"mouse_move(200,150) on screen 2: cursor {cursor}, expected {expected}; screenshots {second.ImageWidth}x{second.ImageHeight}");
            Assert.True(Math.Abs(cursor.X - expected.X) <= 1 && Math.Abs(cursor.Y - expected.Y) <= 1, $"cursor at {cursor}, expected {expected}");
            Assert.True(screens[1].Contains(cursor.X, cursor.Y), $"cursor {cursor} is not on screen 2 {screens[1]}");
            var onSecond = ScreenshotTool.Take(ctx);
            Assert.Matches(@"Mouse cursor at \((199|200|201), (149|150|151)\)", onSecond.Text);
            ctx.CurrentScreen = 0;
            Assert.Contains("Mouse cursor is on screen 2", ScreenshotTool.Take(ctx).Text);
        }
        finally
        {
            platform.Input.MoveMouse(cursorBefore.X, cursorBefore.Y);
        }
    }

    private static bool Overlap(Core.Platform.ScreenRect a, Core.Platform.ScreenRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}
#endif
