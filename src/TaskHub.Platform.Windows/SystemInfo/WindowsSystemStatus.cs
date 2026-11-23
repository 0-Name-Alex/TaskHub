using System.IO;
using System.Runtime.InteropServices;
using TaskHub.Core.Automation;

namespace TaskHub.Platform.Windows.SystemInfo;

/// <summary>Idle time (GetLastInputInfo), battery (GetSystemPowerStatus) and fixed-drive free space.</summary>
public sealed class WindowsSystemStatus : ISystemStatus
{
    public TimeSpan? GetIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!NativeMethods.GetLastInputInfo(ref info)) return null;
        // Both are 32-bit millisecond tick counts; unsigned subtraction handles the 49.7-day wrap.
        var idleMs = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    public PowerState? GetPower()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var s)) return null;
        const byte NoBattery = 128;
        const byte Unknown = 255;
        if (s.BatteryFlag == Unknown || (s.BatteryFlag & NoBattery) != 0) return null;
        int? percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        return new PowerState(OnBattery: s.ACLineStatus == 0, percent);
    }

    public IReadOnlyList<DriveSpace> GetFixedDrives()
    {
        var result = new List<DriveSpace>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                    result.Add(new DriveSpace(d.Name.TrimEnd('\\'), d.AvailableFreeSpace, d.TotalSize));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // drive went away / not accessible
            }
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetLastInputInfo(ref LastInputInfo info);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    }
}
