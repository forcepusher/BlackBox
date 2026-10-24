using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BlackBox.Core.Llm;
using Xunit;

namespace BlackBox.Core.Tests;

public class LlmHttpTests
{
    /// <summary>
    /// LM Studio listens on IPv4 only, and "localhost" is ::1 first: Windows took 2 s to give up on
    /// ::1 at each new connection, so a task with a model to load reached LM Studio 4 s late.
    /// </summary>
    [Fact]
    public async Task Localhost_reaches_a_server_listening_on_IPv4_only_at_once()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                var stream = client.GetStream();
                var request = new byte[4096];
                _ = await stream.ReadAsync(request);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            for (var i = 0; i < 2; i++)
            {
                using var http = LlmHttp.Create(TimeSpan.FromSeconds(10));
                var watch = Stopwatch.StartNew();
                Assert.Equal("ok", await http.GetStringAsync($"http://localhost:{port}/v1/models", TestContext.Current.CancellationToken));
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.ElapsedMilliseconds} ms");
            }
            await serving;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_port_nobody_listens_on_fails_rather_than_hangs()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var http = LlmHttp.Create(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync($"http://localhost:{port}/v1/models", TestContext.Current.CancellationToken));
    }
}
