using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TaskHub.Notifications;

/// <summary>
/// The few Win32/DWM calls the banner needs. Hand-written [LibraryImport]s (not CsWin32) because the
/// DWM attribute calls take untyped buffers; these signatures keep the call sites simple and safe.
/// </summary>
internal static partial class Native
{
    public static readonly nint HwndTopmost = -1;

    public const int GwlExStyle = -20;
    public const long WsExToolWindow = 0x00000080;
    public const long WsExNoActivate = 0x08000000;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpNoOwnerZOrder = 0x0200;

    public const int WmNcActivate = 0x0086;

    public const int DwmwaUseImmersiveDarkMode = 20;
    public const int DwmwaBorderColor = 34;
    /// <summary>DWMWA_COLOR_DEFAULT: let Windows choose the border colour.</summary>
    public const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwaSystemBackdropType = 38;
    public const int DwmwcpRound = 2;
    public const int DwmsbtNone = 1;
    public const int DwmsbtTransientWindow = 3; // acrylic

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    /// <summary>A monitor's work area (excludes the taskbar) in physical pixels, and its DPI scale.</summary>
    public readonly record struct MonitorArea(nint Handle, Rect Work, double Scale);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point pt, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, int msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    /// <summary>
    /// True while a full-screen app, game or presentation is running (Windows' own "busy" check,
    /// the one that holds back its toasts).
    /// </summary>
    public static bool UserIsBusy()
    {
        if (SHQueryUserNotificationState(out var state) != 0) return false;
        return state is 2 or 3 or 4; // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE
    }

    /// <summary>COLORREF (0x00BBGGRR) from RGB.</summary>
    public static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public static void SetExtendedStyle(nint hwnd, long add, long remove)
    {
        var ex = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        ex = (ex | add) & ~remove;
        SetWindowLongPtr(hwnd, GwlExStyle, (nint)ex);
    }

    public static bool SetDwmInt(nint hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;

    /// <summary>The monitor under the mouse pointer.</summary>
    public static MonitorArea MonitorUnderCursor()
    {
        GetCursorPos(out var p);
        var monitor = MonitorFromPoint(p, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        return new MonitorArea(monitor, info.Work, scale);
    }

    /// <summary>Windows 11 22H2 (build 22621) added DWMWA_SYSTEMBACKDROP_TYPE (acrylic/mica).</summary>
    public static bool SupportsSystemBackdrop => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Windows 11 (build 22000) added rounded-corner preferences.</summary>
    public static bool SupportsRoundedCorners => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Whether apps should use light mode (Settings → Personalisation → Colours).</summary>
    public static bool AppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
    }
}
