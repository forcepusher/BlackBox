using System.Globalization;

namespace Sloperator.Core.Projects;

/// <summary>
/// What a folder holds, for the Code editor panel's tree: its folders first, then its files, each in
/// Explorer's order (case aside, "file2" before "file10"). What VS Code leaves out of its tree is left
/// out (.git, .svn, .hg, CVS, .DS_Store, Thumbs.db); hidden files such as .gitignore are shown.
/// </summary>
public static class FolderListing
{
    public sealed record Entry(string Name, string Path, bool IsFolder);

    private static readonly HashSet<string> LeftOut = new(StringComparer.OrdinalIgnoreCase) { ".git", ".svn", ".hg", "CVS", ".DS_Store", "Thumbs.db" };

    /// <summary>The folder's entries; none when it can't be read (gone, no access).</summary>
    public static IReadOnlyList<Entry> List(string folder)
    {
        var folders = new List<Entry>();
        var files = new List<Entry>();
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                if (LeftOut.Contains(entry.Name))
                    continue;
                if (entry is DirectoryInfo)
                    folders.Add(new Entry(entry.Name, entry.FullName, true));
                else
                    files.Add(new Entry(entry.Name, entry.FullName, false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
        folders.Sort(ByName);
        files.Sort(ByName);
        return [.. folders, .. files];
    }

    /// <summary>Explorer's order: case aside, numbers by their value.</summary>
    internal static int ByName(Entry a, Entry b) => Compare(a.Name, b.Name);

    internal static int Compare(string a, string b)
    {
        try
        {
            return string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            // Without ICU's collation (NLS mode): by the letters alone.
            return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        }
    }
}
