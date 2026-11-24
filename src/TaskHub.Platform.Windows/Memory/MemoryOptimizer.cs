using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskHub.Platform.Windows.Memory;

/// <summary>
/// Win32 working-set memory governor for TaskHub.
/// Flushes unneeded working set pages back to the Windows memory manager
/// and runs compacting GC when the app is idle or minimized to tray.
/// </summary>
public static class MemoryOptimizer
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern int EmptyWorkingSet(IntPtr hwProc);

    /// <summary>
    /// Forces full GC collection and compaction, then signals the Windows kernel
    /// to release unused working set pages to standby list.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            var handle = Process.GetCurrentProcess().Handle;
            _ = SetProcessWorkingSetSize(handle, -1, -1);
            _ = EmptyWorkingSet(handle);
        }
        catch
        {
            // Best effort; never fail if security restricts working set calls
        }
    }

    /// <summary>Returns the current physical memory footprint (Working Set) in MB.</summary>
    public static double GetCurrentMemoryMb()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.WorkingSet64 / (1024.0 * 1024.0);
    }
}
