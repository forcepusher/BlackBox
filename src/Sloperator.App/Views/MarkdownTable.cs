using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sloperator.Core.Chats;

namespace Sloperator.App.Views;

/// <summary>
/// A Markdown table in a reply, as a grid: lines between its cells, its header row on a raised
/// background, each cell's text wrapping inside its own column in the chat's font. Columns are as
/// wide as their text when the table fits; when it doesn't, each gets at least its longest word and
/// the rest of the room goes to the columns with the most text. A table that doesn't fit even so
/// scrolls sideways, with the full scrollbar under its last row.
/// </summary>
public sealed class MarkdownTable : Border
{
    private readonly TableGrid _grid = new();

    // Styled as the Border it is: a style's type matches only the exact type.
    protected override Type StyleKeyOverride => typeof(Border);

    public MarkdownTable(MdTable table)
    {
        Classes.Add("mdTable");
        HorizontalAlignment = HorizontalAlignment.Left;
        // The header's background stays inside the rounded corners.
        ClipToBounds = true;
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _grid,
        };
        Show(table);
    }

    public void Show(MdTable table) => _grid.Show(table);

    /// <summary>The grid's cells, row by row (for tests and UI Automation checks).</summary>
    public IReadOnlyList<SelectableTextBlock> Cells => _grid.Children.OfType<SelectableTextBlock>().ToList();

    /// <summary>
    /// The scroller measures the grid as if it had all the width it wants: the grid gets the room
    /// the reply has here, to fit its columns to.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        _grid.Room = Math.Max(0, availableSize.Width - BorderThickness.Left - BorderThickness.Right);
        return base.MeasureOverride(availableSize);
    }

    private sealed class TableGrid : Panel
    {
        /// <summary>A column needn't be wider than this for its longest word: a long address or path breaks.</summary>
        private const double LongestWord = 160;

        /// <summary>Room under the last row for the sideways scrollbar, which Fluent lays over the content.</summary>
        private const double ScrollBarRoom = 16;

        private static readonly Thickness CellPadding = new(12, 4);
        private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,monospace");

        private MdTable _table = new([], 0, []);
        private double[] _widths = [];
        private double[] _heights = [];
        private double[]? _mins;
        private double _room = double.PositiveInfinity;
        private readonly Lines _lines;

        public TableGrid() => _lines = new Lines(this);

        public double Room
        {
            get => _room;
            set
            {
                if (Math.Abs(value - _room) < 0.01) return;
                _room = value;
                InvalidateMeasure();
            }
        }

        public void Show(MdTable table)
        {
            if (Children.Count == 0)
                Children.Add(_lines);
            var count = table.Rows.Count * table.Columns + 1;
            while (Children.Count > count)
                Children.RemoveAt(Children.Count - 1);
            while (Children.Count < count)
                Children.Add(new SelectableText { TextWrapping = TextWrapping.Wrap, LineHeight = 20, Padding = CellPadding });
            for (var r = 0; r < table.Rows.Count; r++)
                for (var c = 0; c < table.Columns; c++)
                {
                    var cell = (SelectableTextBlock)Children[r * table.Columns + c + 1];
                    cell.TextAlignment = table.Align[c] switch { 1 => TextAlignment.Right, 0 => TextAlignment.Center, _ => TextAlignment.Left };
                    MarkdownText.SetSpans(cell, table.Rows[r][c]);
                }
            _table = table;
            _mins = null;
            InvalidateMeasure();
            _lines.InvalidateVisual();
        }

        private SelectableTextBlock Cell(int row, int column) => (SelectableTextBlock)Children[row * _table.Columns + column + 1];

        protected override Size MeasureOverride(Size availableSize)
        {
            int rows = _table.Rows.Count, columns = _table.Columns;
            if (rows == 0 || columns == 0)
                return default;
            _mins ??= Enumerable.Range(0, columns)
                .Select(c => Enumerable.Range(0, rows).Max(r => Math.Min(LongestWord, LongestWordIn(Cell(r, c), _table.Rows[r][c]))) + CellPadding.Left + CellPadding.Right)
                .ToArray();
            var maxes = new double[columns];
            for (var c = 0; c < columns; c++)
                for (var r = 0; r < rows; r++)
                {
                    var cell = Cell(r, c);
                    cell.Measure(Size.Infinity);
                    maxes[c] = Math.Max(maxes[c], cell.DesiredSize.Width);
                }

            // Widths in whole multiples of 4, which are whole pixels at the usual scales: a cell isn't
            // laid out a fraction narrower than it was measured and wrapped once more.
            var room = double.IsFinite(Room) ? Room : availableSize.Width;
            var mins = _mins.Select(m => Math.Ceiling(Math.Min(m, double.MaxValue) / 4) * 4).ToArray();
            var wants = maxes.Select((m, c) => Math.Max(mins[c], Math.Ceiling(m / 4) * 4)).ToArray();
            if (!double.IsFinite(room) || wants.Sum() <= room)
                _widths = wants;
            else if (mins.Sum() >= room)
                _widths = mins;
            else
            {
                // What is left over the longest words goes to each column by how much more it wants.
                var spare = room - mins.Sum();
                var more = wants.Sum() - mins.Sum();
                _widths = mins.Select((m, c) => m + Math.Floor((wants[c] - m) * spare / more / 4) * 4).ToArray();
            }

            _heights = new double[rows];
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < columns; c++)
                {
                    var cell = Cell(r, c);
                    cell.Measure(new Size(_widths[c], double.PositiveInfinity));
                    _heights[r] = Math.Max(_heights[r], cell.DesiredSize.Height);
                }
            _lines.Measure(Size.Infinity);
            var width = _widths.Sum();
            var scrolls = double.IsFinite(room) && width > room + 0.5;
            return new Size(width, _heights.Sum() + (scrolls ? ScrollBarRoom : 0));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (_widths.Length != _table.Columns || _heights.Length != _table.Rows.Count)
                return finalSize;
            _lines.Arrange(new Rect(finalSize));
            _lines.InvalidateVisual();
            var y = 0.0;
            for (var r = 0; r < _heights.Length; r++)
            {
                var x = 0.0;
                for (var c = 0; c < _widths.Length; c++)
                {
                    Cell(r, c).Arrange(new Rect(x, y, _widths[c], _heights[r]));
                    x += _widths[c];
                }
                y += _heights[r];
            }
            return finalSize;
        }

        /// <summary>The lines between the cells and the header's background, under the cells' text.</summary>
        private sealed class Lines(TableGrid grid) : Control
        {
            public override void Render(DrawingContext context) => grid.DrawLines(context);
        }

        private void DrawLines(DrawingContext context)
        {
            if (_widths.Length != _table.Columns || _heights.Length != _table.Rows.Count || _heights.Length == 0)
                return;
            // Lines one device pixel thick, on whole pixels, as the box's own border is.
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var thin = 1 / scale;
            double Snap(double v) => Math.Round(v * scale) / scale;
            var width = _widths.Sum();
            var height = _heights.Sum();
            var header = _heights.Take(_table.HeaderRows).Sum();
            if (header > 0 && Brush("BbSurfaceRaised") is { } raised)
                context.FillRectangle(raised, new Rect(0, 0, width, Snap(header)));
            if (Brush("BbBorder") is not { } line)
                return;
            var y = 0.0;
            for (var r = 0; r < _heights.Length - 1; r++)
            {
                y += _heights[r];
                context.FillRectangle(line, new Rect(0, Snap(y), width, thin));
            }
            var x = 0.0;
            for (var c = 0; c < _widths.Length - 1; c++)
            {
                x += _widths[c];
                context.FillRectangle(line, new Rect(Snap(x), 0, thin, height));
            }
        }

        private static IBrush? Brush(string key) =>
            Application.Current?.TryFindResource(key, out var value) == true ? value as IBrush : null;

        /// <summary>How wide a cell's widest word is, in its runs' fonts: a word can run over runs ("**May**,").</summary>
        private static double LongestWordIn(TextBlock cell, List<MdSpan> spans)
        {
            double longest = 0, word = 0;
            foreach (var span in spans)
            {
                var code = span.Style.HasFlag(MdStyle.Code);
                var typeface = new Typeface(code ? Mono : cell.FontFamily,
                    span.Style.HasFlag(MdStyle.Italic) ? FontStyle.Italic : FontStyle.Normal,
                    span.Style.HasFlag(MdStyle.Bold) ? FontWeight.SemiBold : cell.FontWeight);
                var size = code ? cell.FontSize - 1 : cell.FontSize;
                var start = 0;
                for (var i = 0; i <= span.Text.Length; i++)
                {
                    if (i < span.Text.Length && !char.IsWhiteSpace(span.Text[i]))
                        continue;
                    if (i > start)
                    {
                        word += new FormattedText(span.Text[start..i], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, null).Width;
                        longest = Math.Max(longest, word);
                    }
                    if (i < span.Text.Length)
                        word = 0;
                    start = i + 1;
                }
            }
            return longest;
        }
    }
}
