using TaskHub.Core.Domain;
using TaskHub.Core.Scheduling;

namespace TaskHub.Core.Tests.Scheduling;

public class RecurrenceTests
{
    [Theory]
    [InlineData("daily", "FREQ=DAILY")]
    [InlineData("every day", "FREQ=DAILY")]
    [InlineData("Every Weekday", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR")]
    [InlineData("weekdays", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR")]
    [InlineData("every weekend", "FREQ=WEEKLY;BYDAY=SA,SU")]
    [InlineData("weekly", "FREQ=WEEKLY")]
    [InlineData("every monday", "FREQ=WEEKLY;BYDAY=MO")]
    [InlineData("every fri,mon", "FREQ=WEEKLY;BYDAY=MO,FR")]
    [InlineData("every tuesday and thursday", "FREQ=WEEKLY;BYDAY=TU,TH")]
    [InlineData("every 2 weeks", "FREQ=WEEKLY;INTERVAL=2")]
    [InlineData("every other month", "FREQ=MONTHLY;INTERVAL=2")]
    [InlineData("every 3 days", "FREQ=DAILY;INTERVAL=3")]
    [InlineData("every 1 year", "FREQ=YEARLY")]
    [InlineData("monthly", "FREQ=MONTHLY")]
    [InlineData("annually", "FREQ=YEARLY")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR", "FREQ=MONTHLY;BYDAY=-1FR")]
    [InlineData("RRULE:freq=weekly;byday=mo,we", "FREQ=WEEKLY;BYDAY=MO,WE")]
    [InlineData("FREQ=DAILY;COUNT=5", "FREQ=DAILY;COUNT=5")]
    public void Parses_friendly_text_and_rrules(string text, string expected)
    {
        Assert.True(Recurrence.TryParse(text, out var rule, out var error), error);
        Assert.Equal(expected, rule);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sometimes")]
    [InlineData("every blue moon")]
    [InlineData("every week on mon, thu")]     // a description, not input — must not silently become "weekly"
    [InlineData("FREQ=HOURLY")]               // too frequent for tasks
    [InlineData("FREQ=WEEKLY;BYDAY=XX!")]
    public void Rejects_what_it_does_not_understand(string text)
    {
        Assert.False(Recurrence.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Next_weekly_occurrence()
    {
        // Mon 2 Nov 2026 09:00, every Monday
        var start = new DateTime(2026, 11, 2, 9, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 9, 9, 0, 0), Recurrence.NextOccurrence("FREQ=WEEKLY;BYDAY=MO", start, start));
    }

    [Fact]
    public void Next_skips_to_after_the_given_moment()
    {
        var start = new DateTime(2026, 11, 2, 9, 0, 0);
        var after = new DateTime(2026, 11, 20, 12, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 21, 9, 0, 0), Recurrence.NextOccurrence("FREQ=DAILY", start, after));
    }

    [Fact]
    public void Weekdays_skip_the_weekend()
    {
        var fri = new DateTime(2026, 11, 6, 8, 30, 0);
        Assert.Equal(new DateTime(2026, 11, 9, 8, 30, 0), Recurrence.NextOccurrence("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", fri, fri));
    }

    [Fact]
    public void Last_friday_of_the_month()
    {
        var start = new DateTime(2026, 11, 27, 17, 0, 0); // last Friday of Nov 2026
        Assert.Equal(new DateTime(2026, 12, 25, 17, 0, 0), Recurrence.NextOccurrence("FREQ=MONTHLY;BYDAY=-1FR", start, start));
    }

    [Fact]
    public void Wall_clock_time_is_kept_across_dst()
    {
        // Floating local times: 09:00 stays 09:00 even though New York changes offset on 1 Nov.
        var start = new DateTime(2026, 10, 26, 9, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 2, 9, 0, 0), Recurrence.NextOccurrence("FREQ=WEEKLY", start, start));
    }

    [Fact]
    public void Finite_series_ends()
    {
        var start = new DateTime(2026, 11, 2, 9, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 3, 9, 0, 0), Recurrence.NextOccurrence("FREQ=DAILY;COUNT=2", start, start));
        Assert.Null(Recurrence.NextOccurrence("FREQ=DAILY;COUNT=2", start, start.AddDays(1)));
    }

    [Fact]
    public void Sparse_rules_resolve()
    {
        var start = new DateTime(2026, 2, 28, 0, 0, 0);
        Assert.Equal(new DateTime(2036, 2, 28, 0, 0, 0), Recurrence.NextOccurrence("FREQ=YEARLY;INTERVAL=10", start, start));
    }

    [Theory]
    [InlineData("FREQ=DAILY", "Every day")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2", "Every 2 weeks")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", "Every Monday")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", "Every weekday")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,TH", "Every week on Mon, Thu")]
    [InlineData("FREQ=MONTHLY;COUNT=3", "Every month, 3 times")]
    [InlineData(null, "Does not repeat")]
    public void Describes_rules(string? rule, string expected) => Assert.Equal(expected, Recurrence.Describe(rule));

    [Theory]
    [InlineData("Every day")]
    [InlineData("Every 2 weeks")]
    [InlineData("Every Monday")]
    [InlineData("Every weekday")]
    public void Simple_descriptions_parse_back(string description) =>
        Assert.True(Recurrence.TryParse(description, out _, out _));
}

public class RecurrenceEngineTests
{
    private static readonly TimeZoneInfo Kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private static TaskItem Task(string rule, DateOnly due, TimeOnly? time = null) => new()
    {
        Title = "x",
        Recurrence = rule,
        DueDate = due,
        DueTime = time,
    };

    [Fact]
    public void Advances_to_the_next_occurrence()
    {
        // Now: Mon 2 Nov 2026 10:30 IST
        var engine = new RecurrenceEngine(new TestClock(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), Kolkata));
        Assert.True(engine.TryAdvance(Task("FREQ=WEEKLY;BYDAY=MO", new DateOnly(2026, 11, 2), new TimeOnly(9, 0)), out var date, out var time));
        Assert.Equal(new DateOnly(2026, 11, 9), date);
        Assert.Equal(new TimeOnly(9, 0), time);
    }

    [Fact]
    public void Completing_late_does_not_land_in_the_past()
    {
        // A daily task due 5 days ago, completed today → tomorrow (not 4 days ago).
        var engine = new RecurrenceEngine(new TestClock(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), Kolkata));
        Assert.True(engine.TryAdvance(Task("FREQ=DAILY", new DateOnly(2026, 10, 28)), out var date, out var time));
        Assert.Equal(new DateOnly(2026, 11, 3), date);
        Assert.Null(time);
    }

    [Fact]
    public void Timed_daily_task_completed_before_its_time_moves_to_tomorrow()
    {
        // Due today 18:00, completed at 10:30 → next is tomorrow 18:00.
        var engine = new RecurrenceEngine(new TestClock(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), Kolkata));
        Assert.True(engine.TryAdvance(Task("FREQ=DAILY", new DateOnly(2026, 11, 2), new TimeOnly(18, 0)), out var date, out _));
        Assert.Equal(new DateOnly(2026, 11, 3), date);
    }

    [Fact]
    public void Non_recurring_or_undated_tasks_do_not_advance()
    {
        var engine = new RecurrenceEngine(new TestClock(DateTimeOffset.UtcNow, Kolkata));
        Assert.False(engine.TryAdvance(new TaskItem { Title = "x", DueDate = new DateOnly(2026, 11, 2) }, out _, out _));
        Assert.False(engine.TryAdvance(new TaskItem { Title = "x", Recurrence = "FREQ=DAILY" }, out _, out _));
    }
}
