namespace BlackBox.Core;

public static class Strings
{
    /// <summary>The text cut to <paramref name="max"/> characters, with "…" where it was cut.</summary>
    internal static string Clip(this string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    /// <summary>The text with its first letter a capital: "lines 12-18" as "Lines 12-18".</summary>
    public static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A size in bytes as people read it: "512 bytes", "40 KB", "3.4 MB", "40 MB", "4.8 GB".</summary>
    public static string Bytes(long bytes) =>
        bytes < 1024 ? $"{bytes} bytes"
        : bytes < 1 << 20 ? $"{bytes / 1024} KB"
        : bytes < 1L << 30 ? $"{bytes / 1048576.0:0.#} MB"
        : $"{bytes / 1073741824.0:0.#} GB";
}
