namespace Sloperator.Core.Diffs;

/// <summary>
/// A line-by-line diff of two texts as unified-diff hunks, for comparing two files or folders,
/// which needs no git. Myers' algorithm, after the lines the two share at the start and the end
/// are set aside.
/// </summary>
internal static class LineDiff
{
    /// <summary>
    /// The most edits looked for between the two: past it, the part between the shared start and
    /// end is shown as removed and added whole. The search keeps about D² numbers (2,000: 16 MB).
    /// </summary>
    private const int MaxEdits = 2_000;

    /// <summary>The lines of a text, whatever its line endings; a last line break makes no empty line after it.</summary>
    public static string[] Lines(string text)
    {
        if (text.Length == 0)
            return [];
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>
    /// The hunks that turn <paramref name="a"/> into <paramref name="b"/>: an @@ line each, then
    /// its lines, with <paramref name="context"/> unchanged lines around each change; empty when
    /// the two are the same.
    /// </summary>
    public static List<DiffLine> Hunks(string[] a, string[] b, int context)
    {
        var ops = Script(a, b);
        var hunks = new List<DiffLine>();
        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == ' ')
            {
                i++;
                continue;
            }
            // From context lines before this change to context lines after the last change that
            // comes within twice the context of the one before it.
            var from = Math.Max(0, i - context);
            var last = i;
            for (var j = i + 1; j < ops.Count && j <= last + 2 * context; j++)
                if (ops[j].Kind != ' ')
                    last = j;
            var to = Math.Min(ops.Count - 1, last + context);

            int aCount = 0, bCount = 0;
            var body = new List<DiffLine>();
            for (var j = from; j <= to; j++)
            {
                var (kind, ai, bi) = ops[j];
                body.Add(kind switch
                {
                    '+' => new DiffLine(DiffLineKind.Added, null, bi + 1, DiffLine.Cut(b[bi])),
                    '-' => new DiffLine(DiffLineKind.Removed, ai + 1, null, DiffLine.Cut(a[ai])),
                    _ => new DiffLine(DiffLineKind.Context, ai + 1, bi + 1, DiffLine.Cut(a[ai])),
                });
                if (kind != '+') aCount++;
                if (kind != '-') bCount++;
            }
            hunks.Add(new DiffLine(DiffLineKind.Hunk, null, null, $"@@ -{Range(ops[from].A, aCount)} +{Range(ops[from].B, bCount)} @@"));
            hunks.AddRange(body);
            i = to + 1;
        }
        return hunks;
    }

    /// <summary>Each run of changed lines that turns <paramref name="a"/> into <paramref name="b"/>, with no unchanged lines around it.</summary>
    public static List<ChangeBlock> Blocks(string[] a, string[] b)
    {
        var ops = Script(a, b);
        var blocks = new List<ChangeBlock>();
        for (var i = 0; i < ops.Count;)
        {
            if (ops[i].Kind == ' ')
            {
                i++;
                continue;
            }
            var (start, removed, added) = (i, 0, 0);
            for (; i < ops.Count && ops[i].Kind != ' '; i++)
                if (ops[i].Kind == '-') removed++;
                else added++;
            blocks.Add(new ChangeBlock(ops[start].A, removed, ops[start].B, added));
        }
        return blocks;
    }

    /// <summary>As unified diffs write it: 1-based, the line before the hunk when it has none.</summary>
    private static string Range(int start, int count) => count == 1 ? $"{start + 1}" : $"{(count == 0 ? start : start + 1)},{count}";

    /// <summary>Every line of both in order: (' ', a, b) kept, ('-', a, b) taken out of a, ('+', a, b) put in from b; a and b where each text stands.</summary>
    private static List<(char Kind, int A, int B)> Script(string[] a, string[] b)
    {
        // Lines as numbers, so the search compares ints.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string line) => ids.TryGetValue(line, out var id) ? id : ids[line] = ids.Count;
        var x = a.Select(Id).ToArray();
        var y = b.Select(Id).ToArray();

        int prefix = 0, suffix = 0;
        while (prefix < x.Length && prefix < y.Length && x[prefix] == y[prefix])
            prefix++;
        while (suffix < x.Length - prefix && suffix < y.Length - prefix && x[^(suffix + 1)] == y[^(suffix + 1)])
            suffix++;

        var ops = new List<(char, int, int)>(a.Length + b.Length);
        for (var i = 0; i < prefix; i++)
            ops.Add((' ', i, i));
        foreach (var (kind, ai, bi) in Middle(x[prefix..^suffix], y[prefix..^suffix]))
            ops.Add((kind, ai + prefix, bi + prefix));
        for (var i = suffix; i > 0; i--)
            ops.Add((' ', a.Length - i, b.Length - i));
        return ops;
    }

    private static List<(char Kind, int A, int B)> Middle(int[] a, int[] b)
    {
        int n = a.Length, m = b.Length, max = n + m, off = max + 1;
        var ops = new List<(char, int, int)>();
        if (max == 0)
            return ops;
        var v = new int[2 * max + 3];
        // trace[d] is v as it was before step d, for k from -(d+1) to d+1: all that going back
        // from step d reads.
        var trace = new List<int[]>();
        var edits = -1;
        for (var d = 0; d <= Math.Min(max, MaxEdits) && edits < 0; d++)
        {
            trace.Add(v.AsSpan(off - d - 1, 2 * d + 3).ToArray());
            for (var k = -d; k <= d; k += 2)
            {
                var i = k == -d || k != d && v[off + k - 1] < v[off + k + 1] ? v[off + k + 1] : v[off + k - 1] + 1;
                var j = i - k;
                while (i < n && j < m && a[i] == b[j])
                {
                    i++;
                    j++;
                }
                v[off + k] = i;
                if (i >= n && j >= m)
                {
                    edits = d;
                    break;
                }
            }
        }
        if (edits < 0)
        {
            // Too different to search through: all of one out, all of the other in.
            for (var i = 0; i < n; i++) ops.Add(('-', i, 0));
            for (var j = 0; j < m; j++) ops.Add(('+', n, j));
            return ops;
        }

        int px = n, py = m;
        for (var d = edits; d > 0; d--)
        {
            var step = trace[d];
            int Get(int k) => step[k + d + 1];
            var k = px - py;
            var prevK = k == -d || k != d && Get(k - 1) < Get(k + 1) ? k + 1 : k - 1;
            var prevX = Get(prevK);
            var prevY = prevX - prevK;
            while (px > prevX && py > prevY)
            {
                px--;
                py--;
                ops.Add((' ', px, py));
            }
            if (px == prevX)
                ops.Add(('+', px, prevY));
            else
                ops.Add(('-', prevX, py));
            (px, py) = (prevX, prevY);
        }
        while (px > 0 && py > 0)
        {
            px--;
            py--;
            ops.Add((' ', px, py));
        }
        ops.Reverse();
        return ops;
    }
}
