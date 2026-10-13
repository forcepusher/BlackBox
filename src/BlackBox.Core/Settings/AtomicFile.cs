namespace BlackBox.Core.Settings;

internal static class AtomicFile
{
    /// <summary>
    /// Writes a file through a temporary one, so a crash mid-write leaves the previous version.
    /// Antivirus scanners and indexers hold a file they have just seen open for a moment, which
    /// makes replacing it fail; the move is retried a few times before giving up.
    /// </summary>
    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
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
}
