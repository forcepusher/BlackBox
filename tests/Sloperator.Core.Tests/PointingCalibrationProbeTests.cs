using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// How steady a model's pointing check is: the calibration picture asked BB_CALIB_REPEAT times
/// (default 10) on the server in BB_SERVER_URL / BB_SERVER_MODEL (BB_SERVER_KEY), each answer and
/// what it was read as written to bb-calib-&lt;BB_CLICK_TAG&gt;.txt in the temp folder. Opt-in.
/// </summary>
public class PointingCalibrationProbeTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    [Fact]
    public async Task The_pointing_check_gives_the_same_answer_each_time()
    {
        Assert.SkipWhen(Env("BB_SERVER_URL") is null || Env("BB_SERVER_MODEL") is null || Env("BB_CALIB_REPEAT") is null, "Set BB_SERVER_URL, BB_SERVER_MODEL and BB_CALIB_REPEAT to run.");
        var server = new ServerSettings { BaseUrl = Env("BB_SERVER_URL")!, Model = Env("BB_SERVER_MODEL")!, ApiKey = Env("BB_SERVER_KEY") ?? "" };
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(10));
        await ServerProbe.EnsureLoadedAsync(http, server, null, CancellationToken.None);
        var connection = BackendConnection.Create(http, server);
        var (width, height) = (2560, 1088);
        var file = Path.Combine(Path.GetTempPath(), $"bb-calib-{Env("BB_CLICK_TAG") ?? "probe"}.txt");
        File.Delete(file);
        var repeat = int.Parse(Env("BB_CALIB_REPEAT")!);
        var (grids, pixels, none) = (0, 0, 0);
        for (var i = 0; i < repeat; i++)
        {
            var answer = await new FreshLook(connection.Client, connection.Options)
                .AskAsync([PointingCalibration.Picture(width, height)], PointingCalibration.Question(width, height), CancellationToken.None);
            var grid = PointingCalibration.Read(answer, width, height);
            var said = answer?.ReplaceLineEndings(" ").Clip(200) ?? "(no answer)";
            if (grid is not null) grids++;
            else if (answer is not null && System.Text.RegularExpressions.Regex.IsMatch(answer, @"red""?\s*[:=]")) pixels++;
            else none++;
            File.AppendAllLines(file, [$"{i + 1}: {(grid is null ? "pixels" : "grid")} | {said}"]);
        }
        File.AppendAllLines(file, [$"TOTAL {server.Model}: grid {grids}, pixels {pixels}, unreadable {none} of {repeat}"]);
    }

    /// <summary>
    /// The same on a real screenshot (ClickScenes' Unity editor, or the file in BB_CALIB_SHOT): words
    /// found on it by Windows' text recognition, the model asked for their x/y in pixels.
    /// </summary>
    [Fact]
    public async Task The_pointing_check_on_a_real_screenshot()
    {
        Assert.SkipWhen(Env("BB_SERVER_URL") is null || Env("BB_SERVER_MODEL") is null || Env("BB_CALIB_REPEAT") is null || !OperatingSystem.IsWindows(),
            "Set BB_SERVER_URL, BB_SERVER_MODEL and BB_CALIB_REPEAT to run, on Windows.");
        var server = new ServerSettings { BaseUrl = Env("BB_SERVER_URL")!, Model = Env("BB_SERVER_MODEL")!, ApiKey = Env("BB_SERVER_KEY") ?? "" };
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(10));
        await ServerProbe.EnsureLoadedAsync(http, server, null, CancellationToken.None);
        var connection = BackendConnection.Create(http, server);
        using var shot = SkiaSharp.SKBitmap.Decode(Env("BB_CALIB_SHOT") ?? Path.Combine(AppContext.BaseDirectory, "ClickScenes", "unity-editor.png"))
            .Copy(SkiaSharp.SKColorType.Bgra8888);
        var (width, height) = (shot.Width, shot.Height);
        var lines = new Sloperator.Platform.Windows.WindowsTextReader().Read(new Platform.RawImage(width, height, shot.Bytes));
        Assert.NotNull(lines);
        var marks = PointingCalibration.Words(lines, width, height);
        using var jpeg = shot.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        var file = Path.Combine(Path.GetTempPath(), $"bb-calib-{Env("BB_CLICK_TAG") ?? "probe"}.txt");
        File.Delete(file);
        File.AppendAllLines(file, [$"marks: {string.Join(", ", marks.Select(m => $"{m.Name} ({m.X * width:0}, {m.Y * height:0}) = grid ({m.X * 1000:0}, {m.Y * 1000:0})"))}"]);
        var repeat = int.Parse(Env("BB_CALIB_REPEAT")!);
        var (grids, pixels, none) = (0, 0, 0);
        for (var i = 0; i < repeat; i++)
        {
            var answer = await new FreshLook(connection.Client, connection.Options)
                .AskAsync([jpeg.ToArray()], PointingCalibration.Question(width, height, marks), CancellationToken.None);
            var grid = PointingCalibration.Read(answer, width, height, marks);
            var off = PointingCalibration.Offsets(answer, width, height, marks);
            var said = answer?.ReplaceLineEndings(" ").Clip(300) ?? "(no answer)";
            if (grid is not null) grids++;
            else if (off is not null) pixels++;
            else none++;
            var offText = off is { } o ? $"off as pixels {o.Pixels:0.000}, as grid {o.Grid:0.000}" : "unread";
            File.AppendAllLines(file, [$"{i + 1}: {(grid is null ? "pixels" : "grid")} ({offText}) | {said}"]);
        }
        File.AppendAllLines(file, [$"TOTAL {server.Model}: grid {grids}, pixels {pixels}, unreadable {none} of {repeat}"]);
    }
}
