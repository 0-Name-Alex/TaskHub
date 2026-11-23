using TaskHub.Core.Domain;
using TaskHub.Core.Hosting;

namespace TaskHub.Core.Scheduling;

/// <summary>Moves a recurring task to its next occurrence when it's completed.</summary>
public sealed class RecurrenceEngine(IClock clock)
{
    /// <summary>
    /// Computes the next due date/time after the task's current due — and after "now", so a
    /// task completed late doesn't land in the past again. Returns false when the series ended
    /// (COUNT/UNTIL) or the task has no due date to recur from.
    /// </summary>
    public bool TryAdvance(TaskItem task, out DateOnly nextDate, out TimeOnly? nextTime)
    {
        nextDate = default;
        nextTime = task.DueTime;
        if (!task.IsRecurring || task.DueDate is not { } due) return false;

        var time = task.DueTime ?? TimeOnly.MinValue;
        var start = due.ToDateTime(time);
        var nowLocal = clock.LocalNow();
        // All-day tasks: anything on or before today is done; timed tasks: anything before now.
        var after = task.DueTime is null
            ? Max(start, DateOnly.FromDateTime(nowLocal).ToDateTime(TimeOnly.MinValue))
            : Max(start, nowLocal);

        var next = Recurrence.NextOccurrence(task.Recurrence!, start, after);
        if (next is null) return false;
        nextDate = DateOnly.FromDateTime(next.Value);
        nextTime = task.DueTime is null ? null : TimeOnly.FromDateTime(next.Value);
        return true;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
