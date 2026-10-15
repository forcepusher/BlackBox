using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BlackBox.Core.Documents;

/// <summary>
/// Replaces text in a Word document or a PowerPoint deck the way a person does in the program: the
/// rest of the document, and the formatting of the text where the change starts, stay as they were.
/// Word splits text into runs where nobody would see a break ("Con" "tract" after a spell check),
/// so the text is matched across runs, and across the paragraphs of one stretch of text (a table
/// cell, a text box, the body up to a table). The old text may be copied from read_file with its
/// "# ", "- " and ** marks, and straight quotes match curly ones. In the new text a line break
/// starts a new paragraph like the one it is in, "# " at the start of one makes it a heading, and
/// **bold** and *italic* are applied.
/// </summary>
internal static partial class TextEdit
{
    private static readonly XNamespace W = DocxWriter.W;
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>A stretch of paragraphs in a part where text can be matched: one after the other with nothing between.</summary>
    private sealed record Stretch(string Part, XDocument Xml, List<XElement> Paragraphs);

    private sealed record Hit(Stretch Stretch, int Start, int End);

    /// <summary>
    /// Makes the change in <paramref name="parts"/>; returns how many places changed and in which parts.
    /// Throws <see cref="InvalidOperationException"/> with what to tell the model when the text isn't
    /// there, or is there more than once without <paramref name="all"/>.
    /// </summary>
    public static (int Count, List<string> Parts) Replace(Dictionary<string, XDocument> parts, bool slides, string oldText, string newText, bool all)
    {
        var format = slides ? SlideFormat : WordFormat;
        var stretches = parts.SelectMany(p => Stretches(p.Key, p.Value, format)).ToList();

        List<Hit> hits = [];
        string? found = null;
        foreach (var candidate in Candidates(oldText))
        {
            hits = stretches.SelectMany(s => Find(s, candidate, format)).ToList();
            if (hits.Count > 0)
            {
                found = candidate;
                break;
            }
        }
        if (hits.Count == 0)
            throw new InvalidOperationException("old_text was not found in the document. Read it again with read_file and copy the text exactly; " +
                "a change must stay within one table cell, text box or run of paragraphs not broken by a table.");
        if (hits.Count > 1 && !all)
            throw new InvalidOperationException($"old_text occurs {hits.Count} times. Include more of the text around it to make it unique, or set replace_all to change every one.");

        // Found only without read_file's marks: the new text was copied with them too, and the
        // paragraphs already are the headings and list items the marks stand for.
        var replacement = newText.Replace("\r\n", "\n");
        if (found != oldText.Replace("\r\n", "\n"))
            replacement = Unmarked(replacement);
        // Bold, italic or code marked where the old text had none of it: the new text goes in with
        // that formatting. Otherwise only what differs is changed, and the rest keeps its own.
        var styled = !StyledParts(replacement).SequenceEqual(StyledParts(found!));
        // From the last to the first, so the places still to change stay where they were found.
        foreach (var hit in hits.OrderByDescending(h => h.Stretch.Part).ThenByDescending(h => h.Stretch.Paragraphs[0].ElementsBeforeSelf().Count()).ThenByDescending(h => h.Start))
        {
            if (styled)
            {
                Apply(hit.Stretch, hit.Start, hit.End, Lines(replacement, inline: true), format);
                continue;
            }
            var before = Fold(Text(hit.Stretch, format)[hit.Start..hit.End]);
            var plain = string.Join("\n", replacement.Split('\n').Select((line, i) => i == 0 ? MarkdownDocument.Plain(MarkdownDocument.Inline(line)) : line));
            var after = Fold(plain);
            var prefix = 0;
            while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
            var suffix = 0;
            while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
            if (prefix + suffix == before.Length && prefix + suffix == after.Length)
                continue; // nothing differs
            Apply(hit.Stretch, hit.Start + prefix, hit.End - suffix, Lines(plain[prefix..^suffix], inline: false), format);
        }
        return (hits.Count, hits.Select(h => h.Stretch.Part).Distinct().ToList());
    }

