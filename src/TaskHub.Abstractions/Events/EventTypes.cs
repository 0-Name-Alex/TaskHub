namespace TaskHub.Abstractions.Events;

/// <summary>Well-known event type names. See docs/events.md for payloads.</summary>
public static class EventTypes
{
    // App lifecycle
    public const string AppStarted = "app.started";
    public const string AppStopping = "app.stopping";
    /// <summary>A second launch forwarded its command line to the running instance.</summary>
    public const string AppActivated = "app.activated";

    // Config
    public const string ConfigChanged = "config.changed";
    public const string ConfigInvalid = "config.invalid";

    // Hotkeys
    public const string HotkeyPressed = "hotkey.pressed";
    public const string HotkeyFailed = "hotkey.failed";

    // Tasks & lists (payload: TaskChangedEvent / ListChangedEvent)
    public const string TaskCreated = "task.created";
    public const string TaskUpdated = "task.updated";
    public const string TaskCompleted = "task.completed";
    public const string TaskReopened = "task.reopened";
    public const string TaskDeleted = "task.deleted";
    public const string TaskRestored = "task.restored";
    public const string ListCreated = "list.created";
    public const string ListUpdated = "list.updated";
    public const string DataImported = "data.imported";
    public const string UndoApplied = "undo.applied";

    // Reminders (payload: ReminderEvent / ReminderSummaryEvent / ReminderSnoozedEvent)
    public const string ReminderDue = "reminder.due";
    /// <summary>Several reminders were missed while asleep/off; grouped into one summary.</summary>
    public const string ReminderMissed = "reminder.missed";
    public const string ReminderSnoozed = "reminder.snoozed";

    // Notifications (payload: NotificationShownEvent / NotificationActionEvent)
    public const string NotificationShown = "notification.shown";
    public const string NotificationAction = "notification.action";
    public const string NotificationClosed = "notification.closed";
    /// <summary>Do Not Disturb turned on/off (payload: DndChangedEvent).</summary>
    public const string DndChanged = "dnd.changed";

    // System (no payload unless noted)
    public const string SystemResumed = "system.resumed";
    public const string SystemSuspended = "system.suspended";
    public const string SystemTimeChanged = "system.time_changed";
    public const string SystemLocked = "system.locked";
    public const string SystemUnlocked = "system.unlocked";
    /// <summary>No input for [automation] idle_minutes (payload: IdleEvent).</summary>
    public const string SystemIdle = "system.idle";
    /// <summary>Input again after being idle (payload: IdleEvent with how long it lasted).</summary>
    public const string SystemActive = "system.active";
    /// <summary>Payload: BatteryEvent.</summary>
    public const string BatteryLow = "system.battery.low";
    /// <summary>Plugged in / unplugged (payload: BatteryEvent).</summary>
    public const string PowerAc = "system.power.ac";
    public const string PowerBattery = "system.power.battery";
    /// <summary>Payload: DiskLowEvent.</summary>
    public const string DiskLow = "system.disk.low";

    // Maintenance (payload: BackupCreatedEvent)
    public const string BackupCreated = "backup.created";

    // Camera / microphone (payload: DeviceUsageEvent)
    public const string CameraStarted = "camera.started";
    public const string CameraStopped = "camera.stopped";
    public const string MicrophoneStarted = "microphone.started";
    public const string MicrophoneStopped = "microphone.stopped";
    /// <summary>The camera was switched off (privacy key/switch) or unplugged (payload: CameraSwitchEvent).</summary>
    public const string CameraDisabled = "camera.disabled";
    /// <summary>The camera was switched back on or plugged in (payload: CameraSwitchEvent).</summary>
    public const string CameraEnabled = "camera.enabled";

