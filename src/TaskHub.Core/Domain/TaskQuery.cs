namespace TaskHub.Core.Domain;

public enum TaskView
{
    All,
    Inbox,
    Today,
    Upcoming,
    Overdue,
    Completed,
    Trash,
}

/// <summary>A filter over tasks. All set criteria must match.</summary>
public sealed record TaskQuery
{
    public TaskView View { get; init; } = TaskView.All;
    public Guid? ListId { get; init; }
    public string? Tag { get; init; }
    /// <summary>Case-insensitive substring of title or notes.</summary>
    public string? Text { get; init; }
    /// <summary>For views that default to open tasks only (All, list/tag filters).</summary>
    public bool IncludeCompleted { get; init; }
    public int Limit { get; init; } = 1000;

    public static TaskView ParseView(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => TaskView.All,
        "inbox" => TaskView.Inbox,
        "today" => TaskView.Today,
        "upcoming" => TaskView.Upcoming,
        "overdue" => TaskView.Overdue,
        "completed" or "done" => TaskView.Completed,
        "trash" or "deleted" => TaskView.Trash,
        _ => throw new ArgumentException($"Unknown view '{name}'. Use inbox, today, upcoming, overdue, all, completed or trash."),
    };
}

/// <summary>Ordering used everywhere tasks are listed.</summary>
public static class TaskOrdering
{
    /// <summary>Open first; then by due (dated before undated, earlier first, timed before all-day on the same date); then priority (high first); then newest.</summary>
    public static IEnumerable<TaskItem> Sort(IEnumerable<TaskItem> tasks, TaskView view) => view switch
    {
        TaskView.Completed => tasks.OrderByDescending(t => t.CompletedUtc),
        TaskView.Trash => tasks.OrderByDescending(t => t.DeletedUtc),
        _ => tasks
            .OrderBy(t => t.Status)
            .ThenBy(t => t.DueDate is null)
            .ThenBy(t => t.DueDate)
            .ThenBy(t => t.DueTime is null)
            .ThenBy(t => t.DueTime)
            .ThenByDescending(t => t.Priority)
            .ThenBy(t => t.SortOrder)
            .ThenByDescending(t => t.CreatedUtc),
    };
}
