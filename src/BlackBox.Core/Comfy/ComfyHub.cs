using System.Text.Json.Nodes;

namespace BlackBox.Core.Comfy;

/// <summary>
/// The ComfyUI server BlackBox works with: where it is, whether it runs, and what it offers. One
/// per process, shared by every run, since there is one ComfyUI (and one GPU) on the PC. Its
/// address and folder are kept in Data/comfyui/state.json for later starts.
/// </summary>
public sealed class ComfyHub(string stateDirectory)
{
    private static readonly TimeSpan LibraryLife = TimeSpan.FromMinutes(2), TemplateLife = TimeSpan.FromMinutes(5);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ComfyHub> Hubs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The one hub for a Data folder's state, made on first use and kept for the process's life (its caches and browser outlast a run).</summary>
    public static ComfyHub For(string stateDirectory) => Hubs.GetOrAdd(Path.GetFullPath(stateDirectory), dir => new ComfyHub(dir));

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ComfyNodeLibrary? _library;
    private (int Generation, DateTime At) _libraryStamp;
    private List<ComfyTemplate>? _templates;
    private (int Generation, DateTime At) _templateStamp;
    private Dictionary<string, HashSet<string>>? _models;
    private (int Generation, DateTime At) _modelStamp;

    /// <summary>Changes whenever BlackBox (re)starts the server, so caches of its node types and templates are refreshed.</summary>
    public int Generation { get; private set; }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string ClientId, IReadOnlyDictionary<string, string> Titles)> _jobs = new();

    /// <summary>
    /// The client a job was queued as, and its nodes' names: ComfyUI reports a job's progress only
    /// to the web socket of the client that queued it, so a later wait for it connects as that client.
    /// </summary>
    public void RememberJob(string promptId, string clientId, IReadOnlyDictionary<string, string> titles) => _jobs[promptId] = (clientId, titles);

    public (string ClientId, IReadOnlyDictionary<string, string> Titles) JobClient(string promptId) =>
        _jobs.TryGetValue(promptId, out var known) ? known : ("blackbox-" + Guid.NewGuid().ToString("N")[..12], new Dictionary<string, string>());

    public ComfyFrontend Frontend { get; } = new(Path.Combine(stateDirectory, "browser"));

    public string LogFile => Path.Combine(stateDirectory, "server.log");

    private string StateFile => Path.Combine(stateDirectory, "state.json");

    /// <summary>The server's address: http://127.0.0.1:8188 unless the saved state says otherwise.</summary>
    public Uri Address
    {
        get
        {
            var url = (string?)LoadState()["url"];
            return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : new Uri($"http://127.0.0.1:{ComfyProcess.DefaultPort}/");
        }
    }

    /// <summary>The installation last used, if it is still there.</summary>
    public ComfyInstall? SavedInstall => (string?)LoadState()["folder"] is { } folder ? ComfyInstall.FromFolder(folder) : null;

    private JsonObject LoadState()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private void SaveState(Uri address, ComfyInstall? install)
    {
        var state = LoadState();
        state["url"] = address.ToString();
        if (install is not null)
            state["folder"] = install.Kind == ComfyInstallKind.Desktop ? install.Program : install.Root;
        try
        {
            Settings.AtomicFile.Write(StateFile, state.ToJsonString(ComfyWorkflow.Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The running server, started first if it isn't running: from <paramref name="folder"/> if
    /// given, else the installation used last, else the one found on the PC. Says in
    /// <paramref name="notes"/> what it did.
    /// </summary>
    public async Task<ComfyApi> ConnectAsync(string? folder, List<string> notes, Action<string>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ComfyInstall? install = null;
            if (folder is { Length: > 0 })
            {
                install = ComfyInstall.FromFolder(folder) ?? throw new ComfyApiException($"There is no ComfyUI in {folder}: no ComfyUI\\main.py there.");
            }
            var address = install?.Kind == ComfyInstallKind.Desktop && Address.Port == ComfyProcess.DefaultPort
                ? new Uri($"http://127.0.0.1:{ComfyProcess.DesktopPort}/") : Address;
            var api = new ComfyApi(address);
            if (await api.TrySystemStatsAsync(ct).ConfigureAwait(false) is null && api.IsLocal && ComfyProcess.ListeningProcess(address.Port) is { } busy)
            {
                // Something holds the port but doesn't answer: a server that is busy (a custom node
                // downloading models blocked one for minutes), not one to start a second time.
                for (var waited = 0; waited < 90 && await api.TrySystemStatsAsync(ct).ConfigureAwait(false) is null; waited += 3)
                {
                    progress?.Invoke($"ComfyUI is busy and doesn't answer yet… {waited} s");
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                }
                if (await api.TrySystemStatsAsync(ct).ConfigureAwait(false) is null)
                    throw new ComfyApiException($"Process {busy} listens on port {address.Port} but hasn't answered for 90 seconds: ComfyUI may be busy " +
                                                "(a custom node downloading something holds it up). Wait and try again, or restart it with action=restart.");
            }
            if (await api.TrySystemStatsAsync(ct).ConfigureAwait(false) is not null)
            {
                // Running already (started by the user, or earlier); learn where it lives, for its files.
                if (SavedInstall is null && await FoldersAsync(api, ct).ConfigureAwait(false) is { } dirs && dirs.TryGetValue("code", out var code))
                    SaveState(address, ComfyInstall.FromFolder(code));
                return api;
            }

            install ??= SavedInstall;
            if (install is null)
            {
                progress?.Invoke("Looking for ComfyUI on this PC…");
                var found = ComfyInstall.Find(TimeSpan.FromSeconds(20), ct);
                install = found.FirstOrDefault()
                          ?? throw new ComfyApiException("ComfyUI isn't running and isn't installed in any usual place on this PC (looked on every drive, a few folders deep). " +
                                                         "If it is somewhere else, give its folder in folder=. To install it, the portable Windows build is at https://github.com/comfyanonymous/ComfyUI/releases.");
                if (found.Count > 1)
                    notes.Add($"Found ComfyUI in {found.Count} places ({string.Join("; ", found.Select(f => f.Root))}); using the one used most recently. Give another one's folder in folder= to use it instead.");
                if (install.Kind == ComfyInstallKind.Desktop && address.Port == ComfyProcess.DefaultPort)
                    api = new ComfyApi(address = new Uri($"http://127.0.0.1:{ComfyProcess.DesktopPort}/"));
            }
            if (await api.TrySystemStatsAsync(ct).ConfigureAwait(false) is null)
            {
                var log = ComfyProcess.FreeLogFile(LogFile);
                await ComfyProcess.StartAsync(install, api, log, null, TimeSpan.FromMinutes(5), progress, ct).ConfigureAwait(false);
                Generation++;
                notes.Add($"Started ComfyUI from {install.Root} at {address} (hidden; its log is {log}). It keeps running after this task.");
            }
            SaveState(address, install);
            return api;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the server (whoever started it) and, with <paramref name="start"/>, starts it again with
    /// the arguments it ran with: after installing custom nodes, which load only at start.
    /// </summary>
    public async Task<string> RestartAsync(bool start, Action<string>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var api = new ComfyApi(Address);
            var stats = await api.TrySystemStatsAsync(ct).ConfigureAwait(false);
            var argv = (stats?["system"]?["argv"] as JsonArray)?.Select(a => (string?)a).OfType<string>().ToList();
            var install = SavedInstall;
            if (install is null && stats is not null && await FoldersAsync(api, ct).ConfigureAwait(false) is { } dirs && dirs.TryGetValue("code", out var code))
                install = ComfyInstall.FromFolder(code);
            var (stopped, program) = await ComfyProcess.StopAsync(api, ct).ConfigureAwait(false);
            Generation++;
            if (!start)
                return stopped ? "ComfyUI is stopped." : "ComfyUI wasn't running.";
            install ??= ComfyInstall.Find(TimeSpan.FromSeconds(20), ct).FirstOrDefault()
                        ?? throw new ComfyApiException("ComfyUI's folder isn't known, so it can't be started again; give it in folder=.");
            // The same Python and arguments as before (the user's own flags stay); the script's
            // path is made absolute, since it was relative to where the server was started.
            List<string>? args = null;
            if (argv is { Count: > 0 } && install.Kind != ComfyInstallKind.Desktop)
            {
                args = [.. argv];
                if (!Path.IsPathRooted(args[0]))
                    args[0] = new[] { install.Root, install.CodeDir }.Select(d => Path.GetFullPath(Path.Combine(d, args[0]))).FirstOrDefault(File.Exists) ?? Path.Combine(install.CodeDir, "main.py");
                if (install.Kind == ComfyInstallKind.Portable)
                    args.Insert(0, "-s");
            }
            if (program is not null && install.Kind != ComfyInstallKind.Desktop && File.Exists(program) && Path.GetFileName(program).StartsWith("python", StringComparison.OrdinalIgnoreCase))
                install = install with { Program = program };
            var log = ComfyProcess.FreeLogFile(LogFile);
            await ComfyProcess.StartAsync(install, api, log, args, TimeSpan.FromMinutes(5), progress, ct).ConfigureAwait(false);
            Generation++;
            SaveState(Address, install);
            return $"ComfyUI {(stopped ? "was restarted" : "was started")} from {install.Root} (hidden; its log is {log}).";
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The server's node types, fetched again after a restart or when two minutes old (or when <paramref name="fresh"/>).</summary>
    public async Task<ComfyNodeLibrary> LibraryAsync(ComfyApi api, CancellationToken ct, bool fresh = false)
    {
        if (!fresh && _library is not null && _libraryStamp.Generation == Generation && DateTime.UtcNow - _libraryStamp.At < LibraryLife)
            return _library;
        var info = await api.GetJsonAsync("object_info", ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false) as JsonObject
                   ?? throw new ComfyApiException("ComfyUI sent no node types.");
        _library = ComfyNodeLibrary.Parse(info);
        _libraryStamp = (Generation, DateTime.UtcNow);
        return _library;
    }

    public async Task<List<ComfyTemplate>> TemplatesAsync(ComfyApi api, CancellationToken ct)
    {
        if (_templates is not null && _templateStamp.Generation == Generation && DateTime.UtcNow - _templateStamp.At < TemplateLife)
            return _templates;
        _templates = await ComfyTemplates.ListAsync(api, ct).ConfigureAwait(false);
        _templateStamp = (Generation, DateTime.UtcNow);
        return _templates;
    }

    /// <summary>Installed model files by folder (checkpoints, loras, diffusion_models…), as the server sees them.</summary>
    public async Task<Dictionary<string, HashSet<string>>> ModelsAsync(ComfyApi api, CancellationToken ct, bool fresh = false)
    {
        if (!fresh && _models is not null && _modelStamp.Generation == Generation && DateTime.UtcNow - _modelStamp.At < LibraryLife)
            return _models;
        var models = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (await api.GetJsonAsync("models", ct).ConfigureAwait(false) is JsonArray folders)
            foreach (var folder in folders.Select(f => (string?)f).OfType<string>())
            {
                try
                {
                    if (await api.GetJsonAsync("models/" + Uri.EscapeDataString(folder), ct).ConfigureAwait(false) is JsonArray files)
                        models[folder] = files.Select(f => (string?)f).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch (ComfyApiException) { }
            }
        // A model the agent just downloaded is a new choice in its loader node too: qwen3.8 found
        // the node list it looked up next without it.
        if (_models is not null && !SameModels(_models, models))
            _library = null;
        _models = models;
        _modelStamp = (Generation, DateTime.UtcNow);
        return models;

        static bool SameModels(Dictionary<string, HashSet<string>> a, Dictionary<string, HashSet<string>> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var files) && files.SetEquals(kv.Value));
    }

    /// <summary>
    /// The server's folders on this PC: "code" (where main.py is), "input", "output", "temp", and
    /// each model folder ("models:checkpoints" → its first path). Null when it doesn't say.
    /// </summary>
    public static async Task<Dictionary<string, string>?> FoldersAsync(ComfyApi api, CancellationToken ct)
    {
        try
        {
            if (await api.GetJsonAsync("internal/folder_paths", ct).ConfigureAwait(false) is not JsonObject paths)
                return null;
            var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in paths)
                if (value is JsonArray { Count: > 0 } list && (string?)list[0] is { Length: > 0 } first)
                    dirs["models:" + name] = first;
            if (dirs.TryGetValue("models:custom_nodes", out var customNodes))
                dirs["code"] = Path.GetDirectoryName(customNodes)!;
            if (!dirs.TryGetValue("code", out var code))
                return dirs;
            // Where the server keeps its files: next to main.py unless it was started with other folders.
            var stats = await api.TrySystemStatsAsync(ct).ConfigureAwait(false);
            var argv = (stats?["system"]?["argv"] as JsonArray)?.Select(a => (string?)a).OfType<string>().ToList() ?? [];
            var baseDir = Arg(argv, "--base-directory") ?? code;
            dirs["output"] = Arg(argv, "--output-directory") ?? Path.Combine(baseDir, "output");
            dirs["input"] = Arg(argv, "--input-directory") ?? Path.Combine(baseDir, "input");
            dirs["temp"] = Arg(argv, "--temp-directory") is { } temp ? Path.Combine(temp, "temp") : Path.Combine(baseDir, "temp");
            return dirs;
        }
        catch (Exception ex) when (ex is ComfyApiException or System.Text.Json.JsonException)
        {
            return null;
        }

        static string? Arg(List<string> argv, string flag)
        {
            var i = argv.IndexOf(flag);
            return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
        }
    }
}