    /// <summary>The bold, italic, struck and code parts of a text, to tell whether the new text asks for formatting the old didn't have.</summary>
    private static IEnumerable<string> StyledParts(string text) =>
        text.Split('\n').SelectMany(MarkdownDocument.Inline).Where(s => s.Bold || s.Italic || s.Code || s.Strike)
            .Select(s => $"{s.Bold}{s.Italic}{s.Code}{s.Strike}:{s.Text}");

    /// <summary>The new text's lines: their spans, and for a line after the first, the heading level its "# " asks for.</summary>
    private static List<(IReadOnlyList<Span> Spans, int? Heading)> Lines(string text, bool inline) =>
        text.Split('\n').Select((line, i) =>
        {
            int? heading = i > 0 && HeadingMark().Match(line) is { Success: true } m ? m.Groups[1].Length : null;
            var rest = i > 0 ? HeadingMark().Replace(ListMark().Replace(line, ""), "") : line;
            return ((IReadOnlyList<Span>)(inline ? MarkdownDocument.Inline(rest) : [new Span(rest)]), heading);
        }).ToList();

    /// <summary>The text as given, then without read_file's "# ", "- " and ** marks.</summary>
    private static IEnumerable<string> Candidates(string text)
    {
        var plain = text.Replace("\r\n", "\n");
        yield return plain;
        var unmarked = Unmarked(plain);
        if (unmarked != plain) yield return unmarked;
    }

