using TaskHub.Core.Domain;
using TaskHub.Core.Hosting;

namespace TaskHub.Core.Tests.Domain;

public class ClockTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo Kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    [Fact]
    public void Same_wall_clock_time_maps_to_different_utc_across_dst()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow, NewYork);
        // 9:00 in New York: EDT (UTC-4) before 1 Nov 2026, EST (UTC-5) after.
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.Zero), clock.ToUtc(new DateOnly(2026, 10, 30), new TimeOnly(9, 0)));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero), clock.ToUtc(new DateOnly(2026, 11, 2), new TimeOnly(9, 0)));
    }

    [Fact]
    public void Skipped_spring_forward_time_moves_forward()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow, NewYork);
        // 8 Mar 2026: 02:00 → 03:00. 02:30 doesn't exist; becomes 03:30 EDT = 07:30 UTC.
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), clock.ToUtc(new DateOnly(2026, 3, 8), new TimeOnly(2, 30)));
    }

    [Fact]
    public void Repeated_fall_back_time_takes_first_occurrence()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow, NewYork);
        // 1 Nov 2026: 01:30 happens twice; first is EDT (UTC-4) = 05:30 UTC.
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), clock.ToUtc(new DateOnly(2026, 11, 1), new TimeOnly(1, 30)));
    }

    [Fact]
    public void Today_is_evaluated_in_the_local_zone()
    {
        // 20:00 UTC on 2 Nov is already 3 Nov 01:30 in India.
        var clock = new TestClock(new DateTimeOffset(2026, 11, 2, 20, 0, 0, TimeSpan.Zero), Kolkata);
        Assert.Equal(new DateOnly(2026, 11, 3), clock.Today());
        Assert.Equal(new DateTime(2026, 11, 3, 1, 30, 0), clock.LocalNow());
    }
}

public class TagsTests
{
    [Fact]
    public void Normalize_trims_lowercases_dedupes_and_drops_invalid()
    {
        Assert.Equal(["home", "work", "q4-plan"], Tags.Normalize(["#Home", " work ", "home", "", "#", "two words", "Q4-Plan"]));
    }

    [Fact]
    public void Parse_accepts_spaces_commas_and_hashes()
    {
        Assert.Equal(["a", "b", "c"], Tags.Parse("#a, b;c  #A"));
        Assert.Empty(Tags.Parse(null));
    }
}

public class TaskOrderingTests
{
    [Fact]
    public void Open_dated_timed_and_high_priority_come_first()
    {
        var created = DateTimeOffset.UnixEpoch;
        TaskItem T(string title, DateOnly? due = null, TimeOnly? time = null, Priority p = Priority.None, bool done = false) => new()
        {
            Id = Guid.NewGuid(), Title = title, DueDate = due, DueTime = time, Priority = p,
            Status = done ? TaskItemStatus.Completed : TaskItemStatus.Open, CreatedUtc = created,
        };
        var d1 = new DateOnly(2026, 11, 2);
        var tasks = new[]
        {
            T("done", d1, done: true),
            T("undated-high", p: Priority.High),
            T("undated"),
            T("d2", d1.AddDays(1)),
            T("d1-allday-low", d1, p: Priority.Low),
            T("d1-allday-urgent", d1, p: Priority.Urgent),
            T("d1-0900", d1, new TimeOnly(9, 0)),
        };

        var order = TaskOrdering.Sort(tasks, TaskView.All).Select(t => t.Title);

        Assert.Equal(["d1-0900", "d1-allday-urgent", "d1-allday-low", "d2", "undated-high", "undated", "done"], order);
    }
}
