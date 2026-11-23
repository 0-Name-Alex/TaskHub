using TaskHub.Core.Domain;
using TaskHub.Core.Parsing;

namespace TaskHub.Core.Tests.Parsing;

public class QuickAddParserTests
{
    // Monday 2 November 2026, 10:30 local
    private static readonly DateTime Now = new(2026, 11, 2, 10, 30, 0);
    private static readonly DateOnly Today = new(2026, 11, 2);

    private static QuickAddResult P(string text) => QuickAddParser.Parse(text, Now);

    [Fact]
    public void Full_example()
    {
        var r = P("Pay rent tomorrow 9am #home !high @Finance");
        Assert.Equal("Pay rent", r.Title);
        Assert.Equal(Today.AddDays(1), r.DueDate);
        Assert.Equal(new TimeOnly(9, 0), r.DueTime);
        Assert.Equal(Priority.High, r.Priority);
        Assert.Equal(["home"], r.Tags);
        Assert.Equal("Finance", r.ListName);
    }

    [Fact]
    public void Plain_text_is_just_a_title()
    {
        var r = P("Buy 2 apples and 3 pears");
        Assert.Equal("Buy 2 apples and 3 pears", r.Title);
        Assert.Null(r.DueDate);
        Assert.Null(r.DueTime);
        Assert.Null(r.Priority);
        Assert.Empty(r.Tags);
        Assert.Null(r.ListName);
    }

    [Theory]
    [InlineData("call mom today", 0)]
    [InlineData("call mom tod", 0)]
    [InlineData("call mom tomorrow", 1)]
    [InlineData("call mom tmr", 1)]
    [InlineData("call mom monday", 0)]      // today is Monday
    [InlineData("call mom next monday", 7)]
    [InlineData("call mom on wednesday", 2)]
    [InlineData("call mom fri", 4)]
    [InlineData("call mom next fri", 4)]
    [InlineData("call mom sunday", 6)]
    [InlineData("call mom weekend", 5)]
    [InlineData("call mom this weekend", 5)]
    [InlineData("call mom next week", 7)]
    [InlineData("call mom in 3 days", 3)]
    [InlineData("call mom in a week", 7)]
    [InlineData("call mom in 2 weeks", 14)]
    [InlineData("call mom due tomorrow", 1)]
    public void Relative_dates(string text, int daysFromToday)
    {
        var r = P(text);
        Assert.Equal("call mom", r.Title);
        Assert.Equal(Today.AddDays(daysFromToday), r.DueDate);
        Assert.Null(r.DueTime);
    }

    [Theory]
    [InlineData("dentist 2026-11-20", 2026, 11, 20)]
    [InlineData("dentist 20 nov", 2026, 11, 20)]
    [InlineData("dentist 20th november", 2026, 11, 20)]
    [InlineData("dentist nov 20", 2026, 11, 20)]
    [InlineData("dentist on Nov 20th", 2026, 11, 20)]
    [InlineData("dentist 5 jan", 2027, 1, 5)]          // already past this year → next year
    [InlineData("dentist 1 nov", 2027, 11, 1)]         // yesterday → next year
    [InlineData("dentist 2 nov", 2026, 11, 2)]         // today stays today
    [InlineData("dentist 3 mar 2028", 2028, 3, 3)]
    [InlineData("dentist feb 29 2028", 2028, 2, 29)]
    public void Absolute_dates(string text, int y, int m, int d)
    {
        var r = P(text);
        Assert.Equal("dentist", r.Title);
        Assert.Equal(new DateOnly(y, m, d), r.DueDate);
    }

    [Theory]
    [InlineData("standup tomorrow 9am", 9, 0)]
    [InlineData("standup tomorrow 9 am", 9, 0)]
    [InlineData("standup tomorrow 9:15am", 9, 15)]
    [InlineData("standup tomorrow 9:15 PM", 21, 15)]
    [InlineData("standup tomorrow 12pm", 12, 0)]
    [InlineData("standup tomorrow 12am", 0, 0)]
    [InlineData("standup tomorrow 21:00", 21, 0)]
    [InlineData("standup tomorrow at 5", 17, 0)]
    [InlineData("standup tomorrow at 9", 9, 0)]
    [InlineData("standup tomorrow at 14", 14, 0)]
    [InlineData("standup tomorrow noon", 12, 0)]
    [InlineData("standup tomorrow morning", 9, 0)]
    [InlineData("standup tomorrow evening", 18, 0)]
    [InlineData("standup at 9am tomorrow", 9, 0)]
    public void Times(string text, int h, int min)
    {
        var r = P(text);
        Assert.Equal("standup", r.Title);
        Assert.Equal(Today.AddDays(1), r.DueDate);
        Assert.Equal(new TimeOnly(h, min), r.DueTime);
    }

    [Fact]
    public void Time_without_date_means_next_occurrence()
    {
        Assert.Equal(Today, P("call 3pm").DueDate);              // later today
        Assert.Equal(Today.AddDays(1), P("call 9am").DueDate);   // 9am already passed
    }

