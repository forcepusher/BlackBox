using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>File inspector's bar under the file, as the Code editor's: the zoom buttons there for every file, greyed with why where they don't apply.</summary>
public sealed class InspectorBarTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-inspector-bar-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_zoom_buttons_stay_in_the_bar_and_grey_where_they_dont_apply()
    {
        var text = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(text, "one\ntwo\n");
        var svg = Path.Combine(_dir, "square.svg");
        File.WriteAllText(svg, """<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40"><rect width="40" height="40" fill="#202020"/></svg>""");
        HeadlessApp.Run(() =>
        {
            var vm = new FileInspectorViewModel(new PreviewServices(null, () => null));
            var window = new Window { Width = 432, Height = 432, Background = Brushes.Black, Content = new FileInspectorView { DataContext = vm } };
            window.Show();
            Button Named(string name) => window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
            var zoomOut = Named("Zoom out");
            var fit = Named("Zoom to fit");
            var zoomIn = Named("Zoom in");
            var where = zoomIn.TranslatePoint(new Point(0, 0), window);

            // No file: there, greyed, saying why.
            Assert.All([zoomOut, fit, zoomIn], b => Assert.True(b.IsVisible));
            Assert.All([zoomOut, fit, zoomIn], b => Assert.False(b.IsEffectivelyEnabled));
            Assert.Equal("Open a file first", ToolTip.GetTip(zoomIn));
            Assert.Equal("Open a file first", vm.PromptTip);

            // A text doesn't zoom.
            vm.Show(text);
            Wait(() => vm.Text is not null);
            Assert.False(zoomIn.IsEffectivelyEnabled);
            Assert.Equal("This file doesn't zoom", ToolTip.GetTip(zoomIn));
            Assert.Equal("This file doesn't say how it was made", vm.PromptTip);

            // A picture does: in, then out and back to fit once zoomed.
            vm.Show(svg);
            Wait(() => vm.CanZoom);
            Assert.True(zoomIn.IsEffectivelyEnabled);
            Assert.False(zoomOut.IsEffectivelyEnabled);
            Assert.False(fit.IsEffectivelyEnabled);
            Assert.Equal("Zoom in", ToolTip.GetTip(zoomIn));
            vm.ZoomInCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(zoomOut.IsEffectivelyEnabled);
            Assert.True(fit.IsEffectivelyEnabled);
            vm.ZoomFitCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(fit.IsEffectivelyEnabled);

            // The buttons never moved.
            Assert.Equal(where, zoomIn.TranslatePoint(new Point(0, 0), window));
            // Prompt metadata is the panel's (it has a chat): not in the full-screen viewer's view.
            Assert.False(window.GetVisualDescendants().OfType<ToggleButton>().Single(b => AutomationProperties.GetName(b) == "Prompt metadata").IsVisible);
            window.Close();
            return 0;
        });
    }

    private static void Wait(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }
}
