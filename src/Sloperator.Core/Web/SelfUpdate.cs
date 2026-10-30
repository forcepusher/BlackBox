using System.Diagnostics;

namespace Sloperator.Core.Web;

/// <summary>
/// The files of Sloperator updating itself, beside the running exe: the new version downloads to
/// &lt;exe&gt;.new, and goes in place of the exe by two renames, the old one kept as &lt;exe&gt;.old
/// (a running exe can be renamed, not deleted) until the next start deletes it.
/// </summary>
public static class SelfUpdate
{
    public const string Repository = "forcepusher/Sloperator";

    /// <summary>The file each release carries, made by scripts/publish.ps1.</summary>
    public const string Asset = "Sloperator.exe";

    public static string NewFile(string exe) => exe + ".new";

    public static string OldFile(string exe) => exe + ".old";

    /// <summary>Whether <paramref name="release"/> is a later version than <paramref name="running"/> and has the exe to update to.</summary>
    public static bool Newer(GitHubRelease release, Version running) =>
        release.Asset is not null && IsNewer(release, running);

    /// <summary>Whether <paramref name="release"/>'s tag is a later version than <paramref name="running"/>, exe or not.</summary>
    public static bool IsNewer(GitHubRelease release, Version running) =>
        GitHubReleases.VersionOf(release.Tag) is { } version && version > GitHubReleases.Normal(running);

    /// <summary>An exe's file version, as Explorer shows it; null when it has none or can't be read.</summary>
    public static Version? FileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileMajorPart + info.FileMinorPart + info.FileBuildPart + info.FilePrivatePart == 0 ? null
                : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// What an earlier update left beside <paramref name="exe"/>: the old exe, and a new one or its
    /// half download that wasn't put in place. Whatever is still held (by the old version, still
    /// ending) stays for the next start.
    /// </summary>
    public static void CleanUp(string exe)
    {
        foreach (var file in new[] { OldFile(exe), NewFile(exe), NewFile(exe) + ".part" })
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Whether Sloperator may write files in <paramref name="folder"/>: not in Program Files, nor in a folder locked to it.</summary>
    public static bool CanWrite(string folder)
    {
        var probe = Path.Combine(folder, $".sloperator-write-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Puts &lt;exe&gt;.new in place of <paramref name="exe"/>, the exe going to &lt;exe&gt;.old.
    /// When the second rename fails, the first is undone, and the exe is as it was.
    /// </summary>
    /// <returns>Null once the new exe is in place; else why not.</returns>
    public static string? Swap(string exe)
    {
        var (fresh, old) = (NewFile(exe), OldFile(exe));
        if (!File.Exists(fresh))
            return $"{Path.GetFileName(fresh)} isn't there.";
        try
        {
            // One a start couldn't delete, as the version before still held it.
            File.Delete(old);
            File.Move(exe, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
        try
        {
            File.Move(fresh, exe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Move(old, exe); }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException) { return $"{ex.Message} Putting the old exe back failed too: {back.Message}"; }
            return ex.Message;
        }
    }

    /// <summary>Undoes <see cref="Swap"/>: the new exe that didn't start goes back to &lt;exe&gt;.new, the old one in place.</summary>
    public static bool Unswap(string exe)
    {
        try
        {
            File.Move(exe, NewFile(exe), overwrite: true);
            File.Move(OldFile(exe), exe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
