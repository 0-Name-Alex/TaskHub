using TaskHub.Core.Configuration;
using TaskHub.Core.Domain;
using TaskHub.Core.Scheduling;

namespace TaskHub.Core.Tests.Scheduling;

public class ReminderPlannerTests
{
    private static readonly DateTimeOffset Due = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero); // UTC zone in these tests

    private static ReminderPlanner Planner(ReminderOptions? options = null) =>
        new(new TestClock(Due.AddHours(-5)), () => options ?? new ReminderOptions());

    private static TaskItem Timed(params int[] offsets) => new()
    {
        Title = "x",
        DueDate = DateOnly.FromDateTime(Due.UtcDateTime),
        DueTime = TimeOnly.FromDateTime(Due.UtcDateTime),
        DueAtUtc = Due,
        ReminderOffsets = [.. offsets],
    };

    [Fact]
    public void Earliest_future_offset_wins()
    {
        var t = Timed(0, 10, 60);
        var p = Planner();
        Assert.Equal(Due.AddMinutes(-60), p.NextAfter(t, Due.AddHours(-2)));
        Assert.Equal(Due.AddMinutes(-10), p.NextAfter(t, Due.AddMinutes(-60)));
        Assert.Equal(Due, p.NextAfter(t, Due.AddMinutes(-10)));
        Assert.Null(p.NextAfter(t, Due));
    }

    [Fact]
    public void Nag_repeats_after_due_until_done()
    {
        var t = Timed(0);
        t.RepeatReminderMinutes = 15;
        var p = Planner();
        Assert.Equal(Due, p.NextAfter(t, Due.AddMinutes(-1)));
        Assert.Equal(Due.AddMinutes(15), p.NextAfter(t, Due));
        Assert.Equal(Due.AddMinutes(45), p.NextAfter(t, Due.AddMinutes(40)));

        t.Status = TaskItemStatus.Completed;
        Assert.Null(p.NextAfter(t, Due));
    }

    [Fact]
    public void All_day_tasks_use_the_configured_time()
    {
        var t = new TaskItem { Title = "x", DueDate = new DateOnly(2026, 11, 3), ReminderOffsets = [0] };
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), Planner().NextAfter(t, Due));
        Assert.Null(Planner(new ReminderOptions { AllDayTime = "" }).NextAfter(t, Due));
    }

    [Fact]
    public void No_due_date_deleted_or_no_offsets_means_no_reminder()
    {
        var p = Planner();
        Assert.Null(p.NextAfter(new TaskItem { Title = "x", ReminderOffsets = [0] }, Due.AddDays(-1)));
        Assert.Null(p.NextAfter(Timed(), Due.AddDays(-1)));
        var deleted = Timed(0);
        deleted.DeletedUtc = Due;
        Assert.Null(p.NextAfter(deleted, Due.AddDays(-1)));
    }

    [Fact]
    public void Replan_never_schedules_in_the_past()
    {
        var t = Timed(60, 0); // 1h-before already passed at "now = due - 30 min"
        Planner().Replan(t, Due.AddMinutes(-30));
        Assert.Equal(Due, t.NextReminderUtc);
    }

    [Fact]
    public void Affects_reminders_detects_relevant_changes_only()
    {
        var a = Timed(0);
        var b = a.Clone();
        b.Title = "renamed";
        b.Tags = ["x"];
        Assert.False(ReminderPlanner.AffectsReminders(a, b));
        b.ReminderOffsets = [5];
        Assert.True(ReminderPlanner.AffectsReminders(a, b));
    }
}
