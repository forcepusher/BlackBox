using System.Text;
using System.Text.Json;

namespace BlackBox.Core.Settings;

/// <summary>
/// Files appended to the end of the executable (the model and vision projector), after the
/// .NET single-file bundle. The loader never maps this tail, so it only costs disk space.
/// </summary>
/// <remarks>
/// Layout at the end of the exe:
/// <code>[file 1][file 2]...[index JSON (UTF-8)][index length: int64 LE]["BBPAYLD1"]</code>
/// Index entries hold absolute offsets into the exe.
/// </remarks>
public sealed class EmbeddedPayload
{
    public const string Magic = "BBPAYLD1";

    public sealed record Entry(string Name, long Offset, long Length);

    private EmbeddedPayload(string path, IReadOnlyList<Entry> entries)
    {
        SourcePath = path;
        Entries = entries;
    }

    public string SourcePath { get; }
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>Reads the payload index from a file, or returns null if it has none.</summary>
    public static EmbeddedPayload? TryOpen(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 16) return null;
            var tail = new byte[16];
            fs.Seek(-16, SeekOrigin.End);
            fs.ReadExactly(tail);
            if (Encoding.ASCII.GetString(tail, 8, 8) != Magic) return null;
            var indexLength = BitConverter.ToInt64(tail, 0);
            if (indexLength <= 0 || indexLength > 1 << 20 || indexLength > fs.Length - 16) return null;
            var index = new byte[indexLength];
            fs.Seek(-16 - indexLength, SeekOrigin.End);
            fs.ReadExactly(index);
            var entries = JsonSerializer.Deserialize<List<Entry>>(index, JsonOptions) ?? [];
            return entries.Count == 0 ? null : new EmbeddedPayload(path, entries);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Entry? Find(string fileName) =>
        Entries.FirstOrDefault(e => string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if <paramref name="destination"/> already holds this entry (same size).</summary>
    public static bool IsExtracted(Entry entry, string destination) =>
        File.Exists(destination) && new FileInfo(destination).Length == entry.Length;

    /// <summary>Copies one entry out to <paramref name="destination"/> via a ".part" file.</summary>
    public async Task ExtractAsync(Entry entry, string destination, IProgress<long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        await using (var src = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true))
        await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            dst.SetLength(entry.Length);
            src.Seek(entry.Offset, SeekOrigin.Begin);
            var buffer = new byte[8 << 20];
            long done = 0;
            var last = DateTime.MinValue;
            while (done < entry.Length)
            {
                var read = await src.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, entry.Length - done)), ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The executable ended before the embedded model did; it may be damaged.");
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (DateTime.UtcNow - last > TimeSpan.FromMilliseconds(200))
                {
                    progress?.Report(done);
                    last = DateTime.UtcNow;
                }
            }
            progress?.Report(done);
        }
        File.Move(part, destination, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
