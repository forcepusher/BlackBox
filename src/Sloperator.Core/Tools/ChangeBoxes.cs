using SkiaSharp;

namespace Sloperator.Core.Tools;

/// <summary>
/// Where two pictures of the same size differ, as a few boxes: what capture_result marks on the
/// last of two compared pictures, so a one-cell change in a big sheet can be found at a glance.
/// Any pixel that differs counts; changed pixels are gathered in cells, and cells close together
/// make one box.
/// </summary>
internal static class ChangeBoxes
{
    /// <summary>The side of a cell, in pixels.</summary>
    private const int Cell = 16;

    /// <summary>Cells with up to this many unchanged cells between them make one box: a word's letters, a row's cells.</summary>
    private const int Join = 1;

    /// <summary>The most boxes, the closest put together past it: a scrolled page or edits all over gave dozens.</summary>
    private const int MaxBoxes = 6;

    /// <summary>Room left around what changed, in pixels, so the box doesn't sit on it.</summary>
    private const int Pad = 3;

    /// <summary>
    /// The boxes around what differs between two pictures of <paramref name="width"/>x<paramref name="height"/>
    /// in BGRA rows without padding, top to bottom; none when they are the same.
    /// </summary>
    public static List<SKRectI> Find(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int width, int height)
    {
        var (cols, rows) = ((width + Cell - 1) / Cell, (height + Cell - 1) / Cell);
        var changed = new bool[cols * rows];
        // Each cell's changed pixels' own bounds, so a box is as tight as what changed.
        var bounds = new SKRectI[cols * rows];
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                if (before[i] == after[i] && before[i + 1] == after[i + 1] && before[i + 2] == after[i + 2])
                    continue;
                var c = y / Cell * cols + x / Cell;
                var pixel = new SKRectI(x, y, x + 1, y + 1);
                bounds[c] = changed[c] ? Union(bounds[c], pixel) : pixel;
                changed[c] = true;
            }
        }

        // Cells close together, as one box each.
        var seen = new bool[cols * rows];
        var boxes = new List<SKRectI>();
        var queue = new Queue<int>();
        for (var start = 0; start < changed.Length; start++)
        {
            if (seen[start] || !changed[start])
                continue;
            seen[start] = true;
            queue.Enqueue(start);
            var box = bounds[start];
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                box = Union(box, bounds[c]);
                var (cx, cy) = (c % cols, c / cols);
                for (var dy = -Join - 1; dy <= Join + 1; dy++)
                    for (var dx = -Join - 1; dx <= Join + 1; dx++)
                    {
                        var (nx, ny) = (cx + dx, cy + dy);
                        if (nx < 0 || ny < 0 || nx >= cols || ny >= rows)
                            continue;
                        var n = ny * cols + nx;
                        if (seen[n] || !changed[n])
                            continue;
                        seen[n] = true;
                        queue.Enqueue(n);
                    }
            }
            boxes.Add(box);
        }

        // Boxes that overlap or nearly touch are one, and past the most, the pair that grows least together.
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
