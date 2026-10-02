using System.Net;
using System.Net.Http.Headers;

namespace BlackBox.Core.Llm;

public readonly record struct DownloadProgress(string FileName, long Done, long? Total, double BytesPerSecond);

/// <summary>
/// Resumable HTTP download into a ".part" file that is renamed into place when complete,
/// so an interrupted download never looks like a finished model.
/// </summary>
public sealed class ModelDownloader(HttpClient http)
{
    public async Task DownloadAsync(string url, string destination, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        var name = Path.GetFileName(destination);
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file is already complete.
            File.Move(part, destination, overwrite: true);
            return;
        }
        resp.EnsureSuccessStatusCode();

        var resumed = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;
        long? total = resp.Content.Headers.ContentLength is { } len ? len + existing : null;

        await using (var source = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            var done = existing;
            var windowStart = DateTime.UtcNow;
            long windowBytes = 0;
            var lastReport = DateTime.MinValue;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                windowBytes += read;
                var now = DateTime.UtcNow;
                if (now - lastReport > TimeSpan.FromMilliseconds(250))
                {
                    var seconds = Math.Max(0.001, (now - windowStart).TotalSeconds);
                    progress?.Report(new DownloadProgress(name, done, total, windowBytes / seconds));
                    lastReport = now;
                    if (seconds > 5) { windowStart = now; windowBytes = 0; }
                }
            }
            progress?.Report(new DownloadProgress(name, done, total, 0));
        }

        if (total is { } t && new FileInfo(part).Length != t)
            throw new IOException($"Download of {name} ended early. Press Download again to resume.");
        File.Move(part, destination, overwrite: true);
    }
}
