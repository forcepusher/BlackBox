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

    public string Executable { get; } = executable;

    /// <summary>One page at a time: two browsers on one profile fight over its lock.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The browser found on this PC, or null when there is none.</summary>
    public static HeadlessBrowser? Find(string profileDirectory) =>
        Candidates().FirstOrDefault(File.Exists) is { } exe ? new HeadlessBrowser(exe, profileDirectory) : null;

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
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(profileDirectory);
            var psi = new ProcessStartInfo(Executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[]
                     {
                         "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                         "--disable-extensions", "--disable-sync", "--mute-audio", "--hide-scrollbars",
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
            _ = process.StandardError.ReadToEndAsync(ct); // drained so the browser never blocks on a full pipe
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
            return await stdout.ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
