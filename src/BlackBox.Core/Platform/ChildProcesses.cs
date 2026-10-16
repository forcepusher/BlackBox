using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BlackBox.Core.Platform;

/// <summary>
/// Helper programs that must not outlive BlackBox (the headless browser ComfyUI's page runs in):
/// on Windows they go into a job object that the system closes, and so ends them, when BlackBox
/// ends, however it ends. Elsewhere they are stopped when the process exits normally.
/// </summary>
public static class ChildProcesses
{
    private static readonly Lazy<nint> Job = new(CreateJob);
    private static readonly List<Process> Others = [];

    /// <summary>Ties <paramref name="process"/> (and what it starts afterwards) to BlackBox's life.</summary>
    public static void Tie(Process process)
    {
        if (OperatingSystem.IsWindows() && Job.Value != 0)
        {
            try
            {
                if (AssignProcessToJobObject(Job.Value, process.Handle))
                    return;
            }
            catch (InvalidOperationException) { return; }
        }
        lock (Others)
        {
            if (Others.Count == 0)
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    lock (Others)
                        foreach (var p in Others)
                            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                            catch (Exception) { }
                };
            Others.Add(process);
        }
    }

    private static nint CreateJob()
    {
        if (!OperatingSystem.IsWindows())
            return 0;
        var job = CreateJobObjectW(0, null);
        if (job == 0)
            return 0;
        var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimits>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, ExtendedLimitInformation, buffer, (uint)size))
                return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return job; // kept open for the process's life: closing it ends the processes in it
    }

    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
}
