using System.Net;
using System.Net.Http.Headers;
using BlackBox.Core.Llm;
using Xunit;

namespace BlackBox.Core.Tests;

public class DownloaderTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public async Task Downloads_to_part_file_then_renames()
    {
        var dir = Directory.CreateTempSubdirectory("bb-dl-").FullName;
        var dest = Path.Combine(dir, "m.gguf");
        var reports = new List<DownloadProgress>();

        await new ModelDownloader(new HttpClient(new RangeServer())).DownloadAsync("http://x/m.gguf", dest, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(Payload, File.ReadAllBytes(dest));
        Assert.False(File.Exists(dest + ".part"));
        Assert.Equal(Payload.Length, reports[^1].Done);
    }

    [Fact]
    public async Task Resumes_an_interrupted_download()
    {
        var dir = Directory.CreateTempSubdirectory("bb-dl-").FullName;
        var dest = Path.Combine(dir, "m.gguf");
        File.WriteAllBytes(dest + ".part", Payload[..1_000_000]);
        var server = new RangeServer();

        await new ModelDownloader(new HttpClient(server)).DownloadAsync("http://x/m.gguf", dest, null, CancellationToken.None);

        Assert.Equal(1_000_000, server.LastRangeStart);
        Assert.Equal(Payload, File.ReadAllBytes(dest));
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    private sealed class RangeServer : HttpMessageHandler
    {
        public long? LastRangeStart { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var from = request.Headers.Range?.Ranges.First().From ?? 0;
            LastRangeStart = request.Headers.Range is null ? null : from;
            var body = new ByteArrayContent(Payload[(int)from..]);
            body.Headers.ContentLength = Payload.Length - from;
            var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = body };
            if (from > 0)
                body.Headers.ContentRange = new ContentRangeHeaderValue(from, Payload.Length - 1, Payload.Length);
            return Task.FromResult(resp);
        }
    }
}
