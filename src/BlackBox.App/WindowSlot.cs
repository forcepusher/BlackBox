namespace BlackBox.App;

/// <summary>
/// Which of the BlackBox windows open on the PC this one is, counting from 0: a named mutex each,
/// held until the process ends. The first opens where BlackBox was last closed, the next ones a
/// step down and to the right of that, as Windows cascades windows; right on top of the first, a
/// second window looked as if nothing had opened.
/// </summary>
internal static class WindowSlot
{
    /// <summary>Kept for as long as the process runs: the slot is taken while its mutex exists.</summary>
    private static readonly List<Mutex> Held = [];

    public static int Take()
    {
        for (var slot = 0; slot < 64; slot++)
        {
            try
            {
                var mutex = new Mutex(true, $@"Local\BlackBox.Window.{slot}", out var created);
                if (created)
                {
                    Held.Add(mutex);
                    return slot;
                }
                mutex.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
            {
                // Taken by a BlackBox run as administrator, which this one can't open: the next.
            }
        }
        return 0;
    }
}
