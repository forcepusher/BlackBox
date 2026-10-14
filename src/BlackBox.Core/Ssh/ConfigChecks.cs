using System.Text;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Ssh;

/// <summary>
/// Checks a config file on a server right after BlackBox wrote it, with the program that reads
/// it, as a careful admin does before a restart: visudo for sudoers, sshd -t (and what sshd will
/// really use) for sshd, nginx -t, systemd-analyze verify for units, findmnt --verify for fstab,
/// the rules cron and logrotate apply to their files, netplan generate, nft -c and
/// iptables-restore --test for firewall rules, and PostgreSQL's view of pg_hba.conf and of its settings. A broken sudoers or sshd config can lock everyone out, a broken fstab can stop the
/// next boot; most of the traps here passed a model by unnoticed in the benchmark (a sudoers rule
/// for a group written without %, an sshd drop-in that sorts after the one that turns passwords on).
/// </summary>
public static partial class ConfigChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>The kind of check for a path, or null for a file nobody checks.</summary>
    internal static string? KindOf(string path) => path switch
    {
        "/etc/sudoers" => "sudoers",
        _ when path.StartsWith("/etc/sudoers.d/", StringComparison.Ordinal) => "sudoers",
        "/etc/ssh/sshd_config" => "sshd",
        _ when path.StartsWith("/etc/ssh/sshd_config.d/", StringComparison.Ordinal) => "sshd",
        _ when path.StartsWith("/etc/nginx/", StringComparison.Ordinal) => "nginx",
        _ when UnitPath().IsMatch(path) => "unit",
        "/etc/fstab" => "fstab",
        _ when path.StartsWith("/etc/cron.d/", StringComparison.Ordinal) => "crond",
        _ when path.StartsWith("/etc/logrotate.d/", StringComparison.Ordinal) => "logrotate",
        _ when path.StartsWith("/etc/netplan/", StringComparison.Ordinal) && (path.EndsWith(".yaml") || path.EndsWith(".yml")) => "netplan",
        "/etc/nftables.conf" or "/etc/sysconfig/nftables.conf" => "nft",
        _ when path.StartsWith("/etc/nftables.d/", StringComparison.Ordinal) || path.StartsWith("/etc/nftables/", StringComparison.Ordinal) => "nft",
        "/etc/iptables/rules.v4" or "/etc/iptables/rules.v6" or "/etc/sysconfig/iptables" or "/etc/sysconfig/ip6tables" => "iptables",
        _ when path.EndsWith("/pg_hba.conf", StringComparison.Ordinal) => "pghba",
        _ when path.EndsWith("/postgresql.conf", StringComparison.Ordinal) ||
               path.StartsWith("/etc/postgresql/", StringComparison.Ordinal) && path.Contains("/conf.d/") && path.EndsWith(".conf") => "pgconf",
        _ => null,
    };

    /// <summary>
    /// The config files a shell command seems to write: a checked path in a command that also has
    /// a writing word in it (tee, a redirection, sed -i, cp, mv, install, ln, chmod, chown, rm).
    /// </summary>
    public static IReadOnlyList<string> PathsIn(string command)
    {
        // 2>/dev/null and 2>&1 write nothing anyone checks, and neither does quoted text without a
        // config path in it (an echo's "-> yes", a sed expression; unlike sh -c 'cmd > /etc/fstab').
        if (!Writes().IsMatch(Quoted().Replace(Discards().Replace(command, ""), m => ConfigPath().IsMatch(m.Value) ? m.Value : "''")))
            return [];
        return ConfigPath().Matches(command).Select(m => m.Value.TrimEnd('.', ',', ';', ')', '\'', '"'))
            .Where(p => KindOf(p) is not null).Distinct().ToList();
    }

    /// <summary>
    /// Runs the checks for <paramref name="paths"/> on the server as root and returns a short note
    /// for the tool result ("Checked after writing: sshd -t passed. Note: ..."), or null when there
    /// is nothing to check or the checks can't run (no sudo without a password, no bash).
    /// </summary>
    public static async Task<string?> RunAsync(SshHost host, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var script = Script(paths);
        if (script is null)
            return null;
        ExecResult r;
        try
        {
            r = await host.SudoAsync("bash -s", script, Timeout, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
        return Note(r.Output);
    }

    /// <summary>One bash script with a check per path (a whole-config check such as nginx -t once).</summary>
    internal static string? Script(IReadOnlyCollection<string> paths)
    {
        var calls = new List<string>();
        var sshd = new List<string>();
        foreach (var path in paths.Distinct())
        {
            var q = SshHost.Quote(path);
            switch (KindOf(path))
            {
                case "sudoers": calls.Add($"chk_sudoers {q}"); break;
                case "sshd": sshd.Add(q); break;
                case "nginx": if (!calls.Contains("chk_nginx")) calls.Add("chk_nginx"); break;
                case "unit": calls.Add($"chk_unit {q}"); break;
                case "fstab": if (!calls.Contains("chk_fstab")) calls.Add("chk_fstab"); break;
                case "crond": calls.Add($"chk_crond {q}"); break;
                case "logrotate": calls.Add($"chk_logrotate {q}"); break;
                case "netplan": if (!calls.Contains("chk_netplan")) calls.Add("chk_netplan"); break;
                case "pghba": if (!calls.Contains("chk_pghba")) calls.Add("chk_pghba"); break;
                case "pgconf": if (!calls.Contains("chk_pgconf")) calls.Add("chk_pgconf"); break;
                case "nft": if (!calls.Contains("chk_nft")) calls.Add("chk_nft"); break;
                case "iptables": calls.Add($"chk_iptables {q}"); break;
            }
        }
        if (sshd.Count > 0)
            calls.Add("chk_sshd " + string.Join(' ', sshd));
        return calls.Count == 0 ? null : Functions + string.Join('\n', calls) + "\n";
    }

    /// <summary>The note from the script's OK, ERR and NOTE lines.</summary>
    internal static string? Note(string output)
    {
        var ok = new List<string>();
        var problems = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("OK ", StringComparison.Ordinal)) ok.Add(line[3..]);
            else if (line.StartsWith("ERR ", StringComparison.Ordinal) || line.StartsWith("NOTE ", StringComparison.Ordinal))
                problems.Add(line[(line.IndexOf(' ') + 1)..]);
        }
        if (ok.Count == 0 && problems.Count == 0)
            return null;
        var sb = new StringBuilder("Checked after writing: ");
        sb.Append(ok.Count > 0 ? string.Join(", ", ok.Distinct()) + " passed." : "");
        foreach (var p in problems.Distinct())
            sb.Append(sb[^1] == ' ' ? "" : " ").Append("Problem: ").Append(p.TrimEnd('.')).Append('.');
        return sb.ToString().Trim();
    }

    // The checks, one bash function each; they print OK, ERR (the program rejects the config) or NOTE lines.
    private const string Functions = """
        chk_sudoers() { local f=$1 o m out n; [ -e "$f" ] || return
          out=$(visudo -cf "$f" 2>&1) && echo "OK visudo -cf $f" || echo "ERR visudo -cf $f: $(echo "$out" | head -3 | tr '\n' ' ')"
          case "$f" in /etc/sudoers.d/*) case "$(basename "$f")" in *.*|*~) echo "NOTE sudo ignores $f: in /etc/sudoers.d it skips names with a dot or a trailing ~";; esac;; esac
          read -r o m <<<"$(stat -c '%U %a' "$f")"
          [ "$o" = root ] || echo "NOTE $f belongs to $o: sudo refuses sudoers files not owned by root"
          [ $(( 8#$m & 8#022 )) -eq 0 ] || echo "NOTE $f is writable by group or others (mode $m): sudo refuses it; use 0440"
          grep -vE '^[[:space:]]*(#|$|Defaults|[A-Z_]+_Alias|%|@)' "$f" | awk '{print $1}' | sort -u | while read -r n; do
            case "$n" in *[!a-zA-Z0-9_.-]*) continue;; esac
            getent passwd "$n" >/dev/null || { getent group "$n" >/dev/null && echo "NOTE in $f, $n is a group, not a user: a rule for a group needs %$n"; }
          done; }
        chk_sshd() { local out eff f k v e
          out=$(sshd -t 2>&1) && echo "OK sshd -t" || { echo "ERR sshd -t (restarting sshd now could lock everyone out): $(echo "$out" | head -3 | tr '\n' ' ')"; return; }
          eff=$(sshd -T 2>/dev/null)
          for f in "$@"; do [ -f "$f" ] || continue
            awk 'tolower($1)=="match"{exit} /^[[:space:]]*[A-Za-z]/{print tolower($1), tolower($2)}' "$f" | while read -r k v; do
              [ "$k" = challengeresponseauthentication ] && k=kbdinteractiveauthentication
              case "$k" in passwordauthentication|permitrootlogin|pubkeyauthentication|kbdinteractiveauthentication|permitemptypasswords|port|x11forwarding|maxauthtries) ;; *) continue;; esac
              e=$(echo "$eff" | awk -v k="$k" '$1==k {print $2; exit}')
              [ "$v" = without-password ] && v=prohibit-password; [ "$e" = without-password ] && e=prohibit-password
              [ -n "$e" ] && [ "$e" != "$v" ] && echo "NOTE $f sets $k $v, but sshd uses $k $e: a file or line it reads earlier sets it first (sshd keeps the first value)"
            done
          done; }
        chk_nginx() { local out; command -v nginx >/dev/null || return
          out=$(nginx -t 2>&1) && echo "OK nginx -t" || echo "ERR nginx -t: $(echo "$out" | grep -v 'test failed' | head -3 | tr '\n' ' ')"; }
        chk_unit() { local f=$1 u v out; [ -e "$f" ] || return; u=$(basename "$f"); v=$f
          case "$f" in *.d/*) u=$(basename "$(dirname "$f")" .d); v=$u;; esac
          out=$(systemd-analyze verify "$v" 2>&1 | grep -F "$u" | head -3)
          [ -n "$out" ] && echo "NOTE systemd-analyze verify $u: $(echo "$out" | tr '\n' ' ')" || echo "OK systemd-analyze verify $u"
          [ "$(systemctl show -p NeedDaemonReload --value "$u" 2>/dev/null)" = yes ] && echo "NOTE systemd hasn't read the new $u yet: run systemctl daemon-reload"; }
        chk_fstab() { local out; out=$(findmnt --verify --tab-file /etc/fstab 2>&1) && echo "OK findmnt --verify" ||
          echo "ERR findmnt --verify /etc/fstab (a bad fstab can stop the next boot): $(echo "$out" | grep -v '^$' | head -4 | tr '\n' ' ')"; }
        chk_crond() { local f=$1 o m n=""; [ -f "$f" ] || return
          case "$(basename "$f")" in *[!a-zA-Z0-9_-]*) n+="NOTE cron ignores $f: names in /etc/cron.d may only have letters, digits, - and _"$'\n';; esac
          read -r o m <<<"$(stat -c '%U %a' "$f")"
          [ "$o" = root ] || n+="NOTE cron ignores $f: it must belong to root"$'\n'
          [ $(( 8#$m & 8#022 )) -eq 0 ] || n+="NOTE cron ignores $f: it is writable by group or others (mode $m); use 0644"$'\n'
          grep -q $'\r' "$f" && n+="NOTE $f has Windows line endings, which cron can't read"$'\n'
          [ -z "$(tail -c1 "$f")" ] || n+="NOTE $f doesn't end with a newline: cron skips its last line"$'\n'
          command -v cron >/dev/null || command -v crond >/dev/null || n+="NOTE no cron daemon is installed here, so nothing runs $f"$'\n'
          [ -n "$n" ] && printf '%s' "$n" || echo "OK cron's rules for $f"; }
        chk_logrotate() { local f=$1 out; [ -f "$f" ] || return
          command -v logrotate >/dev/null || { echo "NOTE logrotate is not installed here, so nothing reads $f"; return; }
          out=$(logrotate -d "$f" 2>&1 | grep -iE '^error|: error|bad file mode' | head -3)
          [ -n "$out" ] && echo "NOTE logrotate -d $f: $(echo "$out" | tr '\n' ' ')" || echo "OK logrotate -d $f"; }
        chk_nft() { local out f=/etc/nftables.conf; command -v nft >/dev/null || return
          [ -f "$f" ] || f=/etc/sysconfig/nftables.conf; [ -f "$f" ] || return
          out=$(nft -c -f "$f" 2>&1) && echo "OK nft -c -f $f" || echo "ERR nft -c -f $f: $(echo "$out" | grep -v '^\s*\^' | head -4 | tr '\n' ' ')"
          systemctl is-enabled --quiet nftables 2>/dev/null || echo "NOTE nftables.service isn't enabled, so nothing loads $f at boot"; }
        chk_iptables() { local f=$1 t=iptables-restore out; [ -f "$f" ] || return
          case "$f" in *v6|*ip6tables) t=ip6tables-restore;; esac; command -v $t >/dev/null || return
          out=$($t --test < "$f" 2>&1) && echo "OK $t --test $f" || echo "ERR $t --test $f: $(echo "$out" | head -3 | tr '\n' ' ')"; }
        chk_netplan() { local out; command -v netplan >/dev/null || return
          out=$(netplan generate 2>&1) && echo "OK netplan generate" || echo "ERR netplan generate: $(echo "$out" | head -3 | tr '\n' ' ')"; }
        chk_pgpsql_failed() { case "$1" in
            *"invalid line "*) # Debian's and Ubuntu's psql wrapper reads the config first and stops at a line it can't parse.
              echo "ERR $(echo "$1" | grep -m1 'invalid line' | sed 's/^Error: //') (PostgreSQL can't parse this line: it won't start or reload with it)";;
            *"authentication failed"*|*"no password supplied"*|*"no pg_hba.conf entry"*)
              echo "NOTE the postgres superuser can't log in on the server ($(echo "$1" | head -1)): with no peer rule for it only a password works";;
            *) echo "NOTE psql as postgres fails here: $(echo "$1" | head -2 | tr '\n' ' ')";; esac; }
        chk_pghba() { local out m; command -v psql >/dev/null || return
          if ! out=$(sudo -u postgres psql -XtAc "select coalesce(string_agg(line_number || ': ' || error, '; '), '') from pg_hba_file_rules where error is not null" 2>&1); then
            chk_pgpsql_failed "$out"; return; fi
          [ -n "$out" ] && echo "ERR pg_hba.conf: $out"
          m=$(sudo -u postgres psql -XtAc "select auth_method from pg_hba_file_rules where type = 'local' and ('all' = any(database) or 'postgres' = any(database)) and ('all' = any(user_name) or 'postgres' = any(user_name)) order by line_number limit 1" 2>/dev/null)
          case "$m" in peer|trust|ident) echo "OK pg_hba.conf";; *) echo "NOTE pg_hba.conf: the first rule for local logins of postgres is ${m:-missing}, not peer; after a reload the postgres superuser can't log in on the server without a password";; esac; }
        chk_pgconf() { local out l; command -v psql >/dev/null || return
          # PostgreSQL reads its config files afresh for pg_file_settings: syntax errors and bad values
          # show there, and so does a setting only a restart applies (listen_addresses, port, ...).
          if ! out=$(sudo -u postgres psql -XtAc "select string_agg(case when f.error = 'setting could not be applied' and s.context = 'postmaster' then 'RESTART ' || f.name else 'BAD ' || f.sourcefile || ' line ' || f.sourceline || ': ' || coalesce(f.name || ': ', '') || f.error end, E'\n') from pg_file_settings f left join pg_settings s on s.name = f.name where f.error is not null" 2>&1); then
            chk_pgpsql_failed "$out"; return; fi
          [ -z "$out" ] && { echo "OK PostgreSQL's settings"; return; }
          while read -r l; do case "$l" in
            RESTART*) echo "NOTE ${l#RESTART } changes only when PostgreSQL restarts (a reload doesn't apply it)";;
            BAD*) echo "ERR PostgreSQL rejects ${l#BAD }";; esac; done <<<"$out"; }

        """;

    [GeneratedRegex(@"^/etc/systemd/system/[\w@:.-]+\.(service|timer|socket|path|mount)(\.d/[\w.-]+\.conf)?$")]
    private static partial Regex UnitPath();

    [GeneratedRegex(@"/etc/(sudoers(\.d/[\w.~-]+)?|ssh/sshd_config(\.d/[\w.-]+)?|nginx/[\w./-]+|systemd/system/[\w@:.-]+\.(service|timer|socket|path|mount)(\.d/[\w.-]+\.conf)?|fstab|cron\.d/[\w.-]+|logrotate\.d/[\w.-]+|netplan/[\w.-]+\.ya?ml|nftables\.conf|nftables(\.d)?/[\w.-]+|iptables/rules\.v[46]|sysconfig/(nftables\.conf|ip6?tables))|/[\w./-]*(pg_hba|postgresql)\.conf|/etc/postgresql/[\w./-]+/conf\.d/[\w.-]+\.conf")]
    private static partial Regex ConfigPath();

    [GeneratedRegex(@"\btee\b|>|\bsed\s+(-\w*i|--in-place)|\bcp\b|\bmv\b|\binstall\b|\bln\b|\bchmod\b|\bchown\b|\brm\b|\bvisudo\b")]
    private static partial Regex Writes();

    [GeneratedRegex(@"\d?>&\d|&?\d?>>?\s*/dev/null")]
    private static partial Regex Discards();

    [GeneratedRegex(@"'[^']*'|""(\\.|[^""\\])*""")]
    private static partial Regex Quoted();
}
