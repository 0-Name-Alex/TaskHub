using System.Globalization;
using System.Text.RegularExpressions;
using TaskHub.Core.Domain;

namespace TaskHub.Core.Parsing;

/// <summary>What a quick-add line parsed to. Null fields were not mentioned.</summary>
public sealed record QuickAddResult(
    string Title,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    Priority? Priority,
    IReadOnlyList<string> Tags,
    string? ListName,
    string? Recurrence = null);

/// <summary>
/// Parses natural quick-add text, e.g. <c>Pay rent tomorrow 9am #home !high @Finance</c>.
/// <list type="bullet">
/// <item><c>#tag</c> adds a tag, <c>@List</c> puts it in a list (<c>@Side_Project</c> → "Side Project").</item>
/// <item><c>!low !med !high !urgent</c>, <c>!1</c>–<c>!4</c>, <c>!!</c> (high), <c>!!!</c> (urgent).</item>
/// <item>Dates: today, tonight, tomorrow/tmr, monday/mon, next friday, weekend, next week,
/// in 3 days / 2 weeks, 5 nov, nov 5, 2026-11-05 (optionally prefixed with on/due/by).</item>
/// <item>Times: 9am, 9:30pm, 21:00, at 5, noon, morning, afternoon, evening; "in 2 hours".</item>
/// <item>Repeats: daily, weekdays, weekly, monthly, yearly, every day, every monday, every mon,thu,
/// every 2 weeks, every other month. A repeating task without a date starts at its first occurrence.</item>
/// <item>Text in "double quotes" is kept literally (e.g. <c>"Monday" meeting notes</c>).</item>
/// </list>
/// A time without a date means the next occurrence (today, or tomorrow if already past).
/// </summary>
public static partial class QuickAddParser
{
    private static readonly string[] PrefixWords = ["on", "due", "by", "at", "this"];

