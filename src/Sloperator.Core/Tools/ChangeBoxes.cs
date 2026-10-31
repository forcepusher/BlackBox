using SkiaSharp;

namespace Sloperator.Core.Tools;

/// <summary>
/// Where two pictures of the same size differ, as a few boxes: what capture_result marks on the
/// After of a before and after, so a one-cell change in a big sheet can be found at a glance. A
/// pixel changes as a click's check counts it (<see cref="ClickCheck.Threshold"/>); changed pixels
/// are gathered in cells, cells close together make one region, and a region needs more changed
/// pixels than a blinking caret or a clock's digit has, so neither gets a box.
/// </summary>
internal static class ChangeBoxes
{
    /// <summary>The side of a cell, in pixels.</summary>
    private const int Cell = 16;

    /// <summary>Changed pixels a cell needs: fewer is the fringe of antialiased text.</summary>
    private const int CellPixels = 4;

    /// <summary>
    /// Changed pixels a region needs: a caret (about 2x20) or a clock's digit has some 40, a word
    /// typed into a cell some hundred.
    /// </summary>
    private const int RegionPixels = 80;

    /// <summary>Cells with up to this many empty cells between them make one region: a word's letters, a row's cells.</summary>
    private const int Join = 1;

    /// <summary>The most boxes: past that the closest are put together.</summary>
    private const int MaxBoxes = 6;

    /// <summary>Room left around what changed, in pixels, so the box doesn't sit on it.</summary>
    private const int Pad = 3;

    /// <summary>
    /// The boxes around what differs between two pictures of <paramref name="width"/>x<paramref name="height"/>
    /// in BGRA rows without padding, top to bottom; none when nothing visible changed. <paramref name="changed"/>
    /// is how many pixels changed at all: some, with no box, is a change too small to mark (a caret, a digit).
    /// </summary>
    public static List<SKRectI> Find(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int width, int height, out int changed)
    {
        changed = 0;
        var (cols, rows) = ((width + Cell - 1) / Cell, (height + Cell - 1) / Cell);
        var counts = new int[cols * rows];
        // Each cell's changed pixels' own bounds, so a box is as tight as what changed.
        var bounds = new SKRectI[cols * rows];
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                if (Math.Abs(before[i] - after[i]) <= ClickCheck.Threshold && Math.Abs(before[i + 1] - after[i + 1]) <= ClickCheck.Threshold
                    && Math.Abs(before[i + 2] - after[i + 2]) <= ClickCheck.Threshold)
                    continue;
                changed++;
                var c = y / Cell * cols + x / Cell;
                bounds[c] = counts[c]++ == 0 ? new SKRectI(x, y, x + 1, y + 1) : Union(bounds[c], new SKRectI(x, y, x + 1, y + 1));
            }
        }

        // Cells close together, as regions.
        var seen = new bool[cols * rows];
        var regions = new List<(SKRectI Box, int Pixels)>();
        var queue = new Queue<int>();
        for (var start = 0; start < counts.Length; start++)
        {
            if (seen[start] || counts[start] < CellPixels)
                continue;
            seen[start] = true;
            queue.Enqueue(start);
            var (box, pixels) = (bounds[start], 0);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                (box, pixels) = (Union(box, bounds[c]), pixels + counts[c]);
                var (cx, cy) = (c % cols, c / cols);
                for (var dy = -Join - 1; dy <= Join + 1; dy++)
                    for (var dx = -Join - 1; dx <= Join + 1; dx++)
                    {
                        var (nx, ny) = (cx + dx, cy + dy);
                        if (nx < 0 || ny < 0 || nx >= cols || ny >= rows)
                            continue;
                        var n = ny * cols + nx;
                        if (seen[n] || counts[n] < CellPixels)
                            continue;
                        seen[n] = true;
                        queue.Enqueue(n);
                    }
            }
            if (pixels >= RegionPixels)
                regions.Add((box, pixels));
        }

        // Boxes that overlap or nearly touch are one, and past the most, the pair that grows least together.
        var boxes = regions.Select(r => r.Box).ToList();
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < boxes.Count && !merged; i++)
                for (var j = i + 1; j < boxes.Count && !merged; j++)
                    if (Near(boxes[i], boxes[j]))
                    {
                        (boxes[i], merged) = (Union(boxes[i], boxes[j]), true);
                        boxes.RemoveAt(j);
                    }
        }
        while (boxes.Count > MaxBoxes)
        {
            var (best, bi, bj) = (long.MaxValue, 0, 1);
            for (var i = 0; i < boxes.Count; i++)
                for (var j = i + 1; j < boxes.Count; j++)
                    if (Area(Union(boxes[i], boxes[j])) - Area(boxes[i]) - Area(boxes[j]) is var grows && grows < best)
                        (best, bi, bj) = (grows, i, j);
            boxes[bi] = Union(boxes[bi], boxes[bj]);
            boxes.RemoveAt(bj);
        }
        return boxes
            .Select(b => new SKRectI(Math.Max(0, b.Left - Pad), Math.Max(0, b.Top - Pad), Math.Min(width, b.Right + Pad), Math.Min(height, b.Bottom + Pad)))
            .OrderBy(b => b.Top).ThenBy(b => b.Left).ToList();
    }

    private static SKRectI Union(SKRectI a, SKRectI b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private static long Area(SKRectI r) => (long)r.Width * r.Height;

    /// <summary>Overlapping, or less than a cell apart.</summary>
    private static bool Near(SKRectI a, SKRectI b) =>
        a.Left - Cell < b.Right && b.Left - Cell < a.Right && a.Top - Cell < b.Bottom && b.Top - Cell < a.Bottom;
}
