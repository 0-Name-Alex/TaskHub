using System.ComponentModel;
using System.Globalization;
using TaskHub.Core.Domain;

namespace TaskHub.App.ViewModels;

/// <summary>
/// One line in the task list (a snapshot; the list reloads after every change). Only the relative
/// reminder text changes over time — call <see cref="RefreshTime"/> to update it.
/// </summary>
public sealed class TaskRowViewModel : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs ReminderShortChanged = new(nameof(ReminderShort));
    private static readonly PropertyChangedEventArgs ReminderSoonChanged = new(nameof(ReminderSoon));
    private static readonly PropertyChangedEventArgs IsCompletingChanged = new(nameof(IsCompleting));
    private bool _isCompleting;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TaskRowViewModel(TaskItem task, string? listName, DateOnly today, DateTimeOffset now, bool showList,
        string? listColor = null, bool grouped = false)
    {
        Task = task;
        ListName = showList ? listName : null;
        ListColor = showList ? listColor : null;
        IsOverdue = task.IsOpen && task.DueDate is { } d && (d < today || (task.DueAtUtc is { } at && at < now));
        IsDueToday = task.IsOpen && task.DueDate == today && !IsOverdue;
        var group = GroupFor(task, today, IsOverdue);
        GroupRank = group.Rank;
        Group = group.Name;
        // Under a "Today" heading the date is implied: show just the time (or nothing for all-day tasks).
        DueText = task.DueDate is not { } due ? null
            : grouped && IsDueToday ? task.DueTime?.ToString("t", CultureInfo.CurrentCulture)
            : FormatDue(due, task.DueTime, today);
        TagsText = task.Tags.Count == 0 ? null : string.Join("  ", task.Tags.Select(t => "#" + t));
        PriorityMarker = task.Priority switch
        {
            Priority.Urgent => "!!!",
            Priority.High => "!!",
            Priority.Medium => "!",
            _ => null,
        };
    }

    /// <summary>Section heading in grouped views: Overdue, Today, Tomorrow, Next 7 days, Later, No date, Completed.</summary>
    public string Group { get; }
    public int GroupRank { get; }

    /// <summary>True for a moment after ticking the box, while the row fades out.</summary>
    public bool IsCompleting
    {
        get => _isCompleting;
        set
        {
            if (_isCompleting == value) return;
            _isCompleting = value;
            PropertyChanged?.Invoke(this, IsCompletingChanged);
        }
    }

    private static (int Rank, string Name) GroupFor(TaskItem task, DateOnly today, bool overdue)
    {
        if (task.Status == TaskItemStatus.Completed) return (6, "Completed");
        if (overdue) return (0, "Overdue");
        if (task.DueDate is not { } due) return (5, "No date");
        return (due.DayNumber - today.DayNumber) switch
        {
            <= 0 => (1, "Today"),
            1 => (2, "Tomorrow"),
            < 7 => (3, "Next 7 days"),
            _ => (4, "Later"),
        };
    }

    public TaskItem Task { get; }
    public Guid Id => Task.Id;
    public string Title => Task.Title;
    public bool IsCompleted => Task.Status == TaskItemStatus.Completed;
    public bool IsDeleted => Task.IsDeleted;
    public Priority Priority => Task.Priority;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Task.Notes);
    public bool IsRecurring => Task.IsRecurring;
    public string? RecurrenceText => Task.IsRecurring ? Core.Scheduling.Recurrence.Describe(Task.Recurrence) : null;
    public bool HasReminder => Task.NextReminderUtc is not null;

    /// <summary>Compact next-reminder time for the row: "in 12 min", "13:05", "Tomorrow 09:30", "Tue 09:30".</summary>
    public string? ReminderShort => Task.NextReminderUtc is { } r ? FormatReminder(r, DateTimeOffset.Now) : null;

    /// <summary>True when the next reminder is less than an hour away (highlighted).</summary>
    public bool ReminderSoon => Task.NextReminderUtc is { } r && r - DateTimeOffset.Now < TimeSpan.FromHours(1);

    public void RefreshTime()
    {
        if (Task.NextReminderUtc is null) return;
        PropertyChanged?.Invoke(this, ReminderShortChanged);
        PropertyChanged?.Invoke(this, ReminderSoonChanged);
    }

    public static string FormatReminder(DateTimeOffset reminderUtc, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        var until = reminderUtc - now;
        if (until <= TimeSpan.FromSeconds(30)) return "now";
        if (until < TimeSpan.FromHours(1)) return $"in {(int)Math.Ceiling(until.TotalMinutes)} min";

        var local = reminderUtc.ToLocalTime();
        var days = DateOnly.FromDateTime(local.DateTime).DayNumber - DateOnly.FromDateTime(now.ToLocalTime().DateTime).DayNumber;
        var time = local.ToString("t", culture);
        return days switch
        {
            0 => time,
            1 => $"Tomorrow {time}",
            > 1 and < 7 => $"{local.ToString("ddd", culture)} {time}",
            _ => $"{local.ToString("d MMM", culture)} {time}",
        };
    }
    public string? ReminderText => Task.NextReminderUtc is { } r ? "Reminder " + r.ToLocalTime().ToString("ddd d MMM, t", CultureInfo.CurrentCulture) : null;
    public string? DueText { get; }
    public bool IsOverdue { get; }
    public bool IsDueToday { get; }
    public string? TagsText { get; }
    public string? ListName { get; }
    /// <summary>The list's colour ("#RRGGBB") when the list name is shown.</summary>
    public string? ListColor { get; }
    public string? PriorityMarker { get; }
    public bool HasMeta => DueText is not null || TagsText is not null || ListName is not null || HasNotes || IsRecurring;

    public static string FormatDue(DateOnly date, TimeOnly? time, DateOnly today)
    {
        var culture = CultureInfo.CurrentCulture;
        var day = (date.DayNumber - today.DayNumber) switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            > 1 and < 7 => date.ToString("dddd", culture),
            _ => date.ToString(date.Year == today.Year ? "ddd d MMM" : "d MMM yyyy", culture),
        };
        return time is { } t ? $"{day} {t.ToString("t", culture)}" : day;
    }
}

