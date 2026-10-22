using BlackBox.Core.Llm;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace BlackBox.Core.Mcp;

public enum McpServerState { Disabled, Connecting, Connected, Failed }

public sealed record McpServerStatus(string Name, McpServerState State, IReadOnlyList<string> ToolNames, string? Error, string? Instructions)
{
    /// <summary>One line for the settings dialog and the CLI.</summary>
    public string Summary => State switch
    {
        McpServerState.Disabled => $"{Name}: disabled",
        McpServerState.Connecting => $"{Name}: starting…",
        McpServerState.Connected => $"{Name}: connected, {ToolNames.Count} tool{(ToolNames.Count == 1 ? "" : "s")}" +
                                    (ToolNames.Count > 0 ? " (" + string.Join(", ", ToolNames.Take(12)) + (ToolNames.Count > 12 ? ", …" : "") + ")" : ""),
        _ => $"{Name}: not available. {Error}",
    };
}

/// <summary>
/// The configured MCP servers, kept running for as long as the app: local ones are started as
/// child processes, remote ones connected to. <see cref="Tools"/> is a snapshot taken when a run
/// starts, so the tool list a run sends never changes in the middle (the server's prompt cache
/// depends on it).
/// </summary>
public sealed class McpHost : IAsyncDisposable
{
    /// <summary>Starting a server can mean npx downloading it first.</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(90);

    private readonly object _gate = new();
    private readonly HttpClient _http = LlmHttp.Create(Timeout.InfiniteTimeSpan);
    private List<Connection> _connections = [];

    /// <summary>Tests put a server in-process by handing out its transport here.</summary>
    internal Func<McpServerConfig, IClientTransport>? TransportFactory { get; set; }

    public IReadOnlyList<McpServerStatus> Statuses
    {
        get { lock (_gate) return _connections.Select(c => c.Status).ToList(); }
    }

    /// <summary>The tools of all connected servers, as a new list each time.</summary>
    public IReadOnlyList<McpTool> Tools
    {
        get { lock (_gate) return _connections.SelectMany(c => c.Tools).ToList(); }
    }

    /// <summary>Servers that are connected and offer tools; what the system prompt tells the model about.</summary>
    public IReadOnlyList<McpServerStatus> Connected => Statuses.Where(s => s.State == McpServerState.Connected && s.ToolNames.Count > 0).ToList();

