using Sloperator.Core.Agent;
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
}
