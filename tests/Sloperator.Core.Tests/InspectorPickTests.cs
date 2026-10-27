using Sloperator.Core.Documents;
using Sloperator.Core.Media;
using Xunit;

namespace Sloperator.Core.Tests;

// What the File inspector shows of a binary file, a sound and a 3D model, and how a pick in them is told.
public class InspectorPickTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-inspect-pick-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Bytes(int count)
    {
        var path = Path.Combine(_dir, "blob.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, count).Select(i => (byte)(i % 256)).ToArray());
        return path;
    }

    [Fact]
    public void A_binary_file_is_dumped_in_hex_sixteen_bytes_a_line()
    {
        var (text, length) = FileInspector.Hex(Bytes(0x48));
        var lines = text.Split('\n');
        Assert.Equal(0x48, length);
        Assert.Equal(5, lines.Length);
        Assert.Equal("00000040  40 41 42 43 44 45 46 47                           @ABCDEFG", lines[4]);
        Assert.StartsWith("00000000  00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  ................", lines[0]);
    }

    [Fact]
    public void Only_the_start_of_a_big_file_is_dumped()
    {
        var (text, length) = FileInspector.Hex(Bytes(1000), max: 64);
        Assert.Equal(1000, length);
        Assert.Equal(4, text.Split('\n').Length);
    }

    [Fact]
    public void A_selection_in_a_hex_dump_is_the_bytes_it_covers()
    {
        var (text, _) = FileInspector.Hex(Bytes(0x48));
        var line4 = text.IndexOf("00000040", StringComparison.Ordinal);
        // "41 42" in hex: bytes 0x41-0x42.
        var at = line4 + 10 + 3;
        var pick = DocumentPick.InHex(text, at, at + 5);
        Assert.Equal("bytes 0x41-0x42", pick!.Where);
        Assert.Equal((0x41L, 0x42L), pick.Bytes);
        // The same bytes picked in the text on the right.
        var letters = text.IndexOf("@ABC", StringComparison.Ordinal);
        Assert.Equal("bytes 0x41-0x42", DocumentPick.InHex(text, letters + 1, letters + 3)!.Where);
        // Across the gap after the eighth byte, from a line's offset to the next line's start.
        var line1 = text.IndexOf("00000010", StringComparison.Ordinal);
        Assert.Equal("bytes 0x10-0x1F", DocumentPick.InHex(text, line1, text.IndexOf("00000020", StringComparison.Ordinal))!.Where);
        Assert.Equal("byte 0x18", DocumentPick.InHex(text, line1 + 35, line1 + 37)!.Where);
        Assert.Null(DocumentPick.InHex(text, 5, 5));
    }

    [Theory]
    [InlineData(0.8, 1.45, "0.80-1.45 s")]
    [InlineData(0.8, 0.85, "0.800-0.850 s")]
    [InlineData(61.5, 65.25, "1:01.50-1:05.25")]
    [InlineData(59.0, 61.0, "0:59.00-1:01.00")]
    public void A_stretch_of_a_sound_is_named_by_its_times(double from, double to, string label) => Assert.Equal(label, SoundRange.Label(from, to));

    [Theory]
    [InlineData(0.8124, 3, "0.812 s")]
    [InlineData(62.3456, 3, "1:02.346")]
    [InlineData(5, 0, "5 s")]
    public void Times_read_as_the_panel_shows_them(double seconds, int decimals, string text) => Assert.Equal(text, SoundRange.Clock(seconds, decimals));

    [Fact]
    public void The_model_is_told_a_stretch_to_the_sample_and_how_it_sounds_there()
    {
        // A second of silence, then a second of a loud tone, at 1 kHz.
        var mono = new float[2000];
        for (var i = 1000; i < 2000; i++)
            mono[i] = (float)(0.5 * Math.Sin(i * 0.3));
        var quiet = SoundRange.Told(mono, 1000, 0.2, 0.8);
        Assert.StartsWith("They picked 0.200-0.800 s of it (0.600 s long; samples 200-800 at 1,000 Hz", quiet);
        Assert.EndsWith("silent throughout", quiet);
        Assert.Contains("peak", SoundRange.Told(mono, 1000, 1.2, 1.9));
        using var picture = SoundRange.Picture(mono, 1000, 1.2, 1.9);
        Assert.Equal(1024, picture.Width);
    }

    [Fact]
    public void A_model_moved_aside_is_drawn_aside()
    {
        using var mesh = Mesh.Load(WriteCube());
        using var centred = MeshRenderer.Render(mesh, 64, 64, 0, 0, supersample: 1);
        using var right = MeshRenderer.Render(mesh, 64, 64, 0, 0, supersample: 1, panX: 0.3);
        // The model is lit grey over a dark background: its left edge moves right by about 0.3 of the side.
        static int LeftEdge(SkiaSharp.SKBitmap b) => Enumerable.Range(0, b.Width).First(x => b.GetPixel(x, b.Height / 2).Red > 100);
        Assert.InRange(LeftEdge(right) - LeftEdge(centred), 17, 21);
    }

    private string WriteCube()
    {
        var path = Path.Combine(_dir, "cube.obj");
        File.WriteAllText(path, """
            v -1 -1 1
            v 1 -1 1
            v 1 1 1
            v -1 1 1
            f 1 2 3
            f 1 3 4
            """);
        return path;
    }
}
