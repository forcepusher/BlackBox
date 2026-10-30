using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sloperator.Core.Web;

namespace Sloperator.App;

/// <summary>
/// Sloperator updating itself: at start and once an hour it asks GitHub for the newest release,
/// downloads a newer exe beside the running one, and the chat windows offer to restart into it
/// (see <see cref="SelfUpdate"/> for the files). Never while a run is going: a check due then waits
/// for it to end. One for all the windows, on the windows' thread.
/// </summary>
public sealed partial class Updater : ObservableObject
{
    private readonly Func<bool> _busy;
    private readonly string _checkFile, _rejectedFile;
    private readonly Func<string, Signature> _signature;
    private readonly DispatcherTimer _hourly;
    private bool _due, _checking;

    /// <param name="busy">Whether a run is going in any window.</param>
    /// <param name="checkFile">Where the time of the last check is kept, across starts (Data/update-check.txt).</param>
    /// <param name="rejectedFile">The version whose download wasn't kept, signed by another publisher (Data/update-rejected.txt).</param>
    /// <param name="signature">A file's Authenticode signature, as Windows checks it.</param>
    internal Updater(Func<bool> busy, string checkFile, string rejectedFile, Func<string, Signature> signature)
    {
        _busy = busy;
        _checkFile = checkFile;
        _rejectedFile = rejectedFile;
        _signature = signature;
        _hourly = new DispatcherTimer(SelfUpdate.Every, DispatcherPriority.Background, (_, _) => Due());
    }

    /// <summary>The running exe; null when it can't be told.</summary>
    private static string? Exe => Environment.ProcessPath;

    /// <summary>Only the single-file exe publish.ps1 makes updates itself: a debug or folder build would be replaced by the exe alone.</summary>
    private static bool Updatable => PortableRuntime.SingleFile && Exe is not null;

    private static Version Running =>
        (Exe is { } exe ? SelfUpdate.FileVersion(exe) : null) ?? typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>Checks at start and once an hour; off, Sloperator never asks GitHub by itself (the Check for updates button still does).</summary>
    public bool Automatic { get; set; }

    /// <summary>What the last check found, for Settings: "Checking…", "Up to date", "Sloperator 1.2.0 is ready".</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>A newer version is downloaded, and a restart puts it in place.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNews), nameof(LinkText))]
    private bool _isReady;

    /// <summary>A newer version is out, but Sloperator's folder can't take it (Program Files): it is for the user to download.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNews), nameof(LinkText))]
    private bool _isOut;

    /// <summary>The link to the release's page beside the news: what's in it, or where to download it from.</summary>
    public string LinkText => IsOut ? "Download" : "What's new";

    /// <summary>The chat windows' line: "Sloperator 1.2.0 is ready", "Sloperator 1.2.0 is out".</summary>
    [ObservableProperty]
    private string _news = "";

    /// <summary>The newer release's page, with what's new in it.</summary>
    [ObservableProperty]
    private Uri? _releasePage;

    public bool HasNews => IsReady || IsOut;

    /// <summary>
    /// The first window is up: what an earlier update left goes, and the first check is made a few
    /// seconds on, after the window's own start (its server check, its MCP servers), or, when the
    /// last start checked less than an hour ago, an hour after that check: once an hour across starts
    /// too, not at every start. The hourly checks go on from the first.
    /// </summary>
    internal void Start(bool automatic)
    {
        Automatic = automatic;
        if (Exe is { } exe)
            SelfUpdate.CleanUp(exe);
        if (!Updatable)
            return;
        DispatcherTimer.RunOnce(() =>
        {
            _hourly.Start();
            Due();
        }, SelfUpdate.FirstCheckIn(SelfUpdate.LastCheck(_checkFile), DateTime.UtcNow, TimeSpan.FromSeconds(5)));
    }

    /// <summary>A check is due: now, or once no run is going.</summary>
    private void Due()
    {
        if (!Automatic)
            return;
        if (_busy())
            _due = true;
        else
            _ = CheckAsync();
    }

