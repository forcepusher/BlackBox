using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>An SVG in File inspector: drawn as shapes, so it stays sharp zoomed in, with its source under Text; Text and Area for other files.</summary>
public sealed class SvgInspectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-svg-panel-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void An_svg_is_drawn_sharp_at_any_zoom_and_its_source_is_under_text()
    {
        // A yellow square on dark grey, its left edge half way across a unit: zoomed 8 times, that edge
        // must go straight from one colour to the other, half way across the 8 pixels the unit covers.
        // A picture of it stretched shows the unit's half-yellow blend over all 8 (or blurs it).
        var path = Path.Combine(_dir, "square.svg");
        File.WriteAllText(path, """
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="40">
              <rect width="40" height="40" fill="#202020"/>
              <rect x="10.5" y="10" width="19.5" height="20" fill="#FCC21B"/>
            </svg>
            """);
        HeadlessApp.Run(() =>
        {
            var vm = new FileInspectorViewModel(new PreviewServices(null, () => null));
            vm.Show(path);
            for (var i = 0; i < 200 && !vm.IsDrawing; i++)
            {
                Thread.Sleep(10);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.True(vm.IsDrawing);
            Assert.StartsWith("SVG 40x40, ", vm.Details);

            var window = new Window { Width = 432, Height = 432, Background = Brushes.Black, Content = new FileInspectorView { DataContext = vm } };
            window.Show();
            var box = window.GetVisualDescendants().OfType<ZoomBox>().First(b => b.Name == "PictureBox");
            Assert.True(box.IsVisible);

            vm.Zoom = 8;
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame()!;
            var pixels = Pixels(frame);
            // The box's middle is the drawing's middle (20, 20); 8x zoom puts the edge 76 DIPs left of it.
            var middle = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window)!.Value;
            var row = (int)middle.Y;
            var edge = (int)Math.Round(middle.X - 76);
            Assert.Equal(0xFCC21B, pixels(edge + 1, row));
            Assert.Equal(0x202020, pixels(edge - 2, row));

            vm.ShowText = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(box.IsVisible);
            Assert.Contains("<rect x=\"10.5\" y=\"10\"", vm.Text);
            Assert.False(vm.CanZoom);
            vm.ShowText = false;
            Assert.Null(vm.Text);
            Assert.True(vm.CanZoom);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public void Text_shows_a_markdown_file_as_written_and_is_greyed_for_code()
    {
        var md = Path.Combine(_dir, "notes.md");
        File.WriteAllText(md, "# Notes\n\n- one\n");
        var code = Path.Combine(_dir, "tool.ps1");
        File.WriteAllText(code, "Write-Host hi\n");
        HeadlessApp.Run(() =>
        {
            var vm = new FileInspectorViewModel(new PreviewServices(null, () => null));
            Assert.False(vm.CanShowText);
            Assert.Equal("Open a file first", vm.TextTip);

            Load(vm, md, () => vm.Markdown is not null);
            Assert.True(vm.CanShowText);
            vm.ShowText = true;
            Assert.Equal("# Notes\n\n- one\n", vm.Text);
            Assert.False(vm.ShowsMarkdown);
            vm.ShowText = false;
            Assert.Null(vm.Text);
            Assert.True(vm.ShowsMarkdown);

            Load(vm, code, () => vm.Text is not null);
            Assert.False(vm.CanShowText);
            Assert.Equal("Shown as text already", vm.TextTip);
            return 0;
        });
    }

    private static void Load(FileInspectorViewModel vm, string path, Func<bool> loaded)
    {
        vm.Show(path);
        for (var i = 0; i < 200 && !loaded(); i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(loaded());
    }

    /// <summary>A frame's pixels as 0xRRGGBB by x and y.</summary>
    private static Func<int, int, int> Pixels(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var (height, stride, bgra) = (buffer.Size.Height, buffer.RowBytes, buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888);
        var bytes = new byte[stride * height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return (x, y) =>
        {
            var i = y * stride + x * 4;
            var (r, b) = bgra ? (bytes[i + 2], bytes[i]) : (bytes[i], bytes[i + 2]);
            return r << 16 | bytes[i + 1] << 8 | b;
        };
    }
}
