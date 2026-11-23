namespace TaskHub.Abstractions.Contracts;

// Wire contracts shared by the local API, the CLI, plugins and external apps.
// Enums travel as lowercase strings ("open", "high") so any language can use them.

public static class TaskStatusNames
{
    public const string Open = "open";
    public const string Completed = "completed";
}

public static class PriorityNames
{
    public const string None = "none";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Urgent = "urgent";

    public static readonly IReadOnlyList<string> All = [None, Low, Medium, High, Urgent];
}

/// <summary>Views understood by GET /v1/tasks?view=…</summary>
public static class TaskViewNames
{
    public const string Inbox = "inbox";
    public const string Today = "today";
    public const string Upcoming = "upcoming";
    public const string Overdue = "overdue";
    public const string All = "all";
    public const string Completed = "completed";
    public const string Trash = "trash";

    public static readonly IReadOnlyList<string> Values = [Inbox, Today, Upcoming, Overdue, All, Completed, Trash];
}

/// <summary>A task as seen by clients.</summary>
/// <param name="DueDate">Local calendar date the task is due, if any.</param>
/// <param name="DueTime">Local wall-clock time ("HH:mm"), if the task is due at a specific time.</param>
/// <param name="DueAtUtc">The exact instant, when <paramref name="DueTime"/> is set.</param>
/// <param name="Recurrence">iCalendar RRULE, e.g. FREQ=WEEKLY;BYDAY=MO.</param>
/// <param name="ReminderOffsets">Minutes before due to remind (0 = at due).</param>
/// <param name="RepeatReminderMinutes">Keep reminding every N minutes after due until done.</param>
/// <param name="ReminderActions">Actions run with the reminder, one per line (e.g. "open https://…").</param>
/// <param name="Attachments">Full paths of attached files.</param>
public sealed record TaskDto(
    Guid Id,
    string Title,
    string? Notes,
    string Status,
    string Priority,
    DateOnly? DueDate,
    string? DueTime,
    DateTimeOffset? DueAtUtc,
    Guid? ListId,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset? DeletedUtc,
    string? Recurrence = null,
    IReadOnlyList<int>? ReminderOffsets = null,
    int? RepeatReminderMinutes = null,
    DateTimeOffset? NextReminderUtc = null,
    string? ReminderActions = null,
    IReadOnlyList<string>? Attachments = null)
{
    /// <summary>Human description of <see cref="Recurrence"/>, e.g. "Every Monday".</summary>
    public string? RecurrenceText { get; init; }
}

public sealed record TaskListDto(Guid Id, string Name, string? Color, int OpenCount, string? Icon = null);

public sealed record TagDto(string Name, int OpenCount);

/// <summary>
/// POST /v1/tasks. Either <see cref="Text"/> (quick-add syntax, e.g. "Pay rent tomorrow 9am #home !high")
/// or <see cref="Title"/> plus optional fields. Explicit fields override what the text parsed to.
/// </summary>
public sealed record CreateTaskRequest
{
    public string? Text { get; init; }
    public string? Title { get; init; }
    public string? Notes { get; init; }
    public DateOnly? DueDate { get; init; }
    /// <summary>"HH:mm" local time. Requires a due date (defaults to today if omitted).</summary>
    public string? DueTime { get; init; }
    public string? Priority { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public Guid? ListId { get; init; }
    /// <summary>"every monday", "daily", or an RRULE.</summary>
    public string? Recurrence { get; init; }
    /// <summary>Minutes before due; omit for the user's default, [] for none.</summary>
    public IReadOnlyList<int>? ReminderOffsets { get; init; }
    public int? RepeatReminderMinutes { get; init; }
    /// <summary>Actions to run with the reminder, one per line ("open https://meet.example.com/x").</summary>
    public string? ReminderActions { get; init; }
}

/// <summary>POST /v1/tasks/{id}/snooze. Omit minutes for the user's default.</summary>
public sealed record SnoozeRequest(int? Minutes = null);

public sealed record CreateListRequest(string Name, string? Color = null);

/// <summary>PATCH /v1/lists/{id}. Omitted fields stay; color "" / icon "" clear them.</summary>
public sealed record UpdateListRequest(string? Name = null, string? Color = null, string? Icon = null);

public sealed record UndoResultDto(string Description);

public sealed record StatusDto(string Version, int ProcessId, DateTimeOffset Now, string TimeZone);

public sealed record ApiError(string Error);

/// <summary>JSON export/import file format.</summary>
public sealed record ExportDocument
{
    public const string FormatName = "taskhub";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;
    public DateTimeOffset ExportedUtc { get; init; }
    public IReadOnlyList<ExportList> Lists { get; init; } = [];
    public IReadOnlyList<TaskDto> Tasks { get; init; } = [];
}

public sealed record ExportList(Guid Id, string Name, string? Color, double SortOrder, DateTimeOffset CreatedUtc, string? Icon = null);

public sealed record ImportResult(int ListsAdded, int ListsUpdated, int TasksAdded, int TasksUpdated);

// ---------------------------------------------------------------- notifications

public sealed record NotifyButtonDto(string Action, string Label);

/// <summary>
/// POST /v1/notifications — show a banner. Button clicks (and replies) come back as
/// <c>notification.action</c> events carrying the returned id.
/// </summary>
public sealed record NotifyRequest
{
    public string Title { get; init; } = "";
    public string? Body { get; init; }
    public string? Detail { get; init; }
    /// <summary>A Segoe Fluent Icons glyph or an emoji.</summary>
    public string? Icon { get; init; }
    /// <summary>low, normal, high, urgent.</summary>
    public string? Priority { get; init; }
    /// <summary>Seconds on screen; 0 = until dismissed; omit for the user's setting.</summary>
    public int? Seconds { get; init; }
    public IReadOnlyList<NotifyButtonDto>? Buttons { get; init; }
    public bool? Sound { get; init; }
    /// <summary>Show a reply box; the typed text arrives as the action (parsed like snooze/done/dates).</summary>
    public bool? Reply { get; init; }
}

public sealed record NotifyResult(Guid Id);

public sealed record DndDto(bool Enabled);

public sealed record NotificationHistoryDto(
    Guid Id, DateTimeOffset At, string Title, string? Body, string Kind, string Priority, string Status, string? Outcome, string Source);
