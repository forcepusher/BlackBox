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

    /// <summary>The names of the languages it can read, as the OS shows them; empty when none is installed.</summary>
    IReadOnlyList<string> Languages();

    /// <summary>Installs the text recognition of the user's language (English where there is none of it), asking the OS's permission.</summary>
    Task<TextInstall> InstallAsync(CancellationToken ct);
}

/// <summary>How an install of text recognition ended.</summary>
public enum TextInstall
{
    Installed,
    /// <summary>The user said no to the OS's permission prompt.</summary>
    Declined,
    Failed,
}
