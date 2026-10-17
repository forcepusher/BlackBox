using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Comfy;

public enum ComfyInstallKind
{
    /// <summary>The official Windows build: python_embeded next to the ComfyUI folder.</summary>
    Portable,
    /// <summary>A manual install with its own virtual environment (venv or .venv).</summary>
    Venv,
    /// <summary>A manual install run with the Python on the PATH.</summary>
    SystemPython,
    /// <summary>The ComfyUI Desktop app, which starts its own server.</summary>
    Desktop,
}

/// <summary>A ComfyUI installation on this PC.</summary>
/// <param name="Root">Where its start command runs: the portable folder, or the ComfyUI folder itself.</param>
/// <param name="CodeDir">The folder with main.py, models\, custom_nodes\, input\ and output\ (the Desktop app: its data folder).</param>
/// <param name="Program">The Python that runs it (the Desktop app: its exe).</param>
public sealed record ComfyInstall(string Root, string CodeDir, string Program, ComfyInstallKind Kind)
{
    /// <summary>The installation a folder belongs to: the portable folder, the ComfyUI folder or anything with ComfyUI\main.py in it.</summary>
    public static ComfyInstall? FromFolder(string folder)
    {
        try
        {
            folder = Path.GetFullPath(folder.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (IsCodeDir(folder))
            return ForCodeDir(folder);
        foreach (var name in new[] { "ComfyUI", "comfyui" })
            if (IsCodeDir(Path.Combine(folder, name)))
                return ForCodeDir(Path.Combine(folder, name));
        return null;
    }

    private static bool IsCodeDir(string dir) =>
        File.Exists(Path.Combine(dir, "main.py")) && File.Exists(Path.Combine(dir, "nodes.py")) && Directory.Exists(Path.Combine(dir, "comfy"));

    private static ComfyInstall ForCodeDir(string code)
    {
        var parent = Path.GetDirectoryName(code);
        if (parent is not null && File.Exists(Path.Combine(parent, "python_embeded", "python.exe")))
            return new(parent, code, Path.Combine(parent, "python_embeded", "python.exe"), ComfyInstallKind.Portable);
        foreach (var dir in new[] { code, parent })
        {
            if (dir is null) continue;
            foreach (var venv in new[] { "venv", ".venv", "env" })
            {
                var python = OperatingSystem.IsWindows() ? Path.Combine(dir, venv, "Scripts", "python.exe") : Path.Combine(dir, venv, "bin", "python");
                if (File.Exists(python))
                    return new(code, code, python, ComfyInstallKind.Venv);
            }
        }
        return new(code, code, OperatingSystem.IsWindows() ? "python.exe" : "python3", ComfyInstallKind.SystemPython);
    }

    /// <summary>
    /// Looks for ComfyUI on this PC: the Desktop app, then <paramref name="firstLookIn"/> (BlackBox's
    /// Data/tools, where it installs one), the folders launchers keep it in (Stability Matrix,
    /// Pinokio), every fixed drive and the user's own folders, a few levels deep
    /// (C:\ComfyUI_windows_portable, D:\AI\ComfyUI, …): portable builds, and installs of
    /// one's own with a venv or the system's Python. Gives up after <paramref name="budget"/>; the
    /// one used most recently comes first.
    /// </summary>
    public static IReadOnlyList<ComfyInstall> Find(TimeSpan budget, CancellationToken ct, IEnumerable<string>? firstLookIn = null, IEnumerable<string>? scan = null)
    {
        var found = new List<ComfyInstall>();
        if (scan is null && Desktop() is { } desktop)
            found.Add(desktop);

        var giveUp = DateTime.UtcNow + budget;
        var roots = new List<string>(firstLookIn ?? []);
        if (scan is not null)
            roots.AddRange(scan);
        else
        {
            roots.AddRange(LauncherFolders());
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var special in new[] { "Desktop", "Downloads", "Documents" })
                roots.Add(Path.Combine(home, special));
            roots.Add(home);
            try
            {
                roots.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName));
            }
            catch (IOException) { }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Dir, int Depth)>(roots.Where(Directory.Exists).Select(r => (r, 0)));
        void Add(string code)
        {
            var install = ForCodeDir(code);
            if (!found.Any(f => f.CodeDir.Equals(install.CodeDir, StringComparison.OrdinalIgnoreCase)))
                found.Add(install);
        }
        while (queue.Count > 0 && DateTime.UtcNow < giveUp)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            if (!seen.Add(dir))
                continue;
            if (IsCodeDir(dir))
            {
                Add(dir);
                continue;
            }
            if (depth >= 3)
            {
                // A portable folder on the last level looked at (D:\AI\Tools\ComfyUI_windows_portable):
                // its ComfyUI folder is one deeper, and was missed.
                if (IsCodeDir(Path.Combine(dir, "ComfyUI")))
                    Add(Path.Combine(dir, "ComfyUI"));
                continue;
            }
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }
            foreach (var child in children)
                if (!Skip(Path.GetFileName(child)))
                    queue.Enqueue((child, depth + 1));
        }
        return found.OrderByDescending(LastUsed).ToList();

        static bool Skip(string name) =>
            name.StartsWith('$') || name.StartsWith('.') ||
            name is "Windows" or "ProgramData" or "System Volume Information" or "Recovery" or "PerfLogs" or "node_modules" or "site-packages"
                or "AppData" or "Program Files" or "Program Files (x86)" or "MSOCache" or "Intel" or "AMD" or "NVIDIA" or "Config.Msi" or "OneDriveTemp"
                or "models" or "output" or "input" or "custom_nodes" or "python_embeded" or "venv" or "__pycache__";
    }

    /// <summary>
    /// Where launchers put the ComfyUI they install, deeper than a drive is looked through or inside
    /// AppData, which isn't: Stability Matrix's Packages folder, Pinokio's api folder.
    /// </summary>
    private static IEnumerable<string> LauncherFolders()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StabilityMatrix", "Packages");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "pinokio", "api");
        string[] drives;
        try
        {
            drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToArray();
        }
        catch (IOException)
        {
            drives = [];
        }
        foreach (var drive in drives)
        {
            yield return Path.Combine(drive, "pinokio", "api");
            // Stability Matrix's portable mode keeps its data beside it.
            yield return Path.Combine(drive, "StabilityMatrix", "Data", "Packages");
        }
    }

    /// <summary>The ComfyUI Desktop app, if it is installed: its data folder is in its config.json.</summary>
    private static ComfyInstall? Desktop()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var exe = new[] { @"Programs\ComfyUI\ComfyUI.exe", @"Programs\@comfyorgcomfyui-electron\ComfyUI.exe" }
            .Select(p => Path.Combine(local, p))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ComfyUI", "ComfyUI.exe"))
            .FirstOrDefault(File.Exists);
        if (exe is null)
            return null;
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ComfyUI", "config.json");
        string? basePath = null;
        try
        {
            basePath = (string?)JsonNode.Parse(File.ReadAllText(config))?["basePath"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return new(Path.GetDirectoryName(exe)!, basePath ?? Path.GetDirectoryName(exe)!, exe, ComfyInstallKind.Desktop);
    }

    /// <summary>When it was last used: its output (or user) folder's last change.</summary>
    private static DateTime LastUsed(ComfyInstall install)
    {
        try
        {
            return new[] { "output", "user" }.Select(d => Path.Combine(install.CodeDir, d)).Where(Directory.Exists)
                .Select(Directory.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}

/// <summary>
/// Starting and stopping a ComfyUI server on this PC. A server BlackBox starts runs hidden, with
/// its output in a log file, apart from BlackBox's own processes: a job still in its queue when
/// BlackBox closes gets finished (<see cref="ComfyHub"/> closes the server when the task is over).
/// </summary>
public static partial class ComfyProcess
{
    /// <summary>The port ComfyUI uses unless told otherwise (the Desktop app: 8000).</summary>
    public const int DefaultPort = 8188, DesktopPort = 8000;

    /// <summary>
    /// Starts <paramref name="install"/> listening on <paramref name="port"/> and waits until it
    /// answers. <paramref name="arguments"/> replaces the usual ones (a restart keeps what the
    /// server ran with). Throws with the end of its log when it doesn't come up.
    /// </summary>
    public static async Task StartAsync(ComfyInstall install, ComfyApi api, string logFile, IReadOnlyList<string>? arguments, TimeSpan timeout,
        Action<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        Process process;
        if (install.Kind == ComfyInstallKind.Desktop)
        {
            // The app shows its own window and starts the server itself.
            process = Process.Start(new ProcessStartInfo(install.Program) { UseShellExecute = true, WorkingDirectory = install.Root })!;
        }
        else
        {
            var args = arguments?.ToList() ?? DefaultArguments(install, api.BaseUrl.Port);
            var command = new StringBuilder().Append('"').Append(install.Program).Append('"');
            foreach (var a in args)
                command.Append(' ').Append(Quote(a));
            command.Append(" > \"").Append(logFile).Append("\" 2>&1");
            // A server whose output goes to a file writes it in the console's code page by default,
            // and a custom node printing a character outside it crashes the server: UTF-8, then.
            // Started through the shell, hidden, it shares no handles with BlackBox, so a console
            // waiting for BlackBox's output doesn't wait for the server's as well.
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/d /s /c \"set PYTHONIOENCODING=utf-8&& set PYTHONUTF8=1&& " + command + "\"")
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = install.Root,
                }
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "PYTHONIOENCODING=utf-8 PYTHONUTF8=1 exec " + command + " < /dev/null" }, UseShellExecute = false, WorkingDirectory = install.Root };
            process = Process.Start(psi)!;
        }

        using var _ = process;
        progress?.Invoke("Starting ComfyUI…");
        var started = DateTime.UtcNow;
        var up = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (await api.TrySystemStatsAsync(ct, TimeSpan.FromSeconds(2)).ConfigureAwait(false) is not null)
                {
                    up = true;
                    return;
                }
                if (install.Kind != ComfyInstallKind.Desktop && process.HasExited)
                    throw new ComfyApiException($"ComfyUI stopped while starting (exit code {process.ExitCode}). The end of its log ({logFile}):\n{Tail(logFile, 40)}");
                if (DateTime.UtcNow - started > timeout)
                    throw new ComfyApiException($"ComfyUI did not answer at {api.BaseUrl} within {timeout.TotalMinutes:0} minutes of starting. The end of its log ({logFile}):\n{Tail(logFile, 40)}");
                progress?.Invoke($"Starting ComfyUI… {(DateTime.UtcNow - started).TotalSeconds:0} s");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // One that didn't come up (it took too long, or the user pressed Stop) would go on
            // running hidden, as nobody's server that no task closes, holding the port the next
            // start needs. The Desktop app's window the user sees and closes.
            if (!up && install.Kind != ComfyInstallKind.Desktop)
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
        }
    }

    /// <summary>
    /// How the official launchers start it (run_nvidia_gpu.bat, or run_cpu.bat on a PC without a
    /// graphics card its PyTorch can use), on the given port, without opening the browser:
    /// --windows-standalone-build opens ComfyUI's page in it at every start, and a server BlackBox
    /// runs hidden popped up an Edge window on the laptop each time.
    /// </summary>
    public static List<string> DefaultArguments(ComfyInstall install, int port)
    {
        var args = new List<string>();
        if (install.Kind == ComfyInstallKind.Portable)
            args.Add("-s");
        args.Add(Path.Combine(install.CodeDir, "main.py"));
        if (ComfyHardware.NeedsCpu(install))
            args.Add("--cpu");
        if (install.Kind == ComfyInstallKind.Portable)
            args.Add("--windows-standalone-build");
        if (port != DefaultPort)
        {
            args.Add("--port");
            args.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return Quietly(args);
    }

    /// <summary>The arguments with the browser kept closed (a restart reuses the ones a server ran with, which may open it).</summary>
    public static List<string> Quietly(List<string> args)
    {
        if (!args.Contains("--disable-auto-launch"))
            args.Add("--disable-auto-launch");
        return args;
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// <paramref name="preferred"/>, or server-2.log, server-3.log… when it is still held open: by a
    /// server that is going down, whose output would otherwise keep the new one from starting at all.
    /// </summary>
    public static string FreeLogFile(string preferred)
    {
        for (var n = 1; n <= 9; n++)
        {
            var path = n == 1 ? preferred : Path.Combine(Path.GetDirectoryName(preferred)!, $"{Path.GetFileNameWithoutExtension(preferred)}-{n}{Path.GetExtension(preferred)}");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                return path;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return preferred;
    }

    /// <summary>The last <paramref name="lines"/> lines of a log file, or a note that there is none.</summary>
    public static string Tail(string file, int lines)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');
            return string.Join("\n", all.TakeLast(lines)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "(no log)";
        }
    }

    /// <summary>
    /// Stops whatever listens on the server's port on this PC (with the processes it started), and
    /// waits until it is gone. Returns the program that was listening and false when nothing was.
    /// </summary>
    public static async Task<(bool Stopped, string? Program)> StopAsync(ComfyApi api, CancellationToken ct)
    {
        if (ListeningProcess(api.BaseUrl.Port) is not { } pid)
            return (false, null);
        string? program = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            try { program = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // gone already
        }
        catch (TimeoutException)
        {
            throw new ComfyApiException($"ComfyUI (process {pid}) did not stop within 30 seconds.");
        }
        // The port is free a moment after the process is gone.
        for (var i = 0; i < 20 && ListeningProcess(api.BaseUrl.Port) is not null; i++)
            await Task.Delay(250, ct).ConfigureAwait(false);
        return (true, program);
    }

    /// <summary>The process listening on a TCP port on this PC (from netstat), or null.</summary>
    public static int? ListeningProcess(int port)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p TCP") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return ListeningPid(text, port);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return null;
    }

    /// <summary>
    /// The process listening on <paramref name="port"/> in netstat -ano's output, told by the remote
    /// address a listening socket has (0.0.0.0:0) rather than by its state, which is in the PC's
    /// language: a German Windows says ABHÖREN, an Italian one IN ASCOLTO. Matched by LISTENING,
    /// BlackBox on such a PC never knew the server it had started, so it left it running after the task.
    /// </summary>
    internal static int? ListeningPid(string netstat, int port)
    {
        foreach (Match m in NetstatListen().Matches(netstat))
            if (int.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var p) && p == port &&
                int.TryParse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out var pid))
                return pid;
        return null;
    }

    [GeneratedRegex(@"^\s*TCP\s+\S+:([0-9]+)\s+(?:0\.0\.0\.0|\[::\]):0\s.*?\s([0-9]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex NetstatListen();
}