    /// <summary>A run ended, and none is going: the check that came due meanwhile.</summary>
    internal void Idle()
    {
        if (_due && !_busy())
        {
            _due = false;
            Due();
        }
    }

    /// <summary>Asks GitHub for the newest release, and downloads it if it is newer and the folder takes it.</summary>
    public async Task CheckAsync()
    {
        if (_checking)
            return;
        if (!Updatable)
        {
            Status = "Only the downloaded Sloperator.exe updates itself";
            return;
        }
        _checking = true;
        Status = "Checking…";
        SelfUpdate.Checked(_checkFile, DateTime.UtcNow);
        try
        {
            var exe = Exe!;
            var release = await GitHubReleases.LatestAsync(SelfUpdate.Repository, SelfUpdate.Asset, CancellationToken.None);
            var running = Running;
            if (release is null)
            {
                Status = "GitHub didn't answer";
                return;
            }
            // A release whose exe isn't up yet (the build is still signing it) is no update until it is.
            if (!SelfUpdate.Newer(release, running))
            {
                Status = HasNews ? News : "Up to date";
                return;
            }
            var version = GitHubReleases.VersionOf(release.Tag)!;
            var name = $"Sloperator {version.ToString(3)}";
            if (IsReady && News == $"{name} is ready")
            {
                Status = News;
                return;
            }
            // Its download was signed by another publisher than this exe: not downloaded again. A later version is.
            if (SelfUpdate.Rejected(_rejectedFile) == version)
            {
                Status = $"{name} couldn't be verified";
                return;
            }
            if (!SelfUpdate.CanWrite(Path.GetDirectoryName(exe)!))
            {
                Show(release, $"{name} is out", ready: false);
                return;
            }
            Status = $"Downloading {name}…";
            var fresh = SelfUpdate.NewFile(exe);
            await Downloader.DownloadAsync(release.Asset!, fresh, release.Sha256, null, CancellationToken.None);
            // A signed Sloperator takes only an exe signed by its own publisher (Windows' check can go online, for revocation).
            var (verdict, why) = await Task.Run(() => SelfUpdate.Verify(exe, fresh, _signature));
            if (verdict != Verdict.Keep)
            {
                Discard(fresh);
                if (verdict == Verdict.Reject)
                    SelfUpdate.Reject(_rejectedFile, version);
                Status = $"{name} couldn't be verified";
                Core.ErrorLog.Write("Updates", $"The download of {name} wasn't kept{(verdict == Verdict.Reject ? "" : "; the next check tries again")}: {why}.");
                return;
            }
            if (SelfUpdate.FileVersion(fresh) is not { } got || got <= GitHubReleases.Normal(running))
            {
                Discard(fresh);
                Status = $"The download of {name} isn't a newer Sloperator";
                return;
            }
            Show(release, $"{name} is ready", ready: true);
        }
        catch (Exception ex) when (ex is DownloadException or IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>A download that isn't kept goes; it was written over the one a restart would have put in place, if any.</summary>
    private void Discard(string fresh)
    {
        File.Delete(fresh);
        if (IsReady)
        {
            IsReady = false;
            News = "";
        }
    }

    private void Show(GitHubRelease release, string news, bool ready)
    {
        ReleasePage = release.Page;
        News = news;
        Status = news;
        IsReady = ready;
        IsOut = !ready;
    }

    /// <summary>The downloaded version goes in place of the running exe, which keeps running from its new name. Null once it has; else why not.</summary>
    internal string? Swap()
    {
        if (!IsReady || Exe is not { } exe)
            return "No update is ready.";
        if (SelfUpdate.Swap(exe) is { } failed)
            return failed;
        IsReady = false;
        return null;
    }

    /// <summary>The new exe didn't start: the update goes back to waiting beside the running one.</summary>
    internal void Unswap()
    {
        if (Exe is { } exe && SelfUpdate.Unswap(exe))
            IsReady = true;
    }

    /// <summary>Sloperator ends without a restart: the downloaded version is in place for the next start.</summary>
    internal void SwapOnExit()
    {
        if (IsReady)
            Swap();
    }
}
