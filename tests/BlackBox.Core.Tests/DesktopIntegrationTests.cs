#if WINDOWS
using System.Diagnostics;
using System.Text.Json.Nodes;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using BlackBox.Platform.Windows;
using SkiaSharp;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>
/// Opt-in (set BB_DESKTOP_TESTS=1): drives the real mouse, keyboard and a Notepad window on this
/// PC for about ten seconds to check that the tools agree with each other across monitors. Needs
/// two monitors; skipped otherwise.
/// </summary>
public class DesktopIntegrationTests
{
    [Fact]
    public async Task Screens_mouse_windows_and_controls_agree_across_two_monitors()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("BB_DESKTOP_TESTS") is null, "Set BB_DESKTOP_TESTS=1 to run (uses the real mouse and keyboard).");
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
            Settings = new AgentSettings { FailsafeCorner = false, ScreenshotAfterActions = false },
            WorkingDirectory = Path.GetTempPath(),
        };
        var cursorBefore = platform.Input.GetCursorPosition();
        Process? notepad = null;
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

            // 4. A real window: Notepad, moved to the other monitor with the keyboard.
            notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            var window = await WaitForWindowAsync(ctx, notepad!.Id);
            log($"Notepad window: {window.Bounds}, on screen {ListWindowsTool.ScreenOf(ctx, window) + 1}");
            var focus = await new FocusWindowTool().ExecuteAsync(new JsonObject { ["handle"] = window.Handle }, ctx, CancellationToken.None);
            Assert.StartsWith("Focused", focus.Text);
            var from = ListWindowsTool.ScreenOf(ctx, window);
            var to = from == 0 ? 1 : 0;
            Assert.Equal(from, ctx.CurrentScreen);
            // win+shift+arrow moves the active window to the next monitor in that direction.
            var direction = screens[to].X > screens[from].X ? "right" : screens[to].X < screens[from].X ? "left" : screens[to].Y > screens[from].Y ? "down" : "up";
            await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = new JsonArray("win", "shift", direction) }, ctx, CancellationToken.None);
            await Task.Delay(800);
            window = (await WaitForWindowAsync(ctx, notepad.Id));
            Assert.True(ListWindowsTool.ScreenOf(ctx, window) == to, $"Notepad did not move to screen {to + 1}: {window.Bounds}");
            var list = await new ListWindowsTool().ExecuteAsync([], ctx, CancellationToken.None);
            var line = list.Text.Split('\n').First(l => l.Contains($"handle={window.Handle}"));
            Assert.Contains($"screen={to + 1}", line);
            log("list_windows: " + line.Trim());

            // focus_window follows it, and the window's bounds are within that screen's image.
            focus = await new FocusWindowTool().ExecuteAsync(new JsonObject { ["handle"] = window.Handle }, ctx, CancellationToken.None);
            Assert.Contains($"It is on screen {to + 1}", focus.Text);
            Assert.Equal(to, ctx.CurrentScreen);

            // 5. Its controls are reported in that screen's coordinates, inside the image.
            ctx.CurrentScreen = from; // as if the model had last looked at the other screen
            var controls = await new ListUiElementsTool().ExecuteAsync(new JsonObject { ["window_title"] = "Notepad" }, ctx, CancellationToken.None);
            log("list_ui_elements: " + string.Join(" | ", controls.Text.Split('\n').Take(6).Select(l => l.Trim())));
            Assert.StartsWith($"These controls are on screen {to + 1}", controls.Text);
            Assert.Equal(to, ctx.CurrentScreen);
            var space = spaces[to];
            foreach (var m in System.Text.RegularExpressions.Regex.Matches(controls.Text, @"at \((-?\d+), (-?\d+)\)"))
            {
                var (cx, cy) = (int.Parse(((System.Text.RegularExpressions.Match)m).Groups[1].Value), int.Parse(((System.Text.RegularExpressions.Match)m).Groups[2].Value));
                Assert.InRange(cx, 0, space.ImageWidth);
                Assert.InRange(cy, 0, space.ImageHeight);
            }

            // 6. Clicking into the window by screen-2 coordinates and typing reaches it.
            var (wx, wy) = space.ToImage(window.Bounds.X + window.Bounds.Width / 2, window.Bounds.Y + window.Bounds.Height / 2);
            await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = wx, ["y"] = wy }, ctx, CancellationToken.None);
            await new TypeTextTool().ExecuteAsync(new JsonObject { ["text"] = "multi-monitor ok" }, ctx, CancellationToken.None);
            await Task.Delay(300);
            var typed = platform.Automation!.GetElements("Notepad", 400);
            log("focus_window: " + focus.Text + " | typed into: " + string.Join(", ", typed.Where(e => e.Value?.Contains("multi-monitor ok") == true || e.Name.Contains("multi-monitor ok")).Select(e => $"{e.ControlType} {e.Bounds}")));
            Assert.Contains(typed, e => e.Value?.Contains("multi-monitor ok") == true || e.Name.Contains("multi-monitor ok"));

            // 7. Zoom works on that screen too.
            var zoom = await new ZoomTool().ExecuteAsync(new JsonObject { ["x"] = wx - 100, ["y"] = wy - 50, ["width"] = 200, ["height"] = 100 }, ctx, CancellationToken.None);
            Assert.NotNull(zoom.Image);
        }
        finally
        {
            try { notepad?.Kill(); } catch (InvalidOperationException) { }
            platform.Input.MoveMouse(cursorBefore.X, cursorBefore.Y);
        }
    }

    private static bool Overlap(Core.Platform.ScreenRect a, Core.Platform.ScreenRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static async Task<Core.Platform.WindowInfo> WaitForWindowAsync(ToolContext ctx, int pid)
    {
        var name = Process.GetProcessById(pid).ProcessName;
        for (var i = 0; i < 100; i++)
        {
            var w = ctx.Platform.Windows.ListWindows().FirstOrDefault(w => w.ProcessName == name && !w.IsMinimized && w.Bounds.Width > 100);
            if (w is not null) return w;
            await Task.Delay(100);
        }
        throw new TimeoutException("Notepad's window did not appear.");
    }
}
#endif
