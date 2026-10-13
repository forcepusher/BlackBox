using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace BlackBox.Core.Ssh;

/// <summary>
/// The servers a chat is logged in to, one <see cref="SshHost"/> per user, host and port, kept
/// until the chat ends. Logs in with the key the model names, the keys in ~/.ssh, or a password
/// the user gave. Host keys are checked against BlackBox's own known_hosts (Data/known_hosts):
/// the first key a server shows is saved, and a different one later is refused, as OpenSSH does.
/// </summary>
public sealed class SshHosts(string knownHostsFile) : IDisposable
{
    private readonly Dictionary<string, SshHost> _hosts = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>How long a server that refuses connections (rebooting, say) is retried before giving up.</summary>
    internal static TimeSpan RetryFor { get; set; } = TimeSpan.FromSeconds(45);

    public IReadOnlyCollection<SshHost> Connected => _hosts.Values.Where(h => h.IsConnected).ToList();

    /// <summary>The host of the last successful GetAsync: where an ssh call without a host goes.</summary>
    public string? LastTarget { get; private set; }

    /// <summary>
    /// The logged-in host for <paramref name="target"/>, connecting first if needed. The notice
    /// says what a new connection found (the system, whether sudo asks for a password, a new
    /// host key), or that an old one was lost and a new one made; null for a connection reused.
    /// </summary>
    public async Task<(SshHost Host, string? Notice)> GetAsync(string target, string? keyFile, string? password, CancellationToken ct)
    {
        var t = SshTarget.Parse(target);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? lost = null;
            if (_hosts.TryGetValue(t.Key, out var known))
            {
                if (password is { Length: > 0 }) known.Password = password;
                if (known.IsConnected && (keyFile is null || known.LoginKeys.Contains(Path.GetFullPath(keyFile), StringComparer.OrdinalIgnoreCase)))
                {
                    LastTarget = target;
                    return (known, null);
                }
                if (known.IsConnected)
                {
                    // A key this connection didn't log in with (one just installed, say): a real
                    // login with that key alone. If the server refuses it, the old connection stays.
                    SshHost fresh;
                    string about;
                    try
                    {
                        (fresh, about) = await ConnectAsync(t, keyFile, password ?? known.Password, ct, keyOnly: true).ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        throw new IOException(ex.Message + " The connection made earlier, without this key, still works.", ex);
                    }
                    fresh.Password = known.Password;
                    known.Dispose();
                    _hosts[t.Key] = fresh;
                    LastTarget = target;
                    return (fresh, $"Logged in again with {keyFile} alone, so the server accepts that key; this is a new terminal. " + about);
                }
                known.Dispose();
                _hosts.Remove(t.Key);
                lost = "The earlier connection was lost (a reboot?), so this is a new one: the directory and variables start fresh.";
            }
            var (host, notice) = await ConnectAsync(t, keyFile, password, ct).ConfigureAwait(false);
            _hosts[t.Key] = host;
            LastTarget = target;
            return (host, lost is null ? notice : lost + " " + notice);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <param name="keyOnly">Log in with <paramref name="keyFile"/> alone (<paramref name="password"/> only unlocks it).</param>
    private async Task<(SshHost, string)> ConnectAsync(SshTarget t, string? keyFile, string? password, CancellationToken ct, bool keyOnly = false)
    {
        var auth = new List<AuthenticationMethod>();
        var keys = new List<IPrivateKeySource>();
        var offered = new List<string>();
        var keyErrors = new List<string>();
        foreach (var path in keyOnly ? [keyFile!] : KeyFiles(keyFile, t.IdentityFile))
        {
            try
            {
                keys.Add(password is { Length: > 0 } ? new PrivateKeyFile(path, password) : new PrivateKeyFile(path));
                offered.Add(Path.GetFullPath(path));
            }
            catch (SshPassPhraseNullOrEmptyException) { keyErrors.Add($"{path} is protected by a passphrase"); }
            catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (keyFile is not null && Path.GetFullPath(path) == Path.GetFullPath(keyFile))
                    throw new IOException($"Can't use the key {path}: {ex.Message}");
            }
        }
        if (keyFile is not null && !File.Exists(keyFile))
            throw new FileNotFoundException($"The key file {keyFile} does not exist.");
        if (keys.Count > 0)
            auth.Add(new PrivateKeyAuthenticationMethod(t.User, [.. keys]));
        if (password is { Length: > 0 } && !keyOnly)
        {
            auth.Add(new PasswordAuthenticationMethod(t.User, password));
            var kbd = new KeyboardInteractiveAuthenticationMethod(t.User);
            kbd.AuthenticationPrompt += (_, e) => { foreach (var p in e.Prompts) p.Response = password; };
            auth.Add(kbd);
        }
        if (auth.Count == 0)
            throw new IOException($"No way to log in to {t}: no key was given, none is in ~/.ssh{(keyErrors.Count > 0 ? " (" + string.Join("; ", keyErrors) + ")" : "")}, and no password.");

        var info = new ConnectionInfo(t.Host, t.Port, t.User, [.. auth]) { Timeout = TimeSpan.FromSeconds(15) };
        string? newKey = null;
        string? mismatch = null;
        var giveUp = DateTime.UtcNow + RetryFor;
        while (true)
        {
            var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(20) };
            client.HostKeyReceived += (_, e) =>
            {
                var shown = $"{e.HostKeyName} {Convert.ToBase64String(e.HostKey)}";
                var saved = KnownKey(t);
                if (saved is null)
                {
                    SaveKey(t, shown);
                    newKey = $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}";
                    e.CanTrust = true;
                }
                else
                {
                    e.CanTrust = saved == shown;
                    if (!e.CanTrust)
                        mismatch = $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}";
                }
            };
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or SshConnectionException or SshOperationTimeoutException or TimeoutException && mismatch is null && DateTime.UtcNow < giveUp && !ct.IsCancellationRequested)
            {
                // Refused or unreachable: often a server on its way back up from a reboot.
                client.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                client.Dispose();
                if (mismatch is not null)
                    throw new IOException($"{t.Host} showed a different host key ({mismatch}) than the one saved in {knownHostsFile} the first time. " +
                                          "That happens when the server was reinstalled, and also when someone is in between. Tell the user; " +
                                          "if they confirm the server was rebuilt, they can delete its line in that file.");
                throw ex switch
                {
                    SshAuthenticationException => new IOException($"{t} did not accept the login ({ex.Message}). " +
                        (keyFile is null ? "Check the user name, and give key_file if the user named a key." : "Check the user name and the key.")),
                    SocketException s => new IOException($"Can't reach {t.Host}:{t.Port}: {s.Message}"),
                    _ => new IOException($"Can't connect to {t}: {ex.Message}"),
                };
            }

            var host = new SshHost(client, t, password) { LoginKeys = offered };
            var about = await DescribeAsync(host, ct).ConfigureAwait(false);
            return (host, $"Connected to {t}: {about}" + (newKey is null ? "" : $" Its host key {newKey} was saved."));
        }
    }

    /// <summary>The system, and whether sudo asks for a password: what an admin checks first.</summary>
    private static async Task<string> DescribeAsync(SshHost host, CancellationToken ct)
    {
        try
        {
            var r = await host.ExecAsync(". /etc/os-release 2>/dev/null; printf '%s\\n' \"${PRETTY_NAME:-$(uname -s)}\"; uname -r; hostname", null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            var lines = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var sudo = await host.CheckSudoAsync(ct).ConfigureAwait(false)
                ? host.Password is null ? "sudo asks for a password" : "sudo asks for a password (BlackBox types the one given)"
                : "sudo works without a password";
            return lines.Length >= 3 ? $"{lines[0]}, kernel {lines[1]}, hostname {lines[2]}; {sudo}." : $"{sudo}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "logged in.";
        }
    }

    /// <summary>The named key first, then the one ~/.ssh/config gives, then the usual ones in ~/.ssh.</summary>
    private static IEnumerable<string> KeyFiles(string? keyFile, string? fromConfig)
    {
        if (keyFile is not null) yield return keyFile;
        if (fromConfig is not null && File.Exists(fromConfig)) yield return fromConfig;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
            if (Path.Combine(dir, name) is var p && File.Exists(p) && p != keyFile && p != fromConfig)
                yield return p;
    }

    // ---- known_hosts: "host keytype base64", or "[host]:port keytype base64" -------------

    private static string Name(SshTarget t) => t.Port == 22 ? t.Host.ToLowerInvariant() : $"[{t.Host.ToLowerInvariant()}]:{t.Port}";

    private string? KnownKey(SshTarget t)
    {
        if (!File.Exists(knownHostsFile))
            return null;
        var name = Name(t);
        foreach (var line in File.ReadLines(knownHostsFile))
        {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0].Split(',').Contains(name))
                return parts[1] + " " + parts[2].Trim();
        }
        return null;
    }

    private void SaveKey(SshTarget t, string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(knownHostsFile)!);
        File.AppendAllText(knownHostsFile, $"{Name(t)} {key}\n");
    }

    /// <summary>Logs out of every server (removing each terminal's private folder; at most 5 seconds per server).</summary>
    public void Dispose()
    {
        foreach (var h in _hosts.Values)
            h.Dispose();
        _hosts.Clear();
        LastTarget = null;
    }
}