    /// <summary>Stops the servers of the previous call and starts these, all at once. Never throws: failures show in <see cref="Statuses"/>.</summary>
    public async Task ConnectAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);
        var connections = servers.Select(s => new Connection(s)).ToList();
        lock (_gate) _connections = connections;
        await Task.WhenAll(connections.Where(c => !c.Config.Disabled).Select(c => ConnectOneAsync(c, ct))).ConfigureAwait(false);
        lock (_gate)
        {
            // Same tool name on two servers, or two names that clean up to the same text: the later one gets a number.
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in connections)
                c.NameTools(taken);
        }
    }

    /// <summary>
    /// Starts one more server (in place of one of the same name) while the others keep running,
    /// for the agent setting one up itself (see <see cref="McpServersTool"/>). A server that fails
    /// is not kept. Returns its status and its tools, named as the model will call them.
    /// </summary>
    /// <param name="timeout">How long it may take to start: a first start may download it.</param>
    public async Task<(McpServerStatus Status, IReadOnlyList<McpTool> Tools)> AddAsync(McpServerConfig config, TimeSpan timeout, CancellationToken ct)
    {
        await RemoveAsync(config.Name).ConfigureAwait(false);
        var connection = new Connection(config);
        await ConnectOneAsync(connection, ct, timeout).ConfigureAwait(false);
        lock (_gate)
        {
            if (connection.Status.State == McpServerState.Connected)
            {
                connection.NameTools(new HashSet<string>(_connections.SelectMany(c => c.Tools).Select(t => t.Name), StringComparer.OrdinalIgnoreCase));
                _connections = [.. _connections, connection];
            }
        }
        if (connection.Status.State != McpServerState.Connected)
            await connection.DisposeAsync().ConfigureAwait(false);
        return (connection.Status, connection.Tools.ToList());
    }

    /// <summary>Stops the server of that name, if one runs; true when there was one.</summary>
    public async Task<bool> RemoveAsync(string name)
    {
        Connection? old;
        lock (_gate)
        {
            old = _connections.FirstOrDefault(c => c.Config.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (old is not null)
                _connections = _connections.Where(c => c != old).ToList();
        }
        if (old is null) return false;
        await old.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    private async Task ConnectOneAsync(Connection c, CancellationToken ct, TimeSpan? limit = null)
    {
        // Ends with its connection: the settings changed again while it started.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, c.Stopping);
        timeout.CancelAfter(limit ?? ConnectTimeout);
        var stderr = new Queue<string>();
        try
        {
            var transport = TransportFactory?.Invoke(c.Config) ?? CreateTransport(c.Config, stderr);
            var options = new McpClientOptions { ClientInfo = new() { Name = "BlackBox", Version = typeof(McpHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" } };
            var client = await McpClient.CreateAsync(transport, options, cancellationToken: timeout.Token).ConfigureAwait(false);
            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
                // Its connection was stopped meanwhile: kept there, the server's process would run on
                // with nobody to end it.
                if (!c.Connected(client, tools))
                    await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (c.Stopping.IsCancellationRequested) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            c.Failed($"It did not answer within {(limit ?? ConnectTimeout).TotalSeconds:0} seconds.", stderr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            c.Failed(Describe(ex), stderr);
        }
    }

    private IClientTransport CreateTransport(McpServerConfig config, Queue<string> stderr)
    {
        if (config.IsStdio)
        {
            var options = new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = config.Command!,
                Arguments = config.Args.ToList(),
                WorkingDirectory = config.Cwd,
                ShutdownTimeout = TimeSpan.FromSeconds(2),
                StandardErrorLines = line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    lock (stderr)
                    {
                        stderr.Enqueue(line.Trim());
                        while (stderr.Count > 5) stderr.Dequeue();
                    }
                },
            };
            if (config.Env.Count > 0)
                options.EnvironmentVariables = config.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
            return new StdioClientTransport(options);
        }

        var http = new HttpClientTransportOptions
        {
            Name = config.Name,
            Endpoint = new Uri(config.Url!),
            TransportMode = config.Transport switch
            {
                McpTransport.StreamableHttp => HttpTransportMode.StreamableHttp,
                McpTransport.Sse => HttpTransportMode.Sse,
                _ => HttpTransportMode.AutoDetect,
            },
        };
        if (config.Headers.Count > 0)
            http.AdditionalHeaders = config.Headers.ToDictionary(kv => kv.Key, kv => kv.Value);
        return new HttpClientTransport(http, _http, ownsHttpClient: false);
    }

    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null && inner is not McpException)
            inner = inner.InnerException;
        var message = inner.Message.Trim();
        if (message.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase) || message.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            return "The command was not found. Check that it is installed and on the PATH.";
        return message.TrimEnd('.') + ".";
    }

    private async Task DisconnectAsync()
    {
        List<Connection> old;
        lock (_gate)
        {
            old = _connections;
            _connections = [];
        }
        foreach (var c in old)
            await c.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _http.Dispose();
    }

    private sealed class Connection(McpServerConfig config)
    {
        private McpClient? _client;
        private IList<McpClientTool> _clientTools = [];
        private McpServerState _state = config.Disabled ? McpServerState.Disabled : McpServerState.Connecting;
        private string? _error;

        public McpServerConfig Config { get; } = config;
        public List<McpTool> Tools { get; } = [];

        public McpServerStatus Status => new(Config.Name, _state,
            _state == McpServerState.Connected ? _clientTools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList() : [], _error,
            _client?.ServerInstructions is { Length: > 0 } i ? i.Trim() : null);

        private readonly object _lock = new();
        private bool _stopped;

        /// <summary>Keeps the client; false when the connection was stopped first, and the client is the caller's to end.</summary>
        public bool Connected(McpClient client, IList<McpClientTool> tools)
        {
            lock (_lock)
            {
                if (_stopped)
                    return false;
                _client = client;
                _clientTools = tools;
                _state = McpServerState.Connected;
                return true;
            }
        }

        public void Failed(string error, Queue<string> stderr)
        {
            _state = McpServerState.Failed;
            string[] lines;
            lock (stderr) lines = stderr.ToArray();
            // The SDK's own message may already end in what the server printed ("Server's stderr tail").
            _error = lines.Length > 0 && !lines.All(error.Contains) ? error + " It said: " + string.Join(" / ", lines) : error;
        }

        public void NameTools(HashSet<string> taken)
        {
            Tools.Clear();
            // By name, so the tool list (part of every request, and of the server's prompt cache) is the same on every start.
            foreach (var t in _clientTools.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var name = McpTool.ExposedName(Config.Name, t.Name);
                for (var n = 2; !taken.Add(name); n++)
                    name = McpTool.ExposedName(Config.Name, t.Name, "_" + n);
                Tools.Add(new McpTool(Config.Name, name, t.ProtocolTool, (args, ct) => CallAsync(t, args, ct)));
            }
        }

        // Ends calls in flight when the server is stopped; a run that still holds the tool gets an error, not a hang.
        private readonly CancellationTokenSource _stopping = new();

        /// <summary>Cancelled when the connection is stopped, its start as well.</summary>
        public CancellationToken Stopping => _stopping.Token;

        private async Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(McpClientTool tool, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
        {
            if (_stopping.IsCancellationRequested)
                throw new InvalidOperationException(StoppedMessage);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
            try
            {
                return await tool.CallAsync(args, cancellationToken: linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new InvalidOperationException(StoppedMessage);
            }
        }

        private const string StoppedMessage = "the server was stopped (the MCP settings changed, or BlackBox is closing).";

        public async ValueTask DisposeAsync()
        {
            McpClient? client;
            lock (_lock)
            {
                _stopped = true;
                (client, _client) = (_client, null);
            }
            _stopping.Cancel();
            if (client is null) return;
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* a server that is already gone */ }
        }
    }
}
