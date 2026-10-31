using Sloperator.Core.Tools;
using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>
/// Picture files opened for Skia through .NET, never by their path. Skia opens a path with the C
/// runtime's fopen, whose handle every program started meanwhile inherits: a command the agent
/// started while a picture was read then held it open until it ended, and the picture couldn't be
/// deleted or replaced (the read_file picture test failed on its temporary folder that way).
/// </summary>
public static class PictureFile
{
    /// <summary>The file's codec; null when it can't be read, or isn't a picture Skia reads.</summary>
    public static SKCodec? Codec(string path)
    {
        try
        {
            // Read whole (the codec keeps its own reference to the bytes), so no handle stays open.
            using var stream = TextFiles.OpenRead(path);
            using var data = SKData.Create(stream);
            return data is null ? null : SKCodec.Create(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The picture in the file, as <see cref="SKBitmap.Decode(string)"/> reads it, an SVG drawn at its
    /// own size; null when it can't be read.
    /// </summary>
    public static SKBitmap? Decode(string path)
    {
        if (SvgFile.Handles(path))
            return SvgFile.Open(path)?.Render(1);
        using var codec = Codec(path);
        return codec is null ? null : SKBitmap.Decode(codec);
    }
}
