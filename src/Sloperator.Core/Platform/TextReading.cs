namespace Sloperator.Core.Platform;

/// <summary>A word the OS read off a picture, with its box in the picture's pixels.</summary>
public sealed record TextWord(string Text, double X, double Y, double Width, double Height);

/// <summary>A line of words the OS read off a picture.</summary>
public sealed record TextLine(string Text, IReadOnlyList<TextWord> Words);

/// <summary>The OS's own text recognition (Windows' OCR).</summary>
public interface ITextReader
{
    /// <summary>The lines of text in <paramref name="image"/>; null when the OS has no language it can read installed.</summary>
    IReadOnlyList<TextLine>? Read(RawImage image);
}
