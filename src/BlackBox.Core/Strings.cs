namespace BlackBox.Core;

internal static class Strings
{
    /// <summary>The text cut to <paramref name="max"/> characters, with "…" where it was cut.</summary>
    public static string Clip(this string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
