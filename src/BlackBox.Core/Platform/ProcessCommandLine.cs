using System.Runtime.InteropServices;

namespace BlackBox.Core.Platform;

/// <summary>
/// The command line another process was started with: on Windows from the process itself (the
/// same user's processes, or any when BlackBox runs as administrator), on Linux from /proc.
/// Null where it can't be read.
/// </summary>
public static class ProcessCommandLine
{
    public static string? Of(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        if (!OperatingSystem.IsWindows())
            return null;
        var process = OpenProcess(QueryLimitedInformation, false, pid);
        if (process == 0)
            return null;
        try
        {
            // The first call says how big the answer is.
            NtQueryInformationProcess(process, CommandLineInformation, 0, 0, out var size);
            if (size <= 0 || size > 1 << 20)
                return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(process, CommandLineInformation, buffer, size, out _) != 0)
                    return null;
                var text = Marshal.PtrToStructure<UnicodeString>(buffer);
                return text.Buffer == 0 ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// The value of <paramref name="name"/> (like "--user-data-dir") in a command line: after
    /// "name=", to the closing quote when the argument or its value is quoted, else to the next space.
    /// </summary>
    public static string? Switch(string commandLine, string name)
    {
        var at = commandLine.IndexOf(name + "=", StringComparison.Ordinal);
        if (at < 0)
            return null;
        var start = at + name.Length + 1;
        if (start < commandLine.Length && commandLine[start] == '"')
        {
            var end = commandLine.IndexOf('"', start + 1);
            return end < 0 ? commandLine[(start + 1)..] : commandLine[(start + 1)..end];
        }
        if (at > 0 && commandLine[at - 1] == '"')
        {
            var end = commandLine.IndexOf('"', start);
            return end < 0 ? commandLine[start..] : commandLine[start..end];
        }
        var space = commandLine.IndexOfAny([' ', '\t'], start);
        return space < 0 ? commandLine[start..] : commandLine[start..space];
    }

    private const int CommandLineInformation = 60;
    private const uint QueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int infoClass, nint info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
