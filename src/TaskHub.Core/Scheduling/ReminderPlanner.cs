using TaskHub.Core.Configuration;
using TaskHub.Core.Domain;
using TaskHub.Core.Hosting;

namespace TaskHub.Core.Scheduling;

/// <summary>
/// Pure reminder arithmetic. A task reminds at <c>anchor − offset</c> for each offset, where the
/// anchor is its due time (or the configured all-day time for date-only tasks), and — if it has a
/// repeat interval — every N minutes after the anchor until it's done.
/// </summary>
public sealed class ReminderPlanner(IClock clock, Func<ReminderOptions> options)
{
    public ReminderOptions Options => options();

    /// <summary>The instant reminders are measured from, or null if the task has no due date.</summary>
    public DateTimeOffset? AnchorUtc(TaskItem task)
    {
        if (task.DueAtUtc is { } at) return at;
        if (task.DueDate is { } date && options().GetAllDayTime() is { } time) return clock.ToUtc(date, time);
        return null;
    }

    /// <summary>The first reminder strictly after <paramref name="after"/>, or null.</summary>
    public DateTimeOffset? NextAfter(TaskItem task, DateTimeOffset after)
    {
        if (!task.IsOpen || task.IsDeleted) return null;
        if (AnchorUtc(task) is not { } anchor) return null;

        DateTimeOffset? best = null;
        void Consider(DateTimeOffset t)
        {
            if (t > after && (best is null || t < best)) best = t;
        }

        foreach (var offset in task.ReminderOffsets)
            Consider(anchor.AddMinutes(-offset));

        if (task.RepeatReminderMinutes is > 0 and var every)
        {
            var step = TimeSpan.FromMinutes(every);
            if (anchor > after)
            {
                Consider(anchor);
            }
            else
            {
                var periods = (long)Math.Floor((after - anchor) / step) + 1;
                Consider(anchor + step * periods);
            }
        }
        return best;
    }

    /// <summary>
    /// Re-plans after the task was created or changed: reminders already in the past are not fired
    /// retroactively — only ones still ahead are scheduled.
    /// </summary>
    public void Replan(TaskItem task, DateTimeOffset now) => task.NextReminderUtc = NextAfter(task, now);

    /// <summary>True if a change touched anything reminders depend on.</summary>
    public static bool AffectsReminders(TaskItem before, TaskItem after) =>
        before.DueDate != after.DueDate
        || before.DueTime != after.DueTime
        || before.DueAtUtc != after.DueAtUtc
        || before.Status != after.Status
        || before.DeletedUtc != after.DeletedUtc
        || before.RepeatReminderMinutes != after.RepeatReminderMinutes
        || !before.ReminderOffsets.SequenceEqual(after.ReminderOffsets);
}
