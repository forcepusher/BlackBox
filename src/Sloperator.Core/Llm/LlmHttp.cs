using System.Net;
using System.Net.Sockets;

namespace Sloperator.Core.Llm;

/// <summary>
/// HttpClient for talking to model servers.
/// </summary>
/// <remarks>
/// While a server processes a long prompt it sends nothing back, sometimes for minutes. Home
/// routers, NAT and port forwarding commonly drop TCP connections that look idle for about a
/// minute, after which the server logs "client disconnected" and the client waits forever.
/// TCP keep-alive probes keep such connections open where the cutter counts them as traffic.
/// Where it does not (some VPNs cut after a minute of silence regardless), the probes notice the
/// dead link within a few seconds, so the request is resent at once instead of the client
/// waiting the better part of a minute while the server sits idle with a finished reply.
/// </remarks>
public static class LlmHttp
{
    public static HttpClient Create(TimeSpan timeout) => new(CreateHandler()) { Timeout = timeout };

    private static SocketsHttpHandler CreateHandler() => new()
    {
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = async (context, ct) => new NetworkStream(await ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false), ownsSocket: true),
    };

    /// <summary>
    /// Connects to the host's IPv6 and IPv4 addresses at the same time and keeps whichever answers
    /// first; within a family, the next address is tried when one fails.
    /// </summary>
    /// <remarks>
    /// "localhost" is ::1 first, and LM Studio listens on IPv4 only: Windows takes 2 s to give up on
    /// a port nobody listens on, so each new connection to http://localhost:1234 waited 2 s, and a
    /// message sent with a model to load reached LM Studio 4 s late (the check before a task and
    /// the load each opened one).
    /// </remarks>
    internal static async Task<Socket> ConnectAsync(DnsEndPoint endpoint, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var families = addresses.GroupBy(a => a.AddressFamily).Select(f => InTurnAsync(f.ToList(), endpoint.Port, race.Token)).ToList();
        Exception? failure = null;
        while (families.Count > 0)
        {
            var done = await Task.WhenAny(families).ConfigureAwait(false);
            families.Remove(done);
            if (done.IsCompletedSuccessfully)
            {
                race.Cancel();
                // One that connects as well before it hears of the cancel isn't needed.
                foreach (var other in families)
                    _ = other.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
                return done.Result;
            }
            ct.ThrowIfCancellationRequested();
            failure ??= done.Exception?.InnerException;
        }
        throw failure!;
    }

    /// <summary>The addresses of one family, one after another until one connects.</summary>
    private static async Task<Socket> InTurnAsync(List<IPAddress> addresses, int port, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            var socket = new Socket(addresses[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                await socket.ConnectAsync(new IPEndPoint(addresses[i], port), ct).ConfigureAwait(false);
                return socket;
            }
            catch (Exception) when (i < addresses.Count - 1 && !ct.IsCancellationRequested)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
