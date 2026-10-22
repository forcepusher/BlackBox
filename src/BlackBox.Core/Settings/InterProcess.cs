using System.Security.Cryptography;
using System.Text;

namespace BlackBox.Core.Settings;

/// <summary>
/// What several BlackBoxes running from one folder share (settings.json, the chats, the headless
/// browser's profile), kept from getting in each other's way. Each window is a process of its own.
/// </summary>
public static class InterProcess
{
    /// <summary>
    /// Holds a mutex named after <paramref name="path"/> until disposed, after waiting up to
    /// <paramref name="timeout"/> for another BlackBox to let go of it; past that, goes ahead
    /// without it. A mutex belongs to its thread: dispose it on the thread that took it, with no
    /// await in between. Windows lets go of it for a process that ends while holding it.
    /// </summary>
    public static IDisposable Lock(string path, TimeSpan timeout)
    {
        var mutex = new Mutex(false, MutexName(path));
        bool held;
        try { held = mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { held = true; } // a BlackBox that ended while holding it
        return new Held(mutex, held);
    }

    /// <summary>A name for a mutex that is the same in every process for the same file or folder.</summary>
    internal static string MutexName(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            full = full.ToUpperInvariant();
        return @"Local\BlackBox." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..24];
    }

    private sealed class Held(Mutex mutex, bool held) : IDisposable
    {
        public void Dispose()
        {
            if (held)
                mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    /// <summary>
    /// Claims <paramref name="file"/> for this process until disposed: held open with this
    /// process's id in it, and gone once let go of, also when the process ends however it ends.
    /// Null when another process has it.
    /// </summary>
    public static IDisposable? TryClaim(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose);
            try
            {
                var id = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                stream.SetLength(0);
                stream.Write(id);
                stream.Flush();
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        // Held by another process; or let go of a moment ago and not quite gone yet.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Waits until <paramref name="file"/> can be claimed (see <see cref="TryClaim"/>), trying every 200 ms.</summary>
    public static async Task<IDisposable> ClaimAsync(string file, CancellationToken ct)
    {
        while (true)
        {
            if (TryClaim(file) is { } claim)
                return claim;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The process that claimed <paramref name="file"/>, if another one has it; null when none does.</summary>
    public static int? ClaimedBy(string file)
    {
        // A file left behind where it couldn't be deleted on close (a network share) is no claim.
        if (TryClaim(file) is { } free)
        {
            free.Dispose();
            return null;
        }
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return int.TryParse(reader.ReadToEnd().Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                   && id != Environment.ProcessId ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