    // Automation
    /// <summary>A watched folder got a new/changed/deleted/renamed file (payload: FileDetectedEvent).</summary>
    public const string FileDetected = "file.detected";
    /// <summary>Clipboard captured as a task (payload: CaptureEvent).</summary>
    public const string ClipboardCaptured = "clipboard.captured";
    /// <summary>Screenshot captured as a task (payload: CaptureEvent).</summary>
    public const string ScreenshotCaptured = "screenshot.captured";
    /// <summary>A rule ran (payload: RuleRanEvent).</summary>
    public const string RuleRan = "automation.rule_ran";
    /// <summary>A task's reminder actions ran (payload: RuleRanEvent, Rule = "task:&lt;id&gt;").</summary>
    public const string ReminderActionsRan = "automation.reminder_actions_ran";
}

/// <summary>Well-known values for <see cref="EventEnvelope.Source"/>.</summary>
public static class EventSources
{
    public const string Core = "core";
    public const string Ui = "ui";
    public const string Platform = "platform";
    public const string Instance = "instance";
    public const string Api = "api";
    public const string Cli = "cli";
    public const string Watcher = "watcher";
    public const string Launcher = "launcher";
    public const string Capture = "capture";

    /// <summary>Source of everything a rule causes: <c>rule:&lt;name&gt;</c>. Rules never react to these (no loops).</summary>
    public static string Rule(string name) => "rule:" + name;

    public const string RulePrefix = "rule:";
}

public sealed record AppStartedEvent(string Version, int ProcessId);
public sealed record AppActivatedEvent(IReadOnlyList<string> Args);
public sealed record ConfigInvalidEvent(string Path, string Error);
public sealed record HotkeyPressedEvent(string HotkeyId);
public sealed record HotkeyFailedEvent(string HotkeyId, string Gesture, string Error);
public sealed record TaskChangedEvent(Contracts.TaskDto Task);

/// <param name="Missed">True if it came due while the app was asleep/off (or more than 2 minutes ago).</param>
public sealed record ReminderEvent(Contracts.TaskDto Task, DateTimeOffset ScheduledUtc, bool Missed);
public sealed record ReminderSummaryEvent(IReadOnlyList<Contracts.TaskDto> Tasks);
public sealed record ReminderSnoozedEvent(Contracts.TaskDto Task, DateTimeOffset Until);
public sealed record NotificationShownEvent(Guid NotificationId, string Title);
/// <param name="Action">done, snooze, snooze:&lt;minutes&gt;, open, dismiss — or a custom id.</param>
public sealed record NotificationActionEvent(Guid NotificationId, string Action, IReadOnlyList<Guid> TaskIds);
public sealed record BackupCreatedEvent(string Path, long Bytes);
public sealed record NotificationClosedEvent(Guid NotificationId, string Reason);
public sealed record DndChangedEvent(bool Enabled);
public sealed record ListChangedEvent(Contracts.TaskListDto List);
public sealed record UndoAppliedEvent(string Description);
public sealed record DataImportedEvent(Contracts.ImportResult Result);

public sealed record IdleEvent(int IdleMinutes);
public sealed record BatteryEvent(int Percent, bool OnBattery);
public sealed record DiskLowEvent(string Drive, double FreeGb, double TotalGb);

/// <param name="Watch">Name of the [[watch]] entry.</param>
/// <param name="Change">created, changed, deleted or renamed.</param>
/// <param name="Extension">Lowercase, with the dot (".pdf"); empty for folders/no extension.</param>
public sealed record FileDetectedEvent(string Watch, string Change, string Path, string Name, string Extension, string Folder, long? Size, string? OldPath = null);

public sealed record CaptureEvent(Contracts.TaskDto Task, string Kind);

/// <param name="Rule">Rule name (or "task:&lt;id&gt;" for a task's reminder actions).</param>
/// <param name="Trigger">Event type that started it, or "manual"/"hotkey".</param>
public sealed record RuleRanEvent(string Rule, string Trigger, bool Ok, string? Message);

/// <param name="Device">camera or microphone.</param>
/// <param name="App">Friendly app name, e.g. "Zoom".</param>
/// <param name="AppId">Executable path, or the Store app's package name.</param>
public sealed record DeviceUsageEvent(string Device, string App, string AppId);

/// <param name="Cameras">How many cameras Windows can use now (IR/face-login cameras count too).</param>
public sealed record CameraSwitchEvent(int Cameras);
