using Sloperator.Core.Documents;
using Sloperator.Core.Platform;
using Xunit;

namespace Sloperator.Core.Tests;

// An area dragged over a page, a picture or a frame in the File inspector panel.
public class DocumentAreaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-area-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Corners_dragged_either_way_make_the_same_area_kept_on_what_it_is_on()
    {
        Assert.Equal(new DocumentArea(0.2, 0.1, 0.6, 0.5, 1), DocumentArea.Between(0.6, 0.5, 0.2, 0.1, 1));
        // Dragged out past the edge: up to it.
        Assert.Equal(new DocumentArea(0, 0.5, 0.4, 1), DocumentArea.Between(-0.3, 0.5, 0.4, 1.7));
        // A click has no area.
        Assert.Null(DocumentArea.Between(0.3, 0.3, 0.3, 0.8));
        Assert.Null(DocumentArea.Between(1.2, 0.1, 1.5, 0.4));
    }

    [Fact]
    public void Where_and_place_say_it_as_the_card_and_the_model_get_it()
    {
        var area = new DocumentArea(0.25, 0.5, 0.75, 1, 1);
        Assert.Equal("area on page 2", area.Where);
        Assert.Equal("area", (area with { Page = null }).Where);
        Assert.Equal("x 153-459, y 396-792 of the 612x792 points, from the top left", area.Place(612, 792, "points"));
    }

    [Fact]
    public void An_area_is_cut_out_of_a_picture_and_saved()
    {
        // 4x2: each pixel's blue is its x, its green its y.
        var bgra = new byte[4 * 2 * 4];
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 4; x++)
                (bgra[(y * 4 + x) * 4], bgra[(y * 4 + x) * 4 + 1], bgra[(y * 4 + x) * 4 + 3]) = ((byte)x, (byte)y, 255);
        var picture = new RawImage(4, 2, bgra);

        var cut = new DocumentArea(0.5, 0.5, 1, 1).Crop(picture);
        Assert.Equal((2, 1), (cut.Width, cut.Height));
        Assert.Equal([2, 1, 0, 255, 3, 1, 0, 255], cut.Bgra);
        // A sliver is a pixel at least.
        var sliver = new DocumentArea(0.1, 0.1, 0.11, 0.11).Pixels(4, 2);
        Assert.Equal((1, 1), (sliver.Width, sliver.Height));

        var file = Path.Combine(_dir, "Area ü.png");
        DocumentArea.SavePng(cut, file);
        var back = DocumentArea.Decode(file)!;
        Assert.Equal((2, 1), (back.Width, back.Height));
        Assert.Equal(cut.Bgra, back.Bgra);
    }
}