    [Fact]
    public void Tonight_sets_evening_unless_a_time_is_given()
    {
        Assert.Equal(new TimeOnly(20, 0), P("movie tonight").DueTime);
        Assert.Equal(new TimeOnly(21, 30), P("movie tonight 9:30pm").DueTime);
        Assert.Equal(Today, P("movie tonight").DueDate);
    }

    [Fact]
    public void In_hours_and_minutes_sets_date_and_time()
    {
        var r = P("check oven in 2 hours");
        Assert.Equal(Today, r.DueDate);
        Assert.Equal(new TimeOnly(12, 30), r.DueTime);

        var late = QuickAddParser.Parse("sleep in 90 minutes", new DateTime(2026, 11, 2, 23, 0, 0));
        Assert.Equal(Today.AddDays(1), late.DueDate);
        Assert.Equal(new TimeOnly(0, 30), late.DueTime);
    }

    [Theory]
    [InlineData("x !low", Priority.Low)]
    [InlineData("x !1", Priority.Low)]
    [InlineData("x !med", Priority.Medium)]
    [InlineData("x !medium", Priority.Medium)]
    [InlineData("x !high", Priority.High)]
    [InlineData("x !3", Priority.High)]
    [InlineData("x !!", Priority.High)]
    [InlineData("x !urgent", Priority.Urgent)]
    [InlineData("x !!!", Priority.Urgent)]
    [InlineData("x !4", Priority.Urgent)]
    [InlineData("X !HIGH", Priority.High)]
    public void Priorities(string text, Priority expected)
    {
        var r = P(text);
        Assert.Equal(expected, r.Priority);
        Assert.Equal("x", r.Title, ignoreCase: true);
    }

    [Fact]
    public void Tags_are_normalised_and_deduplicated()
    {
        var r = P("fix sink #Home #errands, #home");
        Assert.Equal("fix sink", r.Title);
        Assert.Equal(["home", "errands"], r.Tags);
    }

    [Fact]
    public void List_with_underscores_becomes_spaces()
    {
        Assert.Equal("Side Project", P("write docs @Side_Project").ListName);
    }

    [Fact]
    public void Quoted_text_is_kept_literally()
    {
        var r = P("\"Monday\" meeting notes tomorrow");
        Assert.Equal("Monday meeting notes", r.Title);
        Assert.Equal(Today.AddDays(1), r.DueDate);
    }

    [Theory]
    [InlineData("work on report", "work on report")]
    [InlineData("meet at home", "meet at home")]
    [InlineData("email bob@example.com", "email bob@example.com")]
    [InlineData("learn C# basics", "learn C# basics")]
    [InlineData("wow! that's great", "wow! that's great")]
    [InlineData("in the morning ritual", "in the ritual")]   // "morning" is a time word
    public void Ordinary_words_are_not_eaten(string text, string title) =>
        Assert.Equal(title, P(text).Title);

    [Fact]
    public void Text_made_only_of_tokens_keeps_original_as_title()
    {
        var r = P("tomorrow");
        Assert.Equal("tomorrow", r.Title);
        Assert.Equal(Today.AddDays(1), r.DueDate);
    }

    [Fact]
    public void Trailing_punctuation_does_not_block_matching()
    {
        var r = P("submit form tomorrow, 5pm.");
        Assert.Equal(Today.AddDays(1), r.DueDate);
        Assert.Equal(new TimeOnly(17, 0), r.DueTime);
        Assert.Equal("submit form", r.Title);
    }

    [Theory]
    [InlineData("dentist 31 feb")]
    [InlineData("dentist 2026-13-01")]
    [InlineData("call 13pm")]
    public void Invalid_dates_and_times_stay_in_title(string text)
    {
        var r = P(text);
        Assert.Null(r.DueTime);
        Assert.Equal(text, r.Title);
    }

    [Theory]
    [InlineData("standup every weekday 9:30am", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", 2026, 11, 3)]   // 9:30 passed today → Tue
    [InlineData("water plants every 3 days", "FREQ=DAILY;INTERVAL=3", 2026, 11, 2)]
    [InlineData("team sync every thursday 3pm", "FREQ=WEEKLY;BYDAY=TH", 2026, 11, 5)]
    [InlineData("gym every mon,wed,fri", "FREQ=WEEKLY;BYDAY=MO,WE,FR", 2026, 11, 2)]
    [InlineData("rent monthly", "FREQ=MONTHLY", 2026, 11, 2)]
    [InlineData("review every other week friday", "FREQ=WEEKLY;INTERVAL=2", 2026, 11, 6)]
    public void Repeats(string text, string rule, int y, int m, int d)
    {
        var r = P(text);
        Assert.Equal(rule, r.Recurrence);
        Assert.Equal(new DateOnly(y, m, d), r.DueDate);
        Assert.DoesNotContain("every", r.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repeat_with_explicit_start_date_keeps_it()
    {
        var r = P("pay rent every month 1 dec");
        Assert.Equal("FREQ=MONTHLY", r.Recurrence);
        Assert.Equal(new DateOnly(2026, 12, 1), r.DueDate);
        Assert.Equal("pay rent", r.Title);
    }

    [Fact]
    public void Every_followed_by_nonsense_stays_in_title()
    {
        var r = P("every little thing");
        Assert.Null(r.Recurrence);
        Assert.Equal("every little thing", r.Title);
    }
}
