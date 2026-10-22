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
        var updated = dialog.Settings;
        // The server, key, model and context size live in the top bar, not in the dialog.
        updated.Server = _settings.Server;
        updated.Window = _settings.Window;
        _settings = updated;
        OnPropertyChanged(nameof(ApprovalNote));
        SaveSettings();
        // Written only when the user changed the list: a dialog left open while the agent added a
        // server would otherwise save the list without it.
        if (dialog.McpConfigText != dialog.McpConfigBase)
        {
            try
            {
                // What changed in the file meanwhile stays, unless the user changed the same server.
                var text = McpConfig.Merge(dialog.McpConfigBase, McpConfig.Load(_paths.McpConfigFile), dialog.McpConfigText);
                McpConfig.Save(_paths.McpConfigFile, text);
                if (IsBusy) _mcpRestartPending = true;
                else _mcpConnect = ConnectMcpAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddNotice($"The MCP server list could not be saved: {ex.Message}", isError: true);
            }
        }
        await CheckServerAsync();
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
            if (!_saveFailed) AddNotice($"Settings could not be saved, so they last only until BlackBox closes: {ex.Message}", isError: true);
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
            AddNotice($"The MCP server list could not be read, so no MCP tools are available: {ex.Message} Fix it in Settings.", isError: true);
            servers = [];
        }
        await Task.Run(() => _mcp.ConnectAsync(servers, CancellationToken.None));
        foreach (var s in _mcp.Statuses.Where(s => s.State == McpServerState.Failed))
            AddNotice($"MCP server \"{s.Name}\" is not available: {s.Error} (Settings)", isError: true);
    }

    private void AddNotice(string text, bool isError) => Items.Add(new NoticeItem(text, isError));

    private readonly HashSet<string> _problemsShown = [];

    /// <summary>Something no code caught (see <see cref="CrashLog"/>), said in the chat once, with where to read more.</summary>
    public void ShowProblem(string text)
    {
        if (_problemsShown.Add(text))
            AddNotice(text + @" Details are in Data\crash.log.", isError: true);
    }
}
