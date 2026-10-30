using Avalonia.Controls;
using Avalonia.Media.TextFormatting;

namespace Sloperator.App.Views;

/// <summary>
/// Selectable text whose selected part keeps each run's own font, size, weight and lines, only its
/// colour changing to the selection's. Avalonia's (11.3) redraws a selection of runs with one run's
/// font at the block's size: dragging over a reply turned everything selected into the code font,
/// a size up, and the words after it moved along (the user, 2026-10-30, "the selected text changes
/// size"). Every selectable text in the app is one of these, styled as a SelectableTextBlock.
/// </summary>
public class SelectableText : SelectableTextBlock
{
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override TextLayout CreateTextLayout(string? text)
    {
        var start = Math.Min(SelectionStart, SelectionEnd);
        var end = Math.Max(SelectionStart, SelectionEnd);
        // Plain text is one run, which Avalonia's own selection draws right.
        if (_textRuns is not { } runs || start == end || SelectionForegroundBrush is not { } selected)
            return base.CreateTextLayout(text);

        // The runs, those across the selection's ends split there, the selected pieces in the
        // selection's colour and otherwise as they were.
        var shown = new List<TextRun>(runs.Count + 2);
        var at = 0;
        foreach (var run in runs)
        {
            var from = Math.Clamp(start - at, 0, run.Length);
            var to = Math.Clamp(end - at, 0, run.Length);
            at += run.Length;
            if (from == to || run is not TextCharacters { Properties: { } p })
            {
                shown.Add(run);
                continue;
            }
            var recoloured = new GenericTextRunProperties(p.Typeface, p.FontRenderingEmSize,
                p.TextDecorations, selected, p.BackgroundBrush, p.BaselineAlignment, p.CultureInfo, p.FontFeatures);
            if (from > 0)
                shown.Add(new TextCharacters(run.Text[..from], p));
            shown.Add(new TextCharacters(run.Text[from..to], recoloured));
            if (to < run.Length)
                shown.Add(new TextCharacters(run.Text[to..], p));
        }

        var typeface = new Avalonia.Media.Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var defaults = new GenericTextRunProperties(typeface, FontSize, TextDecorations, Foreground, fontFeatures: FontFeatures);
        var paragraph = new GenericTextParagraphProperties(FlowDirection, TextAlignment, true, false,
            defaults, TextWrapping, LineHeight, 0, LetterSpacing);
        var max = GetMaxSizeFromConstraint();
        return new TextLayout(new InlinesTextSource(shown), paragraph, TextTrimming, max.Width, max.Height, MaxLines);
    }
}
