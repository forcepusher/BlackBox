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

    /// <summary>The jobs queued last: the hub lives as long as the process, and a long batch of jobs only added to it.</summary>
    private readonly RecentMap<string, (string ClientId, IReadOnlyDictionary<string, string> Titles)> _jobs = new(MaxJobs);

    internal const int MaxJobs = 200;

    /// <summary>
    /// The client a job was queued as, and its nodes' names: ComfyUI reports a job's progress only
    /// to the web socket of the client that queued it, so a later wait for it connects as that client.
    /// </summary>
    public void RememberJob(string promptId, string clientId, IReadOnlyDictionary<string, string> titles) => _jobs.Set(promptId, (clientId, titles));

    public (string ClientId, IReadOnlyDictionary<string, string> Titles) JobClient(string promptId) =>
        _jobs.TryGet(promptId, out var known) ? known : ("blackbox-" + Guid.NewGuid().ToString("N")[..12], new Dictionary<string, string>());

    public ComfyFrontend Frontend { get; } = new(Path.Combine(stateDirectory, "browser"));

    /// <summary>What Civitai says about the model files: how to prompt them, at what size, with which settings.</summary>
    public ComfyModelCards Cards { get; } = new(stateDirectory);

    /// <summary>Civitai's most downloaded checkpoints and LoRAs this month, kept for a week.</summary>
    public ComfyPopularModels Popular { get; } = new(stateDirectory);

    public string LogFile => Path.Combine(stateDirectory, "server.log");

    /// <summary>BlackBox's Data/tools, beside Data/comfyui: where it installs ComfyUI, and looks for it first.</summary>
    public string ToolsDirectory { get; init; } = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(stateDirectory))!, "tools");

    private IReadOnlyList<ComfyInstall> FindInstalls(CancellationToken ct) => ComfyInstall.Find(TimeSpan.FromSeconds(20), ct, [ToolsDirectory]);

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
                // By the clock: each look waits for an answer too, so counting 3 s a round said 90 s at about 180.
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (waited.Elapsed < TimeSpan.FromSeconds(90) && await api.TrySystemStatsAsync(ct).ConfigureAwait(false) is null)
                {
                    progress?.Invoke($"ComfyUI is busy and doesn't answer yet… {waited.Elapsed.TotalSeconds:0} s");
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                }
                if (await api.TrySystemStatsAsync(ct).ConfigureAwait(false) is null)
                    throw new ComfyApiException($"Process {busy} listens on port {address.Port} but hasn't answered for 90 seconds: ComfyUI may be busy " +
                                                "(a custom node downloading something holds it up). Wait and try again, or restart it with action=restart.");
            }
            if (await api.TrySystemStatsAsync(ct).ConfigureAwait(false) is not null)
            {
                // Running already (started by the user, or earlier); learn where it lives, for its files.
                var code = await CodeFolderAsync(api, ct).ConfigureAwait(false);
                if (SavedInstall is null && code is not null)
                    SaveState(address, ComfyInstall.FromFolder(code));
                Announce(api, code, notes);
                return api;
            }

            // One the user runs on ComfyUI's usual ports (the portable build's 8188, the Desktop
            // app's 8000), elsewhere than the address used last, is used as it runs.
            if (folder is not { Length: > 0 } && api.IsLocal)
                foreach (var port in new[] { ComfyProcess.DefaultPort, ComfyProcess.DesktopPort }.Where(p => p != address.Port))
                {
                    var other = new ComfyApi(new Uri($"http://127.0.0.1:{port}/"));
                    if (await other.TrySystemStatsAsync(ct).ConfigureAwait(false) is null)
                        continue;
                    var code = await CodeFolderAsync(other, ct).ConfigureAwait(false);
                    SaveState(other.BaseUrl, code is null ? null : ComfyInstall.FromFolder(code));
                    Announce(other, code, notes);
                    return other;
                }

            install ??= SavedInstall;
            if (install is null)
            {
                progress?.Invoke("Looking for ComfyUI on this PC…");
                var found = FindInstalls(ct);
                install = found.FirstOrDefault()
                          ?? throw new ComfyApiException("ComfyUI isn't running (at ports 8188 and 8000) and isn't installed in any usual place on this PC (looked for the Desktop app, " +
                                                         "in BlackBox's Data\\tools, Stability Matrix's and Pinokio's folders, and on every drive a few folders deep). " +
                                                         "If it is somewhere else, give its folder in folder=. Otherwise install it with action=install: " +
                                                         "the portable build that suits this PC's graphics card (or its CPU) goes into BlackBox's Data\\tools, about 2 GB to download.");
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
                SaveState(address, install);
                RememberServer(api, ours: true);
                _announced = $"{api.BaseUrl}|{Generation}";
                var (device, cpu) = ComfyHardware.Device(await api.TrySystemStatsAsync(ct).ConfigureAwait(false));
                notes.Add($"Started ComfyUI from {install.Root} at {address} (hidden; its log is {log}), on {device}. It is closed when this task is over." +
                          (cpu ? " " + ComfyHardware.CpuAdvice : ""));
                return api;
            }
            SaveState(address, install);
            return api;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The server and run it was last said to be used in, so it is said once.</summary>
    private string? _announced;

    /// <summary>
    /// Says, once, which ComfyUI is used when it ran already: the user's own (portable, the Desktop
    /// app, an install of their own) is used as it is and keeps running after the task.
    /// </summary>
    private void Announce(ComfyApi api, string? code, List<string> notes)
    {
        var key = $"{api.BaseUrl}|{Generation}";
        if (_announced == key)
            return;
        _announced = key;
        var where = code is not null ? $" (from {code})" : "";
        notes.Add(OwnsForTests?.Invoke(api) ?? IsOurs(api)
            ? $"Using the ComfyUI BlackBox started at {api.BaseUrl}{where}; it is closed when this task is over."
            : $"Using the ComfyUI that already runs at {api.BaseUrl}{where}; it keeps running after this task.");
    }

    /// <summary>Where a running server's ComfyUI folder is (main.py, models\…), as it says; null when it doesn't.</summary>
    private static async Task<string?> CodeFolderAsync(ComfyApi api, CancellationToken ct) =>
        await FoldersAsync(api, ct).ConfigureAwait(false) is { } dirs && dirs.TryGetValue("code", out var code) ? code : null;

    /// <summary>
    /// Installs ComfyUI's portable build that suits this PC (see <see cref="ComfyHardware.Pick"/>)
    /// into <paramref name="folder"/>, BlackBox's Data/tools unless told otherwise, and makes it the
    /// one to use. Without a folder, a ComfyUI that already runs or is on the PC is kept instead:
    /// the answer says where it is.
    /// </summary>
    public async Task<string> InstallAsync(string? folder, Action<string>? progress, CancellationToken ct)
    {
        if (!ComfyInstaller.Supported)
            throw new ComfyApiException("BlackBox installs ComfyUI's portable build on 64-bit Windows only. Elsewhere install it as its README says " +
                                        "(https://github.com/Comfy-Org/ComfyUI), then give its folder in folder=.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (folder is not { Length: > 0 })
            {
                // A ComfyUI the user has (running, or found on the PC) is used rather than a second one.
                ComfyApi? running = null;
                foreach (var url in new[] { Address, new Uri($"http://127.0.0.1:{ComfyProcess.DefaultPort}/"), new Uri($"http://127.0.0.1:{ComfyProcess.DesktopPort}/") }.Distinct())
                    if (running is null && await new ComfyApi(url).TrySystemStatsAsync(ct).ConfigureAwait(false) is not null)
                        running = new ComfyApi(url);
                var code = running is null ? null : await CodeFolderAsync(running, ct).ConfigureAwait(false);
                var existing = SavedInstall ?? (code is not null ? ComfyInstall.FromFolder(code) : null) ?? (running is null ? FindInstalls(ct).FirstOrDefault() : null);
                if (running is not null && existing is null)
                    return $"A ComfyUI runs already, at {running.BaseUrl}; nothing was downloaded, it is used as it is.";
                if (existing is not null)
                    return $"ComfyUI is installed already, in {existing.Root}{(running is not null ? $", and runs at {running.BaseUrl}" : "")}; nothing was downloaded. " +
                           "To install another one anyway, give the folder for it in folder=.";
            }
            var build = ComfyHardware.Pick(ComfyHardware.Cards(), ComfyInstaller.Arm64);
            var install = await ComfyInstaller.InstallAsync(folder is { Length: > 0 } ? Path.GetFullPath(folder) : ToolsDirectory, build, progress, ct).ConfigureAwait(false);
            SaveState(Address, install);
            return $"Installed ComfyUI's portable build ({build.Asset}) in {install.Root}, to run on {build.Device}.";
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Where a models folder (checkpoints, loras…) is: as the running server says, since it may keep
    /// models elsewhere (extra_model_paths.yaml), else in the installation used last or found on
    /// the PC. Null when there is no ComfyUI.
    /// </summary>
    public async Task<string?> ModelsFolderAsync(string folder, CancellationToken ct)
    {
        var api = new ComfyApi(Address);
        if (await api.TrySystemStatsAsync(ct).ConfigureAwait(false) is not null && await FoldersAsync(api, ct).ConfigureAwait(false) is { } dirs)
        {
            if (dirs.TryGetValue("models:" + folder, out var dir))
                return dir;
            if (dirs.TryGetValue("code", out var code))
                return Path.Combine(code, "models", folder);
        }
        var install = SavedInstall ?? FindInstalls(ct).FirstOrDefault();
        return install is null ? null : Path.Combine(install.CodeDir, "models", folder);
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
            // One the user had running stays theirs after a restart: it keeps running after the task.
            var ours = stats is null || IsOurs(api);
            var (stopped, program) = await ComfyProcess.StopAsync(api, ct).ConfigureAwait(false);
            Generation++;
            RememberServer(api, ours: false);
            if (!start)
                return stopped ? "ComfyUI is stopped." : "ComfyUI wasn't running.";
            install ??= FindInstalls(ct).FirstOrDefault()
                        ?? throw new ComfyApiException("ComfyUI's folder isn't known, so it can't be started again; give it in folder=, or install it with action=install.");
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
                ComfyProcess.Quietly(args);
            }
            if (program is not null && install.Kind != ComfyInstallKind.Desktop && File.Exists(program) && Path.GetFileName(program).StartsWith("python", StringComparison.OrdinalIgnoreCase))
                install = install with { Program = program };
            var log = ComfyProcess.FreeLogFile(LogFile);
            await ComfyProcess.StartAsync(install, api, log, args, TimeSpan.FromMinutes(5), progress, ct).ConfigureAwait(false);
            Generation++;
            SaveState(Address, install);
            RememberServer(api, ours);
            return $"ComfyUI {(stopped ? "was restarted" : "was started")} from {install.Root} (hidden; its log is {log})" +
                   (ours ? ". It is closed when this task is over." : "; it was running before this task, so it keeps running after it.");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- closing it after the task ------------------------------------------------------------

    // The user's choice (2026-10-16): ComfyUI closes after a job. A server BlackBox started is
    // stopped when the last task using it is over and its queue is empty; one the user started
    // keeps running, with the models BlackBox's jobs loaded let go.
    private int _users;
    private CancellationTokenSource? _waitForQueue;
    private Task _closing = Task.CompletedTask;

    /// <summary>Stand-ins for tests: whether the server that runs is the one BlackBox started, and how it is stopped.</summary>
    internal Func<ComfyApi, bool>? OwnsForTests { get; set; }
    internal Func<ComfyApi, CancellationToken, Task>? StopForTests { get; set; }

    /// <summary>Done when a server BlackBox started has been closed after the last task (it may wait for its queue to empty first).</summary>
    public Task Closing => _closing;

    /// <summary>A task starts using the server; it calls <see cref="ReleaseAsync"/> when it is over.</summary>
    public void Use()
    {
        Interlocked.Increment(ref _users);
        Interlocked.Exchange(ref _waitForQueue, null)?.Cancel();
    }

    /// <summary>
    /// A task that used the server is over. When no other task uses it, a server BlackBox started is
    /// closed, once the jobs in its queue are done; <paramref name="ranJobs"/> lets one the user
    /// started let go of the models the task's jobs loaded.
    /// </summary>
    public async Task ReleaseAsync(bool ranJobs)
    {
        var left = Interlocked.Decrement(ref _users);
        if (left > 0)
            return;
        if (left < 0)
            Interlocked.Exchange(ref _users, 0);
        var api = new ComfyApi(Address);
        if (!(OwnsForTests?.Invoke(api) ?? IsOurs(api)))
        {
            if (ranJobs)
                await api.FreeAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        // Another BlackBox (the command line, another build) may be between two of its jobs, and each
        // counts only its own tasks: the server is left running, for the next start to close.
        if (OthersRunForTests?.Invoke() ?? OtherBlackBoxRuns())
            return;
        var wait = new CancellationTokenSource();
        Interlocked.Exchange(ref _waitForQueue, wait)?.Cancel();
        _closing = CloseWhenIdleAsync(api, wait.Token);
        // A short wait here; a queue still at work is waited for in the background.
        await Task.WhenAny(_closing, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
    }

    /// <summary>
    /// At start: a server an earlier BlackBox started and left running (it was closed while jobs
    /// were still queued) is closed once its queue is empty, unless a task starts using it first.
    /// It ran on, hidden, until a later task that used ComfyUI ended.
    /// </summary>
    public void CloseLeftover()
    {
        if (Volatile.Read(ref _users) > 0)
            return;
        var wait = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _waitForQueue, wait, null) is not null)
            return;
        _closing = Task.Run(async () =>
        {
            // Not while another BlackBox runs (the command line, another build): it may be
            // between two of its jobs, and each BlackBox counts only its own tasks.
            if (OthersRunForTests?.Invoke() ?? OtherBlackBoxRuns())
                return;
            var api = new ComfyApi(Address);
            if (OwnsForTests?.Invoke(api) ?? IsOurs(api))
                await CloseWhenIdleAsync(api, wait.Token).ConfigureAwait(false);
        });
    }

    /// <summary>Stand-in for tests: whether another BlackBox process runs.</summary>
    internal Func<bool>? OthersRunForTests { get; set; }

    private static bool OtherBlackBoxRuns()
    {
        var found = false;
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                found |= process.Id != Environment.ProcessId && process.ProcessName.StartsWith("BlackBox", StringComparison.OrdinalIgnoreCase);
            }
            catch (InvalidOperationException) { } // ended meanwhile
            finally
            {
                process.Dispose();
            }
        }
        return found;
    }

    private async Task CloseWhenIdleAsync(ComfyApi api, CancellationToken ct)
    {
        try
        {
            // Until the jobs in its queue are done. One that stops answering for half a minute is
            // stopped all the same; one that has gone needs nothing.
            for (var failures = 0; failures < 6; )
            {
                try
                {
                    var queue = await api.GetJsonAsync("queue", ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false) as JsonObject;
                    if ((queue?["queue_running"] as JsonArray)?.Count is null or 0 && (queue?["queue_pending"] as JsonArray)?.Count is null or 0)
                        break;
                    failures = 0;
                }
                catch (Exception ex) when (ex is ComfyApiException or System.Text.Json.JsonException)
                {
                    if (!(OwnsForTests?.Invoke(api) ?? IsOurs(api)))
                        return;
                    failures++;
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Still the one BlackBox started, and still nobody's: a task that starts using it cancels
                // this, but one that started while the last one's end was still finding out whose the
                // server is found nothing to cancel.
                if (ct.IsCancellationRequested || Volatile.Read(ref _users) > 0 || !(OwnsForTests?.Invoke(api) ?? IsOurs(api)))
                    return;
                if (StopForTests is { } stop)
                    await stop(api, ct).ConfigureAwait(false);
                else
                    await ComfyProcess.StopAsync(api, ct).ConfigureAwait(false);
                Generation++;
                RememberServer(api, ours: false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (ComfyApiException) { } // it didn't stop in time; the next task's end tries again
    }

    /// <summary>Keeps which process is the server BlackBox started (by its id and start time), or forgets it.</summary>
    private void RememberServer(ComfyApi api, bool ours)
    {
        var state = LoadState();
        state.Remove("pid");
        state.Remove("pidStarted");
        if (ours && api.IsLocal && ComfyProcess.ListeningProcess(api.BaseUrl.Port) is { } pid && StartTime(pid) is { } started)
        {
            state["pid"] = pid;
            state["pidStarted"] = started;
        }
        try
        {
            Settings.AtomicFile.Write(StateFile, state.ToJsonString(ComfyWorkflow.Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Whether the server that runs now is the one BlackBox started (it may have been started by an earlier BlackBox run).</summary>
    private bool IsOurs(ComfyApi api)
    {
        var state = LoadState();
        return api.IsLocal && state["pid"] is JsonValue p && p.TryGetValue<int>(out var pid) &&
               ComfyProcess.ListeningProcess(api.BaseUrl.Port) == pid && StartTime(pid) is { } started && started == (string?)state["pidStarted"];
    }

    private static string? StartTime(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
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