    private static readonly Dictionary<string, DayOfWeek> Weekdays = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monday"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday,
    };

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["january"] = 1, ["feb"] = 2, ["february"] = 2, ["mar"] = 3, ["march"] = 3,
        ["apr"] = 4, ["april"] = 4, ["may"] = 5, ["jun"] = 6, ["june"] = 6, ["jul"] = 7, ["july"] = 7,
        ["aug"] = 8, ["august"] = 8, ["sep"] = 9, ["sept"] = 9, ["september"] = 9, ["oct"] = 10, ["october"] = 10,
        ["nov"] = 11, ["november"] = 11, ["dec"] = 12, ["december"] = 12,
    };

    private static readonly Dictionary<string, TimeOnly> NamedTimes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["noon"] = new(12, 0), ["midday"] = new(12, 0),
        ["morning"] = new(9, 0), ["afternoon"] = new(14, 0), ["evening"] = new(18, 0),
    };

    [GeneratedRegex(@"^(\d{1,2})(?::(\d{2}))?\s*(am|pm|a|p)$", RegexOptions.IgnoreCase)]
    private static partial Regex AmPmTime();

    [GeneratedRegex(@"^([01]?\d|2[0-3]):([0-5]\d)$")]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"^(\d{4})-(\d{1,2})-(\d{1,2})$")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^(\d{1,2})(st|nd|rd|th)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DayNumber();

    private sealed record Token(string Text, bool Literal)
    {
        /// <summary>Lowercase, trailing punctuation removed — used for matching only.</summary>
        public string Key { get; } = Literal ? "" : Text.TrimEnd(',', '.', ';').ToLowerInvariant();
    }

    private sealed class State
    {
        public DateOnly? Date;
        public TimeOnly? Time;
        public Priority? Priority;
        public string? List;
        public string? Recurrence;
        public readonly List<string> Tags = [];
    }

    public static QuickAddResult Parse(string input, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tokens = Tokenize(input);
        var used = new bool[tokens.Count];
        var s = new State();
        var today = DateOnly.FromDateTime(localNow);

        for (var i = 0; i < tokens.Count; i++)
        {
            if (used[i] || tokens[i].Literal) continue;

            if (TrySymbol(tokens[i], s))
            {
                used[i] = true;
                continue;
            }

            // Optional prefix word ("on friday", "at 5pm", "due tomorrow") is consumed only with a match.
            var start = i;
            var hasPrefix = PrefixWords.Contains(tokens[i].Key) && i + 1 < tokens.Count;
            var matchAt = hasPrefix ? i + 1 : i;
            var allowBareHour = hasPrefix && tokens[i].Key == "at";

            var n = TryDateOrTime(tokens, matchAt, used, s, today, localNow, allowBareHour);
            if (n == 0 && hasPrefix)
            {
                // the prefix word wasn't followed by a date/time: try the word itself
                n = TryDateOrTime(tokens, i, used, s, today, localNow, false);
                matchAt = i;
                start = i;
            }
            if (n > 0)
            {
                for (var k = start; k < matchAt + n; k++) used[k] = true;
                i = matchAt + n - 1;
            }
        }

        // A repeating task with no date starts at its first occurrence (later today if the time hasn't passed).
        if (s.Recurrence is { } rule && s.Date is null)
        {
            s.Date = Scheduling.Recurrence.FirstDate(rule, today);
            if (s.Date == today && s.Time is { } at && at <= TimeOnly.FromDateTime(localNow))
                s.Date = Scheduling.Recurrence.FirstDate(rule, today.AddDays(1));
        }

        // A time with no date: the next time that clock time comes round.
        if (s.Time is { } t && s.Date is null)
            s.Date = t > TimeOnly.FromDateTime(localNow) ? today : today.AddDays(1);

        var title = string.Join(' ', tokens.Where((_, idx) => !used[idx]).Select(tk => tk.Text)).Trim();
        if (title.Length == 0)
            title = input.Trim();

        return new QuickAddResult(title, s.Date, s.Time, s.Priority, s.Tags, s.List, s.Recurrence);
    }

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < input.Length)
        {
            if (char.IsWhiteSpace(input[i])) { i++; continue; }
            if (input[i] == '"')
            {
                var end = input.IndexOf('"', i + 1);
                if (end > i)
                {
                    tokens.Add(new Token(input[(i + 1)..end], Literal: true));
                    i = end + 1;
                    continue;
                }
            }
            var j = i;
            while (j < input.Length && !char.IsWhiteSpace(input[j])) j++;
            tokens.Add(new Token(input[i..j], Literal: false));
            i = j;
        }
        return tokens;
    }

    private static bool TrySymbol(Token token, State s)
    {
        var text = token.Text.TrimEnd(',', ';');
        if (text.Length > 1 && text[0] == '#')
        {
            var tag = Domain.Tags.Normalize([text[1..]]);
            if (tag.Count == 1 && !s.Tags.Contains(tag[0])) s.Tags.Add(tag[0]);
            return tag.Count == 1;
        }
        if (text.Length > 1 && text[0] == '@')
        {
            s.List = text[1..].Replace('_', ' ');
            return true;
        }
        if (text.Length >= 2 && text[0] == '!')
        {
            Priority? p = text.ToLowerInvariant() switch
            {
                "!!" => Priority.High,
                "!!!" or "!!!!" => Priority.Urgent,
                "!1" or "!l" or "!low" => Priority.Low,
                "!2" or "!m" or "!med" or "!medium" => Priority.Medium,
                "!3" or "!h" or "!high" => Priority.High,
                "!4" or "!u" or "!urgent" => Priority.Urgent,
                "!0" or "!none" => Priority.None,
                _ => null,
            };
            if (p is not null) s.Priority = p;
            return p is not null;
        }
        return false;
    }

    /// <summary>Returns how many tokens starting at <paramref name="i"/> form a date/time phrase (0 = none).</summary>
    private static int TryDateOrTime(List<Token> t, int i, bool[] used, State s, DateOnly today, DateTime now, bool allowBareHour)
    {
        if (i >= t.Count || used[i] || t[i].Literal) return 0;
        string Key(int k) => k < t.Count && !used[k] && !t[k].Literal ? t[k].Key : "";
        var a = Key(i);
        var b = Key(i + 1);
        var c = Key(i + 2);

        // --- repeats: "every …" or a lone daily/weekly/…
        if (s.Recurrence is null)
        {
            if (a == "every")
            {
                var rest = new List<string>();
                for (var k = i + 1; k < t.Count && !used[k] && !t[k].Literal; k++) rest.Add(t[k].Key);
                if (Scheduling.Recurrence.TryFriendly(rest, out var rrule, out var consumed))
                {
                    s.Recurrence = rrule;
                    return consumed + 1;
                }
            }
            else if (a is "daily" or "weekly" or "monthly" or "yearly" or "annually" or "weekdays")
            {
                Scheduling.Recurrence.TryFriendly([a], out var rrule, out _);
                s.Recurrence = rrule;
                return 1;
            }
        }

        // --- relative: in N unit
        if (a == "in" && TryCount(b, out var count))
        {
            switch (c.TrimEnd('s'))
            {
                case "day": s.Date = today.AddDays(count); return 3;
                case "week" or "wk": s.Date = today.AddDays(7 * count); return 3;
                case "month" or "mo": s.Date = today.AddMonths(count); return 3;
                case "hour" or "hr" or "h":
                {
                    var at = now.AddHours(count);
                    s.Date = DateOnly.FromDateTime(at);
                    s.Time = new TimeOnly(at.Hour, at.Minute);
                    return 3;
                }
                case "minute" or "min" or "m":
                {
                    var at = now.AddMinutes(count);
                    s.Date = DateOnly.FromDateTime(at);
                    s.Time = new TimeOnly(at.Hour, at.Minute);
                    return 3;
                }
            }
        }

        // --- named days
        switch (a)
        {
            case "today" or "tod":
                s.Date = today;
                return 1;
            case "tonight":
                s.Date = today;
                s.Time ??= new TimeOnly(20, 0);
                return 1;
            case "tomorrow" or "tmr" or "tmrw" or "tomorow":
                s.Date = today.AddDays(1);
                return 1;
            case "weekend":
                s.Date = NextWeekday(today, DayOfWeek.Saturday, allowToday: true);
                return 1;
        }

        if (a == "next")
        {
            if (Weekdays.TryGetValue(b, out var wd))
            {
                s.Date = NextWeekday(today, wd, allowToday: false);
                return 2;
            }
            if (b == "week")
            {
                s.Date = NextWeekday(today, DayOfWeek.Monday, allowToday: false);
                return 2;
            }
            if (b == "month")
            {
                s.Date = today.AddMonths(1);
                return 2;
            }
        }

        if (Weekdays.TryGetValue(a, out var day))
        {
            s.Date = NextWeekday(today, day, allowToday: true);
            return 1;
        }

        // --- absolute dates
        var iso = IsoDate().Match(a);
        if (iso.Success && TryDate(int.Parse(iso.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(iso.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(iso.Groups[3].Value, CultureInfo.InvariantCulture), out var isoDate))
        {
            s.Date = isoDate;
            return 1;
        }

        // "5 nov [2027]" / "5th november"
        var dn = DayNumber().Match(a);
        if (dn.Success && Months.TryGetValue(b, out var m1))
        {
            var d = int.Parse(dn.Groups[1].Value, CultureInfo.InvariantCulture);
            return ResolveDayMonth(d, m1, c, today, s) is { } used1 ? 2 + used1 : 0;
        }
        // "nov 5 [2027]"
        if (Months.TryGetValue(a, out var m2))
        {
            var dn2 = DayNumber().Match(b);
            if (dn2.Success)
            {
                var d = int.Parse(dn2.Groups[1].Value, CultureInfo.InvariantCulture);
                return ResolveDayMonth(d, m2, c, today, s) is { } used2 ? 2 + used2 : 0;
            }
        }

        // --- times
        if (NamedTimes.TryGetValue(a, out var named))
        {
            s.Time = named;
            return 1;
        }

        // "9 am" written with a space
        if ((b is "am" or "pm") && TryAmPm(a + b, out var spaced))
        {
            s.Time = spaced;
            return 2;
        }
        if (TryAmPm(a, out var ampm))
        {
            s.Time = ampm;
            return 1;
        }

        var clock = ClockTime().Match(a);
        if (clock.Success)
        {
            s.Time = new TimeOnly(int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture));
            return 1;
        }

        // "at 5": hours 1–7 are afternoon (nobody means 5am by "at 5"), 8–23 as written
        if (allowBareHour && int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) && hour is >= 0 and <= 23)
        {
            s.Time = new TimeOnly(hour is >= 1 and <= 7 ? hour + 12 : hour, 0);
            return 1;
        }

        return 0;
    }

    /// <summary>Sets the date; returns extra tokens consumed (1 if a year followed), or null if invalid.</summary>
    private static int? ResolveDayMonth(int day, int month, string maybeYear, DateOnly today, State s)
    {
        if (maybeYear.Length == 4 && int.TryParse(maybeYear, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 2000 and <= 2100 && TryDate(year, month, day, out var withYear))
        {
            s.Date = withYear;
            return 1;
        }
        if (!TryDate(today.Year, month, day, out var date)) return null;
        if (date < today && TryDate(today.Year + 1, month, day, out var next)) date = next; // "5 jan" said in December
        s.Date = date;
        return 0;
    }

    private static bool TryAmPm(string text, out TimeOnly time)
    {
        time = default;
        var m = AmPmTime().Match(text);
        if (!m.Success) return false;
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (h is < 1 or > 12 || min > 59) return false;
        var pm = m.Groups[3].Value.StartsWith('p') || m.Groups[3].Value.StartsWith('P');
        h %= 12;
        if (pm) h += 12;
        time = new TimeOnly(h, min);
        return true;
    }

    private static bool TryCount(string text, out int count)
    {
        if (text is "a" or "an" or "one") { count = 1; return true; }
        if (text == "two") { count = 2; return true; }
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count is > 0 and <= 1000;
    }

    private static bool TryDate(int y, int m, int d, out DateOnly date)
    {
        date = default;
        if (m is < 1 or > 12 || d < 1 || y is < 1 or > 9999 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    private static DateOnly NextWeekday(DateOnly from, DayOfWeek day, bool allowToday)
    {
        var diff = ((int)day - (int)from.DayOfWeek + 7) % 7;
        if (diff == 0 && !allowToday) diff = 7;
        return from.AddDays(diff);
    }
}
