namespace BlackBox.Core.Media;

/// <summary>
/// A file about to be written over, said to whoever plays it, so they let go of it first: a clip
/// in the chat plays over and over while it is on screen, holding its file open, and making a shot
/// again failed with "the process cannot access the file" while its first take played there.
/// </summary>
public static class MediaRelease
{
    /// <summary>A file (its full path) is about to be written over: stop playing it, and return once it is let go.</summary>
    public static event Action<string>? Releasing;

    /// <summary>Has whoever plays <paramref name="path"/> let go of it; returns once they have.</summary>
    public static void Release(string path)
    {
        try
        {
            Releasing?.Invoke(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A player that couldn't be stopped: the write says so, as before.
        }
    }
}
