namespace TaskHub.Core.Domain;

public enum TaskItemStatus
{
    Open = 0,
    Completed = 1,
}

public enum Priority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Urgent = 4,
}

/// <summary>
/// A task. Persisted by the storage layer; mutated only through <see cref="Services.TaskService"/>.
/// </summary>
public sealed class TaskItem
{
    public const int MaxTitleLength = 500;
    public const int MaxNotesLength = 20_000;
    public const int MaxReminderActionsLength = 4000;
    public const int MaxAttachments = 30;

    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    public TaskItemStatus Status { get; set; }
    public Priority Priority { get; set; }

    /// <summary>Local calendar date the task is due.</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>Local wall-clock time; only set together with <see cref="DueDate"/>.</summary>
    public TimeOnly? DueTime { get; set; }

    /// <summary>The instant the task is due (DueDate + DueTime in the user's zone), when it has a time.</summary>
    public DateTimeOffset? DueAtUtc { get; set; }

    /// <summary>Null = Inbox.</summary>
    public Guid? ListId { get; set; }

    /// <summary>Lowercase, unique, without the leading '#'.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>iCalendar RRULE (e.g. <c>FREQ=WEEKLY;BYDAY=MO</c>). Completing moves the task to its next occurrence.</summary>
    public string? Recurrence { get; set; }

    /// <summary>Remind this many minutes before the due time (0 = at due). Empty = no reminder.</summary>
    public List<int> ReminderOffsets { get; set; } = [];

    /// <summary>After the due time, keep reminding every N minutes until done (null = don't).</summary>
    public int? RepeatReminderMinutes { get; set; }

    /// <summary>When the scheduler should next remind (computed; indexed). Null = nothing scheduled.</summary>
    public DateTimeOffset? NextReminderUtc { get; set; }

    /// <summary>When a reminder last fired.</summary>
    public DateTimeOffset? LastReminderUtc { get; set; }

    /// <summary>
    /// Actions to run with the task's reminder, one per line in the action syntax
    /// (<c>open https://meet.example.com/standup</c>). See <see cref="Automation.ActionScript"/>.
    /// </summary>
    public string? ReminderActions { get; set; }

    /// <summary>Full paths of files attached to the task (screenshots, captured files).</summary>
    public List<string> Attachments { get; set; } = [];

    public double SortOrder { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    /// <summary>Soft delete: set when moved to the trash; undo/restore clears it.</summary>
    public DateTimeOffset? DeletedUtc { get; set; }

    public bool IsOpen => Status == TaskItemStatus.Open;
    public bool IsRecurring => !string.IsNullOrEmpty(Recurrence);
    public bool IsDeleted => DeletedUtc is not null;

    /// <summary>Deep copy, used for undo snapshots.</summary>
    public TaskItem Clone()
    {
        var copy = (TaskItem)MemberwiseClone();
        copy.Tags = [.. Tags];
        copy.ReminderOffsets = [.. ReminderOffsets];
        copy.Attachments = [.. Attachments];
        return copy;
    }

    /// <summary>Copies every persisted value from <paramref name="other"/> (same id).</summary>
    public void CopyFrom(TaskItem other)
    {
        Title = other.Title;
        Notes = other.Notes;
        Status = other.Status;
        Priority = other.Priority;
        DueDate = other.DueDate;
        DueTime = other.DueTime;
        DueAtUtc = other.DueAtUtc;
        ListId = other.ListId;
        Tags = [.. other.Tags];
        Recurrence = other.Recurrence;
        ReminderOffsets = [.. other.ReminderOffsets];
        RepeatReminderMinutes = other.RepeatReminderMinutes;
        NextReminderUtc = other.NextReminderUtc;
        LastReminderUtc = other.LastReminderUtc;
        ReminderActions = other.ReminderActions;
        Attachments = [.. other.Attachments];
        SortOrder = other.SortOrder;
        CreatedUtc = other.CreatedUtc;
        UpdatedUtc = other.UpdatedUtc;
        CompletedUtc = other.CompletedUtc;
        DeletedUtc = other.DeletedUtc;
    }
}

public sealed class TaskList
{
    public const int MaxNameLength = 100;

    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    /// <summary>Icon key from <see cref="ListIcons"/>; null = folder.</summary>
    public string? Icon { get; set; }
    public double SortOrder { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>Normalises tag input: trims '#', lowercases, de-duplicates, keeps order.</summary>
public static class Tags
{
    public static List<string> Normalize(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        if (tags is null) return result;
        foreach (var raw in tags)
        {
            var t = raw.Trim().TrimStart('#').Trim().ToLowerInvariant();
            if (t.Length > 0 && t.Length <= 50 && !t.Any(char.IsWhiteSpace) && !result.Contains(t))
                result.Add(t);
        }
        return result;
    }

    /// <summary>Splits "home, work #urgent" style text.</summary>
    public static List<string> Parse(string? text) =>
        Normalize((text ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries));
}
