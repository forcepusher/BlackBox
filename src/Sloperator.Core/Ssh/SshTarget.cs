namespace Sloperator.Core.Ssh;

/// <summary>
/// Where to log in. Read from what the model writes: "user@host", "user@host:port",
/// "ssh://user@host:port", "[v6address]:port", or a Host alias from ~/.ssh/config, whose HostName,
/// User, Port and IdentityFile fill in what the text leaves out, as with OpenSSH.
/// </summary>
public sealed record SshTarget(string User, string Host, int Port, string? IdentityFile = null)
{
    /// <summary>One connection per user, host and port.</summary>
    public string Key => $"{User}@{Host}:{Port}".ToLowerInvariant();

    public override string ToString() => Port == 22 ? $"{User}@{Host}" : $"{User}@{Host}:{Port}";

    /// <summary>Reads a target; throws <see cref="ArgumentException"/> when the user name is missing or the text is no address.</summary>
    /// <param name="sshConfig">The OpenSSH client configuration to look aliases up in (~/.ssh/config by default).</param>
    public static SshTarget Parse(string text, string? sshConfig = null)
    {
        var s = text.Trim();
        if (s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            s = s[6..];
        s = s.TrimEnd('/');
        string? user = null;
        var at = s.LastIndexOf('@');
        if (at >= 0)
        {
            user = s[..at];
            s = s[(at + 1)..];
        }
        int? port = null;
        if (s.StartsWith('[') && s.IndexOf(']') is var close and > 0)
        {
            if (close + 2 < s.Length && s[close + 1] == ':' && int.TryParse(s[(close + 2)..], out var p))
                port = p;
            s = s[1..close];
        }
        else if (s.Count(c => c == ':') == 1 && int.TryParse(s[(s.IndexOf(':') + 1)..], out var p))
        {
            port = p;
            s = s[..s.IndexOf(':')];
        }
        if (s.Length == 0 || s.Any(char.IsWhiteSpace))
            throw new ArgumentException($"'{text}' is not a host. Give it as user@host, user@host:port or user@192.168.1.10.");

        var config = SshConfig.Lookup(s, sshConfig ?? DefaultConfigPath);
        user ??= config.User;
        if (string.IsNullOrWhiteSpace(user))
            throw new ArgumentException($"Say which user to log in as: user@{s}.");
        return new SshTarget(user, config.HostName ?? s, port ?? config.Port ?? 22, config.IdentityFile);
    }

    private static string DefaultConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
}

/// <summary>The few settings of an OpenSSH client config (~/.ssh/config) that say where and as whom to log in.</summary>
internal sealed record SshConfig(string? HostName, string? User, int? Port, string? IdentityFile)
{
    /// <summary>
    /// The settings for <paramref name="alias"/>: as in OpenSSH, every Host block whose pattern
    /// matches contributes, and the first value given for a setting wins. Match blocks and
    /// Include lines are skipped.
    /// </summary>
    public static SshConfig Lookup(string alias, string path)
    {
        string? hostName = null, user = null, identity = null;
        int? port = null;
        if (!File.Exists(path))
            return new(null, null, null, null);
        var applies = true; // settings before the first Host line apply to every host
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var split = line.IndexOfAny([' ', '\t', '=']);
            if (split < 0)
                continue;
            var key = line[..split].ToLowerInvariant();
            var value = line[(split + 1)..].Trim().TrimStart('=').Trim().Trim('"');
            switch (key)
            {
                case "host":
                    // A block applies when one of its patterns matches and none of its !patterns does.
                    var patterns = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    applies = patterns.Any(p => !p.StartsWith('!') && Matches(alias, p)) &&
                              !patterns.Any(p => p.StartsWith('!') && Matches(alias, p[1..]));
                    break;
                case "match":
                    applies = false;
                    break;
                case "hostname" when applies:
                    hostName ??= value.Replace("%h", alias);
                    break;
                case "user" when applies:
                    user ??= value;
                    break;
                case "port" when applies && int.TryParse(value, out var p):
                    port ??= p;
                    break;
                case "identityfile" when applies:
                    identity ??= ExpandHome(value);
                    break;
            }
        }
        return new(hostName, user, port, identity);
    }

    private static bool Matches(string text, string pattern) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, text, ignoreCase: true);

    private static string ExpandHome(string path) =>
        path.StartsWith("~/") || path.StartsWith("~\\")
            ? Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]))
            : path;
}
