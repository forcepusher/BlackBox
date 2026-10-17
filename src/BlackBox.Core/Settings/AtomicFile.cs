namespace BlackBox.Core.Settings;

internal static class AtomicFile
{
    /// <summary>
    /// Writes a file through a temporary one, so a crash mid-write leaves the previous version.
    /// The new text is on the disk before it takes the old one's place: Windows can carry out the
    /// rename first, and a power cut in between left a settings file or a chat of zeros where the
    /// old one had been. Each write has a temporary file of its own, as the agent and the Settings
    /// dialog can save mcp.json at the same moment. Antivirus scanners and indexers hold a file
    /// they have just seen open for a moment, which makes replacing it fail; the move is retried a
    /// few times before giving up.
    /// </summary>
    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(file, leaveOpen: true))
                    writer.Write(text);
                file.Flush(flushToDisk: true);
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
                {
                    Thread.Sleep(50 * attempt);
                }
            }
        }
        finally
        {
            // Gone once moved; left over only when the write or the move failed.
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
