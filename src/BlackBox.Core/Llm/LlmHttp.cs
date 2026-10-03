using System.Net.Sockets;

namespace BlackBox.Core.Llm;

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

    public static SocketsHttpHandler CreateHandler() => new()
    {
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