    private static string Unmarked(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => LineMark().Replace(line, "").Replace("**", "")));

    // ---- finding ---------------------------------------------------------------------------------------

    private static IEnumerable<Stretch> Stretches(string part, XDocument xml, Format format)
    {
        Stretch? current = null;
        foreach (var p in xml.Descendants(format.Paragraph).Where(p => !p.Ancestors(Mc + "Fallback").Any()))
        {
            if (current is not null && PreviousParagraph(p, format) == current.Paragraphs[^1])
                current.Paragraphs.Add(p);
            else
            {
                if (current is not null) yield return current;
                current = new Stretch(part, xml, [p]);
            }
        }
        if (current is not null) yield return current;
    }

    /// <summary>The paragraph right before this one, past bookmarks and other marks that hold no text; null after a table or the like.</summary>
    private static XElement? PreviousParagraph(XElement p, Format format)
    {
        for (var e = p.ElementsBeforeSelf().LastOrDefault(); e is not null; e = e.ElementsBeforeSelf().LastOrDefault())
        {
            if (e.Name == format.Paragraph) return e;
            if (!Marks.Contains(e.Name.LocalName)) return null;
        }
        return null;
    }

    private static readonly HashSet<string> Marks = ["bookmarkStart", "bookmarkEnd", "proofErr", "permStart", "permEnd", "commentRangeStart", "commentRangeEnd"];

    private static string Text(Stretch s, Format format) => string.Join("\n", s.Paragraphs.Select(p => string.Concat(format.Units(p).Select(format.UnitText))));

    private static IEnumerable<Hit> Find(Stretch stretch, string text, Format format)
    {
        var haystack = Fold(Text(stretch, format));
        var needle = Fold(text);
        if (needle.Length == 0) yield break;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            yield return new Hit(stretch, i, i + needle.Length);
    }

    /// <summary>Curly quotes, dashes and no-break spaces as their plain forms, one character for one, so positions stay.</summary>
    private static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c switch
            {
                '‘' or '’' or '‚' or '′' => '\'',
                '“' or '”' or '„' or '″' => '"',
                '–' or '—' or '‑' or '−' => '-',
                ' ' or ' ' => ' ',
                _ => c,
            });
        return sb.ToString();
    }

    // ---- changing --------------------------------------------------------------------------------------

    /// <summary>Replaces the stretch's text from <paramref name="from"/> to <paramref name="to"/> with <paramref name="lines"/>.</summary>
    private static void Apply(Stretch stretch, int from, int to, List<(IReadOnlyList<Span> Spans, int? Heading)> lines, Format format)
    {
        var (startPara, startOffset) = Locate(stretch, from, format);
        var (endPara, endOffset) = Locate(stretch, to, format);
        var first = stretch.Paragraphs[startPara];
        var last = stretch.Paragraphs[endPara];
        var wholeParagraphs = startOffset == 0 && endOffset == Length(last, format) && to > from;

        // Runs break where the change starts and ends, so whole runs go.
        var end = format.SplitAt(last, endOffset);
        var start = startPara == endPara && startOffset == endOffset ? end : format.SplitAt(first, startOffset);
        // The new text looks like the old text where it started; an insertion like the text before it.
        var before = format.Units(first).TakeWhile(u => u != start).LastOrDefault();
        var baseProperties = to > from && start is not null ? format.Properties(start)
            : before is not null ? format.Properties(before) : start is not null ? format.Properties(start) : null;

        // What goes: the end of the first paragraph (up to the end, in this one), whole paragraphs
        // between, the start of the last one, whose rest then joins the first.
        var doomed = new List<XElement>();
        if (startPara == endPara)
            doomed.AddRange(format.Units(first).SkipWhile(u => u != start).TakeWhile(u => u != end));
        else
        {
            doomed.AddRange(format.Units(first).SkipWhile(u => u != start));
            doomed.AddRange(format.Units(last).TakeWhile(u => u != end));
        }

        // A marker where the old text started (inside the same link, if it was in one): the new
        // text goes before it, and a paragraph the new text starts takes what follows it.
        var marker = new XElement("blackbox-insertion-point");
        if (start is not null && (startPara == endPara || start != end))
            start.AddBeforeSelf(marker);
        else
            format.Append(first, [marker]);
        marker.AddBeforeSelf(lines[0].Spans.SelectMany(s => format.NewRuns(s, baseProperties)));

        foreach (var unit in doomed)
            unit.Remove();
        foreach (var p in stretch.Paragraphs.Skip(startPara + 1).Take(endPara - startPara - 1))
            p.Remove();
        if (endPara != startPara)
        {
            format.MoveContent(last, first);
            last.Remove();
        }

        // Further lines are paragraphs after it, made like it; the last takes what followed the change.
        var added = new List<XElement>();
        var current = first;
        foreach (var (spans, heading) in lines.Skip(1))
        {
            var next = format.NewParagraph(first, heading);
            current.AddAfterSelf(next);
            added.Add(next);
            current = next;
        }
        if (added.Count > 0)
            format.MoveAfter(first, marker, added[^1]);
        for (var i = 0; i < added.Count; i++)
            format.Prepend(added[i], lines[i + 1].Spans.SelectMany(s => format.NewRuns(s, baseProperties)).ToList());
        marker.Remove();
        foreach (var p in added.Prepend(first))
            Prune(p, format);

        // A change that took whole paragraphs and put nothing in leaves no empty paragraph behind.
        if (lines.Count == 1 && MarkdownDocument.Plain(lines[0].Spans).Length == 0 && wholeParagraphs
            && format.Units(first).All(u => format.UnitText(u).Length == 0) && format.CanRemove(first))
            first.Remove();
    }

    private static int Length(XElement paragraph, Format format) => format.Units(paragraph).Sum(u => format.UnitText(u).Length);

    /// <summary>Paragraph and offset in it of a position in the stretch's text.</summary>
    private static (int Paragraph, int Offset) Locate(Stretch stretch, int position, Format format)
    {
        for (var i = 0; i < stretch.Paragraphs.Count; i++)
        {
            var length = Length(stretch.Paragraphs[i], format);
            if (position <= length) return (i, position);
            position -= length + 1;
        }
        return (stretch.Paragraphs.Count - 1, Length(stretch.Paragraphs[^1], format));
    }

    /// <summary>Links and the like that the change left empty.</summary>
    private static void Prune(XElement paragraph, Format format)
    {
        foreach (var empty in paragraph.Elements().Where(e => format.Containers.Contains(e.Name) && !e.Descendants().Any(d => format.IsUnit(d))).ToList())
            empty.Remove();
    }

    // ---- the two formats -------------------------------------------------------------------------------

    /// <summary>How paragraphs and runs look in Word or PowerPoint.</summary>
    private abstract class Format
    {
        public abstract XName Paragraph { get; }
        public abstract IReadOnlyCollection<XName> Containers { get; }
        public abstract bool IsUnit(XElement e);
        public abstract IEnumerable<XElement> Units(XElement paragraph);
        public abstract string UnitText(XElement unit);
        public abstract XElement? Properties(XElement unit);
        public abstract IEnumerable<XElement> NewRuns(Span span, XElement? properties);
        public abstract XElement NewParagraph(XElement like, int? heading);
        public abstract bool CanRemove(XElement paragraph);
        protected abstract XElement? SplitUnit(XElement unit, int count);
        protected abstract XName ParagraphProperties { get; }
        protected virtual bool IsEnd(XElement child) => false;

        /// <summary>Breaks the run at <paramref name="offset"/> into two; returns the unit that starts there, or null at the paragraph's end.</summary>
        public XElement? SplitAt(XElement paragraph, int offset)
        {
            var seen = 0;
            foreach (var unit in Units(paragraph).ToList())
            {
                var length = UnitText(unit).Length;
                if (seen == offset) return unit;
                if (seen + length > offset)
                    return SplitUnit(unit, offset - seen) ?? throw new InvalidOperationException("The change would start or end inside a field (a page number, a date); include all of it or none.");
                seen += length;
            }
            return null;
        }

        public void Append(XElement paragraph, IEnumerable<XElement> runs)
        {
            var end = paragraph.Elements().FirstOrDefault(IsEnd);
            if (end is not null) end.AddBeforeSelf(runs);
            else paragraph.Add(runs);
        }

        public void Prepend(XElement paragraph, IEnumerable<XElement> runs)
        {
            var properties = paragraph.Element(ParagraphProperties);
            if (properties is not null) properties.AddAfterSelf(runs);
            else paragraph.AddFirst(runs);
        }

        /// <summary>The content of <paramref name="from"/> (but its properties) to the end of <paramref name="to"/>.</summary>
        public void MoveContent(XElement from, XElement to)
        {
            var content = from.Elements().Where(e => e.Name != ParagraphProperties && !IsEnd(e)).ToList();
            foreach (var e in content) e.Remove();
            Append(to, content);
        }

        /// <summary>What follows <paramref name="after"/> in the paragraph moves to <paramref name="next"/>.</summary>
        public void MoveAfter(XElement paragraph, XElement? after, XElement next)
        {
            var top = after is null ? null : after.AncestorsAndSelf().First(e => e.Parent == paragraph);
            var rest = (top is null ? paragraph.Elements().Where(e => e.Name != ParagraphProperties) : top.ElementsAfterSelf()).Where(e => !IsEnd(e)).ToList();
            foreach (var e in rest) e.Remove();
            Append(next, rest);
        }
    }

    private static readonly Format WordFormat = new WordText();
    private static readonly Format SlideFormat = new SlideText();

    private sealed class WordText : Format
    {
        public override XName Paragraph => W + "p";
        protected override XName ParagraphProperties => W + "pPr";
        public override IReadOnlyCollection<XName> Containers { get; } = [W + "hyperlink", W + "smartTag", W + "ins", W + "fldSimple"];
        public override bool IsUnit(XElement e) => e.Name == W + "r";

        // The paragraph's own runs: not those of a paragraph inside it (a text box), nor deleted text, nor a fallback copy.
        public override IEnumerable<XElement> Units(XElement paragraph) =>
            paragraph.Descendants(W + "r").Where(r => r.Ancestors(W + "p").First() == paragraph
                && !r.Ancestors().Any(a => a.Name == W + "del" || a.Name == W + "moveFrom" || a.Name == Mc + "Fallback"));

        public override string UnitText(XElement unit) => string.Concat(unit.Elements().Select(Piece));

        private static string Piece(XElement e) =>
            e.Name == W + "t" ? e.Value
            : e.Name == W + "tab" ? "\t"
            : e.Name == W + "br" || e.Name == W + "cr" ? "\n"
            : e.Name == W + "noBreakHyphen" ? "-"
            : "";

        public override XElement? Properties(XElement unit) => unit.Element(W + "rPr");

        public override IEnumerable<XElement> NewRuns(Span span, XElement? properties) => DocxWriter.Runs(span with { Link = null }, properties);

        protected override XElement? SplitUnit(XElement unit, int count)
        {
            var rest = new XElement(unit.Name, unit.Attributes(), unit.Element(W + "rPr") is { } rPr ? new XElement(rPr) : null);
            var seen = 0;
            foreach (var child in unit.Elements().Where(e => e.Name != W + "rPr").ToList())
            {
                var length = Piece(child).Length;
                if (seen >= count)
                {
                    child.Remove();
                    rest.Add(child);
                }
                else if (child.Name == W + "t" && seen + length > count)
                {
                    var keep = count - seen;
                    rest.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), child.Value[keep..]));
                    child.Value = child.Value[..keep];
                    child.SetAttributeValue(XNamespace.Xml + "space", "preserve");
                }
                seen += length;
            }
            unit.AddAfterSelf(rest);
            return rest;
        }

        public override XElement NewParagraph(XElement like, int? heading)
        {
            var p = new XElement(W + "p");
            if (like.Element(W + "pPr") is { } pPr)
            {
                var copy = new XElement(pPr);
                copy.Element(W + "sectPr")?.Remove();
                p.Add(copy);
            }
            if (heading is { } level)
            {
                var properties = p.Element(W + "pPr") ?? new XElement(W + "pPr");
                if (properties.Parent is null) p.AddFirst(properties);
                properties.Element(W + "numPr")?.Remove();
                properties.Element(W + "pStyle")?.Remove();
                properties.AddFirst(new XElement(W + "pStyle", new XAttribute(W + "val", $"Heading{level}")));
            }
            return p;
        }

        // A table cell needs a paragraph, and so does the end of the body.
        public override bool CanRemove(XElement paragraph) =>
            paragraph.Parent is { } parent && parent.Elements(W + "p").Count() > 1 && paragraph.Element(W + "pPr")?.Element(W + "sectPr") is null;
    }

    private sealed class SlideText : Format
    {
        public override XName Paragraph => A + "p";
        protected override XName ParagraphProperties => A + "pPr";
        public override IReadOnlyCollection<XName> Containers { get; } = [];
        public override bool IsUnit(XElement e) => e.Name == A + "r" || e.Name == A + "br" || e.Name == A + "fld";
        protected override bool IsEnd(XElement child) => child.Name == A + "endParaRPr";

        public override IEnumerable<XElement> Units(XElement paragraph) => paragraph.Elements().Where(IsUnit);

        public override string UnitText(XElement unit) => unit.Name == A + "br" ? "\n" : (string?)unit.Element(A + "t") ?? "";

        public override XElement? Properties(XElement unit) => unit.Element(A + "rPr");

        public override IEnumerable<XElement> NewRuns(Span span, XElement? properties)
        {
            var lines = span.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var rPr = properties is null ? new XElement(A + "rPr", new XAttribute("lang", "en-US")) : new XElement(properties);
                rPr.Elements(A + "hlinkClick").Remove();
                if (span.Bold) rPr.SetAttributeValue("b", 1);
                if (span.Italic) rPr.SetAttributeValue("i", 1);
                if (span.Strike) rPr.SetAttributeValue("strike", "sngStrike");
                if (i > 0) yield return new XElement(A + "br", new XElement(rPr));
                if (lines[i].Length > 0) yield return new XElement(A + "r", rPr, new XElement(A + "t", lines[i]));
            }
        }

        protected override XElement? SplitUnit(XElement unit, int count)
        {
            if (unit.Name != A + "r") return null;
            var t = unit.Element(A + "t")!;
            var rest = new XElement(A + "r", unit.Element(A + "rPr") is { } rPr ? new XElement(rPr) : null, new XElement(A + "t", t.Value[count..]));
            t.Value = t.Value[..count];
            unit.AddAfterSelf(rest);
            return rest;
        }

        public override XElement NewParagraph(XElement like, int? heading)
        {
            var p = new XElement(A + "p");
            if (like.Element(A + "pPr") is { } pPr) p.Add(new XElement(pPr));
            if (like.Element(A + "endParaRPr") is { } end) p.Add(new XElement(end));
            return p;
        }

        // A text box needs a paragraph.
        public override bool CanRemove(XElement paragraph) => paragraph.Parent is { } parent && parent.Elements(A + "p").Count() > 1;
    }

    [GeneratedRegex(@"^(\s*(#{1,6}|[-*+•]|\d{1,3}[.)])\s+)")]
    private static partial Regex LineMark();

    [GeneratedRegex(@"^\s*(#{1,6})\s+")]
    private static partial Regex HeadingMark();

    [GeneratedRegex(@"^\s*[-*+•]\s+")]
    private static partial Regex ListMark();
}
