using BlackBox.Core.Mcp;
using BlackBox.Core.Settings;

namespace BlackBox.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ---- settings -------------------------------------------------------------------

    public SettingsViewModel CreateSettingsViewModel() => new(_settings.Clone(), _paths, LoadMcpConfig(), _mcp.Statuses);

    /// <summary>
    /// mcp.json as it is, read again a moment later when caught while the agent replaces it:
    /// Settings didn't open then, and the window showed an error instead.
    /// </summary>
    private string LoadMcpConfig()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return McpConfig.Load(_paths.McpConfigFile); }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Shows an open Settings dialog the MCP servers as they are now: one the agent added or removed, and how each one is doing.</summary>
    public void RefreshMcp(SettingsViewModel dialog)
    {
        try { dialog.ShowCurrentMcp(McpConfig.Load(_paths.McpConfigFile), _mcp.Statuses); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* caught mid-save: next time */ }
    }

    public async Task ApplySettingsAsync(SettingsViewModel dialog)
    {
        // What the user changed, over what another window's Settings saved while the dialog was
        // open: its copy still had the setting the other window changed, and took it back.
        var updated = _settings.With(dialog.Opened, dialog.Settings);
        // The server, key, model and context size live in the top bar, not in the dialog.
        updated.Server = _settings.Server;
        updated.Window = _settings.Window;
        _settings = updated;
        OnPropertyChanged(nameof(ApprovalNote));
        SaveSettings();
        // Written only when the user changed the list: a dialog left open while the agent added a
        // server would otherwise save the list without it.
        var mcpSaved = false;
        if (dialog.McpConfigText != dialog.McpConfigBase)
        {
            try
            {
                // What changed in the file meanwhile stays, unless the user changed the same server.
                var text = McpConfig.Merge(dialog.McpConfigBase, McpConfig.Load(_paths.McpConfigFile), dialog.McpConfigText);
                McpConfig.Save(_paths.McpConfigFile, text);
                mcpSaved = true;
                RestartMcp();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddNotice($"The MCP server list could not be saved: {ex.Message}", isError: true, ex);
            }
        }
        // They are BlackBox's settings, not this window's: "Ask me first" turned on here holds the
        // other windows' agents too.
        _windows.SettingsApplied(this, updated, mcpSaved);
        await CheckServerAsync();
    }

    /// <summary>
    /// The settings another window's Settings dialog saved: this window takes them, but for its own
    /// server, model and place, from its next task on (as the window they were changed in does).
    /// </summary>
    internal void TakeSettings(AppSettings applied, bool mcpChanged)
    {
        var taken = applied.Clone();
        taken.Server = _settings.Server;
        taken.Window = _settings.Window;
        _settings = taken;
        OnPropertyChanged(nameof(ApprovalNote));
        if (mcpChanged)
            RestartMcp();
    }

    /// <summary>The MCP servers started again from mcp.json: now, or once the task going on is over, not to pull its tools from under it.</summary>
    private void RestartMcp()
    {
        if (IsBusy) _mcpRestartPending = true;
        else _mcpConnect = ConnectMcpAsync();
    }

    /// <summary>Keeps the window's size and place for the next start (see <see cref="Views.MainWindow.Restore"/>).</summary>
    public void SavePlacement(WindowPlacement placement)
    {
        _settings.Window = placement;
        SaveSettings();
    }

    private bool _saveFailed;

    /// <summary>Writes the settings; a disk that refuses (read-only, full) is reported once, and BlackBox goes on with them in memory.</summary>
    private void SaveSettings()
    {
        try
        {
            _store.Save(_settings);
            _saveFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_saveFailed) AddNotice($"Settings could not be saved, so they last only until BlackBox closes: {ex.Message}", isError: true, ex);
            _saveFailed = true;
        }
    }

    // ---- MCP servers ----------------------------------------------------------------

    /// <summary>Starts the servers in Data/mcp.json (stopping any running ones) and reports the ones that fail in the chat.</summary>
    private async Task ConnectMcpAsync()
    {
        IReadOnlyList<McpServerConfig> servers;
        try
        {
            servers = McpConfig.Parse(McpConfig.Load(_paths.McpConfigFile)).Servers;
        }
        catch (Exception ex) when (ex is McpConfigException or IOException or UnauthorizedAccessException)
        {
            AddNotice($"The MCP server list could not be read, so no MCP tools are available: {ex.Message} Fix it in Settings.", isError: true, ex);
            servers = [];
        }
        await Task.Run(() => _mcp.ConnectAsync(servers, CancellationToken.None));
        foreach (var s in _mcp.Statuses.Where(s => s.State == McpServerState.Failed))
            AddNotice($"MCP server \"{s.Name}\" is not available: {s.Error} (Settings)", isError: true);
    }

    /// <summary>A line in the chat; an error goes into Data/errors.log too, with the exception behind it.</summary>
    private void AddNotice(string text, bool isError, Exception? error = null)
    {
        if (isError)
            Core.ErrorLog.Write(ChatPlace(), text, error);
        Items.Insert(NoticeIndex(), new NoticeItem(text, isError));
    }

    private readonly HashSet<string> _problemsShown = [];

    /// <summary>Something no code caught (see <see cref="CrashLog"/>), said in the chat once, with where to read more.</summary>
    public void ShowProblem(string text, Exception? error = null)
    {
        if (_problemsShown.Add(text))
            AddNotice(text + @" Details are in Data\crash.log.", isError: true, error);
    }
}
