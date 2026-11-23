namespace TaskHub.Core.Configuration;

/// <summary>
/// Strongly typed view of config.toml. TOML keys are the snake_case form of these
/// property names (e.g. <c>ShowMainWindowOnLaunch</c> → <c>show_main_window_on_launch</c>).
/// Every property has a default, so a partial or empty file is valid.
/// </summary>
public sealed class TaskHubOptions
{
    public GeneralOptions General { get; set; } = new();
    public HotkeyOptions Hotkeys { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public ApiOptions Api { get; set; } = new();
    public ReminderOptions Reminders { get; set; } = new();
    public MaintenanceOptions Maintenance { get; set; } = new();
    public NotificationOptions Notifications { get; set; } = new();
    public AutomationOptions Automation { get; set; } = new();
    public LauncherOptions Launcher { get; set; } = new();

    private AutomationConfig _automationConfig = AutomationConfig.Empty;

    /// <summary>Rules, folder watchers and shortcuts (<c>[[rules]]</c>, <c>[[watch]]</c>, <c>[[shortcuts]]</c>).
    /// A method, not a property, so the typed TOML mapper leaves it alone.</summary>
    public AutomationConfig GetAutomationConfig() => _automationConfig;

    public void SetAutomationConfig(AutomationConfig config) => _automationConfig = config ?? AutomationConfig.Empty;
}

public sealed class GeneralOptions
{
    /// <summary>Show the main window when launched manually. Autostart always starts in the tray.</summary>
    public bool ShowMainWindowOnLaunch { get; set; } = true;
}

public sealed class HotkeyOptions
{
    public string ShowMainWindow { get; set; } = "Ctrl+Alt+T";
    public string QuickAdd { get; set; } = "Ctrl+Alt+N";
    public string Launcher { get; set; } = "Ctrl+Alt+Space";
    /// <summary>Toggle Do Not Disturb. Off by default.</summary>
    public string ToggleDnd { get; set; } = "";
    /// <summary>Turn what's on the clipboard (text, image, files) into a task.</summary>
    public string CaptureClipboard { get; set; } = "Ctrl+Alt+V";
    /// <summary>Drag a rectangle on screen; the screenshot becomes a task.</summary>
    public string CaptureScreenshot { get; set; } = "Ctrl+Alt+S";

    /// <summary>Hotkey id → gesture text. Ids are what <c>hotkey.pressed</c> events carry.</summary>
    public IReadOnlyDictionary<string, string> AsMap() => new Dictionary<string, string>
    {
        [HotkeyIds.ShowMainWindow] = ShowMainWindow ?? "",
        [HotkeyIds.QuickAdd] = QuickAdd ?? "",
        [HotkeyIds.Launcher] = Launcher ?? "",
        [HotkeyIds.ToggleDnd] = ToggleDnd ?? "",
        [HotkeyIds.CaptureClipboard] = CaptureClipboard ?? "",
        [HotkeyIds.CaptureScreenshot] = CaptureScreenshot ?? "",
    };
}

public static class HotkeyIds
{
    public const string ShowMainWindow = "show_main_window";
    public const string QuickAdd = "quick_add";
    public const string Launcher = "launcher";
    public const string ToggleDnd = "toggle_dnd";
    public const string CaptureClipboard = "capture_clipboard";
    public const string CaptureScreenshot = "capture_screenshot";
}

public sealed class LoggingOptions
{
    public static readonly string[] Levels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    public string MinimumLevel { get; set; } = "Information";
    public int RetainedDays { get; set; } = 14;
}

public sealed class ApiOptions
{
    /// <summary>Loopback port for the local API (127.0.0.1 only).</summary>
    public int Port { get; set; } = 47821;
}

public sealed class ReminderOptions
{
    /// <summary>Minutes before the due time for new tasks' reminders (0 = at due). Empty = no reminder by default.</summary>
    public List<int> DefaultOffsets { get; set; } = [0];

    /// <summary>Tasks due on a date without a time are reminded at this local time ("" = never).</summary>
    public string AllDayTime { get; set; } = "09:00";

    /// <summary>Default snooze length in minutes.</summary>
    public int SnoozeMinutes { get; set; } = 10;

    /// <summary>More missed reminders than this (after sleep/shutdown) are grouped into one summary.</summary>
    public int GroupMissedAbove { get; set; } = 3;

    /// <summary>Parsed <see cref="AllDayTime"/> (a method, so the TOML mapper ignores it).</summary>
    public TimeOnly? GetAllDayTime() =>
        TimeOnly.TryParseExact(AllDayTime, ["HH:mm", "H:mm"], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var t) ? t : null;
}

public sealed class MaintenanceOptions
{
    /// <summary>Daily database backups to keep.</summary>
    public int BackupKeepDays { get; set; } = 14;

    /// <summary>Trashed tasks are deleted permanently after this many days.</summary>
    public int TrashRetentionDays { get; set; } = 30;
}
