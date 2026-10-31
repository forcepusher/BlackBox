using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// The top bar's server status in a narrow window or beside a long Side panel name: each part is shown
/// whole or not at all, never cut short with "…" (the user, 2026-10-30, at "Conne…").
/// </summary>
public sealed class WholeRowTests
{
    /// <summary>
    /// Room for the first <paramref name="parts"/> parts plus <paramref name="extra"/>: those show in
    /// full, in their place, and the rest are past the row's right edge, where its clip hides them.
    /// </summary>
    [Theory]
    [InlineData(3, 0, 3)]
    [InlineData(2, 0, 2)]
    [InlineData(3, -1, 2)]
    [InlineData(2, -1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(0, 0, 1)]
    public void Each_part_is_whole_or_gone(int parts, double extra, int shown)
    {
        HeadlessApp.Run(() =>
        {
            Control[] all =
            [
                new Ellipse { Width = 9, Height = 9 },
                new TextBlock { Text = "Connected", Margin = new Thickness(8, 0, 0, 0) },
                new TextBlock { Text = "· 35k of 100k used", Margin = new Thickness(6, 0, 0, 0) },
            ];
            var row = new WholeRow();
            row.Children.AddRange(all);
            foreach (var part in all)
                part.Measure(Size.Infinity);
            var room = all.Take(parts).Sum(p => p.DesiredSize.Width) + extra;

            row.Measure(new Size(room, 40));
            row.Arrange(new Rect(0, 0, row.DesiredSize.Width, 40));

            Assert.True(row.ClipToBounds);
            double x = 0;
            for (var i = 0; i < all.Length; i++)
            {
                var bounds = all[i].Bounds;
                Assert.Equal(all[i].DesiredSize.Width - all[i].Margin.Left - all[i].Margin.Right, bounds.Width, 3);
                if (i < shown)
                    Assert.Equal(x + all[i].Margin.Left, bounds.X, 3);
                else
                    Assert.True(bounds.X >= row.Bounds.Width, $"part {i} at {bounds} shows in a row {row.Bounds.Width} wide");
                x += all[i].DesiredSize.Width;
            }
            return 0;
        });
    }
}
