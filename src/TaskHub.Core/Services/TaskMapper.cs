using System.Globalization;
using TaskHub.Abstractions.Contracts;
using TaskHub.Core.Domain;

namespace TaskHub.Core.Services;

public static class TaskMapper
{
    public static TaskDto ToDto(this TaskItem t) => new(
        t.Id,
        t.Title,
        t.Notes,
        t.Status == TaskItemStatus.Completed ? TaskStatusNames.Completed : TaskStatusNames.Open,
        PriorityName(t.Priority),
        t.DueDate,
        t.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
        t.DueAtUtc,
        t.ListId,
        [.. t.Tags],
        t.CreatedUtc,
        t.UpdatedUtc,
        t.CompletedUtc,
        t.DeletedUtc,
        t.Recurrence,
        [.. t.ReminderOffsets],
        t.RepeatReminderMinutes,
        t.NextReminderUtc,
        t.ReminderActions,
        [.. t.Attachments])
    {
        RecurrenceText = t.Recurrence is null ? null : Scheduling.Recurrence.Describe(t.Recurrence),
    };

    public static TaskListDto ToDto(this TaskList l, int openCount = 0) => new(l.Id, l.Name, l.Color, openCount, l.Icon);

    public static ExportList ToExport(this TaskList l) => new(l.Id, l.Name, l.Color, l.SortOrder, l.CreatedUtc, l.Icon);

    public static string PriorityName(Priority p) => p switch
    {
        Priority.Low => PriorityNames.Low,
        Priority.Medium => PriorityNames.Medium,
        Priority.High => PriorityNames.High,
        Priority.Urgent => PriorityNames.Urgent,
        _ => PriorityNames.None,
    };

    public static Priority ParsePriority(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "none" or "0" => Priority.None,
        "low" or "1" => Priority.Low,
        "medium" or "med" or "2" => Priority.Medium,
        "high" or "3" => Priority.High,
        "urgent" or "4" => Priority.Urgent,
        _ => throw new TaskValidationException($"Unknown priority '{name}'. Use none, low, medium, high or urgent."),
    };

    public static TimeOnly? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t;
        throw new TaskValidationException($"Invalid time '{text}'. Use HH:mm (24-hour), e.g. 09:30.");
    }
}
