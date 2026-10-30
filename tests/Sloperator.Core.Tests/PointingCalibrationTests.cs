using Sloperator.Core.Agent;
using Sloperator.Core.Platform;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

public class PointingCalibrationTests
{
    [Fact]
    public void Answers_in_pixels_keep_pixels_and_answers_on_the_grid_switch_to_it()
    {
        // The dots sit at (0.18, 0.27), (0.71, 0.64) and (0.43, 0.86) of a 2560x1088 picture.
        Assert.Null(PointingCalibration.Read("""{"red": [461, 294], "green": [1818, 696], "blue": [1101, 936]}""", 2560, 1088));
        // Nemotron's way: the same places on a 0-1000 grid, though it was asked for pixels.
        Assert.Equal(1000, PointingCalibration.Read("""{"red": [180, 270], "green": [712, 641], "blue": [430, 860]}""", 2560, 1088));
        // Thinking first, other quotes and brackets, one dot missed: still read.
        Assert.Equal(1000, PointingCalibration.Read("The dots are at red: (181, 268) and green=(709, 640).", 2560, 1088));
    }

    [Fact]
    public void An_answer_that_fits_neither_or_none_changes_nothing()
    {
        Assert.Null(PointingCalibration.Read(null, 2560, 1088));
        Assert.Null(PointingCalibration.Read("I can't see any dots.", 2560, 1088));
        Assert.Null(PointingCalibration.Read("""{"red": [10, 10], "green": [20, 20], "blue": [30, 30]}""", 2560, 1088));
        // A picture about 1000 wide: pixels and the grid are alike, so it stays in pixels.
        Assert.Null(PointingCalibration.Read("""{"red": [184, 294], "green": [727, 696], "blue": [440, 936]}""", 1024, 1088));
    }

    [Fact]
    public void The_picture_is_the_screenshots_size_with_its_three_dots()
    {
        using var image = SKBitmap.Decode(PointingCalibration.Picture(2560, 1088));
        Assert.Equal((2560, 1088), (image.Width, image.Height));
        var red = image.GetPixel((int)(0.18 * 2560), (int)(0.27 * 1088));
        Assert.True(red.Red > 180 && red.Green < 90, red.ToString());
        Assert.Equal(SKColors.White, image.GetPixel(5, 5));
    }

    private static TextLine Line(params (string Text, double X, double Y)[] words) =>
        new(string.Join(" ", words.Select(w => w.Text)), [.. words.Select(w => new TextWord(w.Text, w.X, w.Y, 40, 16))]);

    [Fact]
    public void Words_on_a_screenshot_are_picked_once_each_spread_out_and_away_from_the_middle()
    {
        var lines = new[]
        {
            Line(("File", 10, 10), ("Edit", 60, 10), ("Hierarchy", 1300, 80)),
            Line(("Inspector", 2400, 900), ("OK", 1280, 540), ("12.5", 600, 600)),
            Line(("Edit", 1200, 500), ("Project", 100, 1000)),
        };
        var marks = PointingCalibration.Words(lines, 2560, 1080);
        // "Edit" is there twice, "OK" too short, "12.5" not a word.
        Assert.Equal(["File", "Inspector", "Project"], marks.Select(m => m.Name));
        Assert.Equal(0.9453, marks[1].X, 3); // (2400 + 20) / 2560
    }

    [Fact]
    public void Answers_about_words_are_read_as_pixels_or_the_grid()
    {
        PointingCalibration.Mark[] marks = [new("Inspector", 0.9453, 0.8407), new("File", 0.0117, 0.0167), new("Project", 0.0469, 0.9333)];
        Assert.Null(PointingCalibration.Read("""{"Inspector": [2420, 908], "File": [30, 18], "Project": [120, 1008]}""", 2560, 1080, marks));
        Assert.Equal(1000, PointingCalibration.Read("""{"Inspector": [945, 841], "File": [12, 17], "Project": [47, 933]}""", 2560, 1080, marks));
        Assert.Contains("\"Inspector\": [x, y]", PointingCalibration.Question(2560, 1080, marks));
    }
}
