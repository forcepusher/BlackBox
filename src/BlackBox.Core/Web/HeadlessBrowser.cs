using System.Diagnostics;
using System.Text;

namespace BlackBox.Core.Web;

/// <summary>
/// The Chromium browser already on the PC (Edge, which every Windows has, or Chrome/Chromium),
/// run headless to load one page and print its DOM. Used where a plain HTTP request gets nowhere:
/// Bing's results (it serves non-browsers results that ignore most of the query), pages built by
/// JavaScript, and sites that turn away non-browsers. Nothing is downloaded or installed; the
/// browser profile lives in BlackBox's own Data folder, not the user's.
/// </summary>
public sealed class HeadlessBrowser(string executable, string profileDirectory)
{
    private const string UserAgent = WebHttp.UserAgent + " Edg/140.0.0.0";

    /// <summary>
    /// One page at a time on a profile: two browsers on one fight over its lock. Kept per profile, not
    /// per instance, as every BlackBox window's agent has an instance of its own on the same profile.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim Gate => Gates.GetOrAdd(Path.GetFullPath(profileDirectory), _ => new SemaphoreSlim(1, 1));

    /// <summary>The browser found on this PC, or null when there is none.</summary>
    public static HeadlessBrowser? Find(string profileDirectory) =>
        FindExecutable() is { } exe ? new HeadlessBrowser(exe, profileDirectory) : null;

    /// <summary>The Chromium browser's program on this PC (Edge first), or null when there is none.</summary>
    internal static string? FindExecutable() => FindExecutables().FirstOrDefault();

    /// <summary>Every Chromium browser on this PC, Edge first.</summary>
    internal static IReadOnlyList<string> FindExecutables() => Candidates().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     }.Where(r => r.Length > 0))
            {
                yield return Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
                yield return Path.Combine(root, @"Google\Chrome\Application\chrome.exe");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
        }
        else
        {
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
            foreach (var name in new[] { "microsoft-edge", "google-chrome", "google-chrome-stable", "chromium", "chromium-browser" })
                foreach (var dir in path)
                    yield return Path.Combine(dir, name);
        }
    }

    /// <summary>Loads <paramref name="url"/> and returns its HTML as the browser built it.</summary>
    /// <param name="runScripts">Let the page's scripts run for up to 5 s of page time first, for pages that build their content in script.</param>
    public async Task<string> GetDomAsync(Uri url, bool runScripts, CancellationToken ct)
    {
        var gate = Gate;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(profileDirectory);
            var psi = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            // --do-not-de-elevate: started by a BlackBox run as administrator, the browser would start
            // a copy of itself that isn't and quit at once, leaving no page here to read.
            foreach (var arg in new[]
                     {
                         "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                         "--disable-extensions", "--disable-sync", "--mute-audio", "--hide-scrollbars", "--do-not-de-elevate",
                         $"--user-data-dir={profileDirectory}", $"--user-agent={UserAgent}", "--lang=en-US",
                     })
                psi.ArgumentList.Add(arg);
            if (runScripts)
                psi.ArgumentList.Add("--virtual-time-budget=5000");
            psi.ArgumentList.Add("--dump-dom");
            psi.ArgumentList.Add(url.ToString());

            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct); // drained so the browser never blocks on a full pipe
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                throw new WebPageException($"The browser did not finish loading {url.Host} in 30 seconds.");
            }
            var html = await stdout.ConfigureAwait(false);
            if (html.Length == 0)
            {
                // Nothing back: where Edge's (or Chrome's) HeadlessModeEnabled policy is off, it quits with
                // 0 and only a line on its error output says why. --dump-dom works headless only.
                var said = await SaidAsync(stderr).ConfigureAwait(false);
                if (said?.Contains("Headless mode is disallowed", StringComparison.OrdinalIgnoreCase) == true)
                    throw new WebPageException("The browser won't run headless on this PC (its HeadlessModeEnabled policy is off), so pages that need one can't be loaded here.");
                // Taken for a page without text, or a search without results: "No results", and the
                // model wrote its query again and again. A page loaded is never empty (<html>…).
                var last = said?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                throw new WebPageException($"The browser gave back no page (exit code {process.ExitCode}" +
                                           (last is null ? ")." : $"; it said: {(last.Length > 200 ? last[..200] + "…" : last)})."));
            }
            return html;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>What the browser wrote to its error output, if it has finished writing within a moment (a helper it started may hold the pipe).</summary>
    private static async Task<string?> SaidAsync(Task<string> errorOutput)
    {
        try { return await errorOutput.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException) { return null; }
    }
}