public enum NavKind
{
    View,
    List,
    Tag,
}

/// <summary>A sidebar entry: a built-in view, a list, or a tag.</summary>
public sealed class NavItem(NavKind kind, string section, string title, string glyph)
{
    public NavKind Kind { get; } = kind;
    public string Section { get; } = section;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public TaskView View { get; init; } = TaskView.All;
    public Guid? ListId { get; init; }
    public string? Tag { get; init; }
    /// <summary>List colour ("#RRGGBB"), or null.</summary>
    public string? Color { get; init; }
    public bool HasColor => Color is not null;
    /// <summary>List icon key (see <see cref="ListIcons"/>), or null for the folder.</summary>
    public string? Icon { get; init; }
    public int? Count { get; init; }

    /// <summary>Choices for the list's icon menu (lists only).</summary>
    public IReadOnlyList<ListIconAction> IconChoices =>
        Kind == NavKind.List && ListId is { } id
            ? ListIcons.All.Select(i => new ListIconAction(id, i.Key, i.Name, i.Glyph,
                string.Equals(i.Key, Icon ?? ListIcons.Default, StringComparison.OrdinalIgnoreCase))).ToList()
            : [];

    /// <summary>Choices for the list's colour menu (lists only).</summary>
    public IReadOnlyList<ListColorAction> ColorChoices =>
        Kind == NavKind.List && ListId is { } id
            ? ListColorChoice.All.Select(c => new ListColorAction(id, c.Name, c.Hex, string.Equals(c.Hex, Color, StringComparison.OrdinalIgnoreCase))).ToList()
            : [];
    public string? CountText => Count is > 0 ? Count.Value.ToString(CultureInfo.CurrentCulture) : null;

    public string Key => Kind switch
    {
        NavKind.List => "list:" + ListId,
        NavKind.Tag => "tag:" + Tag,
        _ => "view:" + View,
    };
}

/// <summary>Choice in the editor's list drop-down. <see cref="Guid.Empty"/> = Inbox.</summary>
public sealed record ListOption(Guid Id, string Name, string? Color = null);

/// <summary>A colour in the list palette. Null hex = no colour.</summary>
public sealed record ListColorChoice(string Name, string? Hex)
{
    public static readonly IReadOnlyList<ListColorChoice> All =
    [
        new("No colour", null), new("Red", "#E5484D"), new("Orange", "#F76B15"), new("Amber", "#FFB224"),
        new("Green", "#30A46C"), new("Teal", "#12A594"), new("Blue", "#3E8EF7"), new("Indigo", "#6E6ADE"),
        new("Purple", "#9B5DE5"), new("Pink", "#E54D9B"), new("Brown", "#AD7F58"), new("Grey", "#8B8D98"),
    ];
}

/// <summary>One entry in a list's icon menu.</summary>
public sealed record ListIconAction(Guid ListId, string Key, string Name, string Glyph, bool IsCurrent);

/// <summary>One entry in a list's colour menu.</summary>
public sealed record ListColorAction(Guid ListId, string Name, string? Hex, bool IsCurrent);

/// <summary>A parsed piece of the quick-add text, shown as a chip under the box.</summary>
public sealed record PreviewChip(string Icon, string Text, string? Color = null)
{
    public bool HasColor => Color is not null;
}

/// <summary>A labelled choice in an editor drop-down. Null value = off.</summary>
public sealed record MinutesChoice(string Label, int? Minutes)
{
    public static readonly IReadOnlyList<MinutesChoice> ReminderChoices =
    [
        new("No reminder", null), new("At due time", 0), new("5 minutes before", 5), new("10 minutes before", 10),
        new("15 minutes before", 15), new("30 minutes before", 30), new("1 hour before", 60), new("2 hours before", 120),
        new("1 day before", 1440),
    ];

    public static readonly IReadOnlyList<MinutesChoice> RepeatChoices =
    [
        new("Don't repeat", null), new("Every 5 minutes", 5), new("Every 10 minutes", 10), new("Every 15 minutes", 15),
        new("Every 30 minutes", 30), new("Every hour", 60),
    ];
}
