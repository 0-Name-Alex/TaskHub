using System.Globalization;
using System.Text.RegularExpressions;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace TaskHub.Core.Scheduling;

/// <summary>
/// Repeating schedules as iCalendar RRULEs (RFC 5545), evaluated with Ical.Net.
/// Rules are expanded in <b>floating local time</b> — "every Monday 09:00" stays 09:00 local
/// across DST changes; the caller converts each local occurrence to UTC with the user's zone.
/// </summary>
public static partial class Recurrence
{
    private static readonly (string Code, string Name, DayOfWeek Day)[] Days =
    [
        ("MO", "Mon", DayOfWeek.Monday), ("TU", "Tue", DayOfWeek.Tuesday), ("WE", "Wed", DayOfWeek.Wednesday),
        ("TH", "Thu", DayOfWeek.Thursday), ("FR", "Fri", DayOfWeek.Friday), ("SA", "Sat", DayOfWeek.Saturday),
        ("SU", "Sun", DayOfWeek.Sunday),
    ];

    private static readonly Dictionary<string, string> DayWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monday"] = "MO", ["mon"] = "MO", ["mondays"] = "MO",
        ["tuesday"] = "TU", ["tue"] = "TU", ["tues"] = "TU", ["tuesdays"] = "TU",
        ["wednesday"] = "WE", ["wed"] = "WE", ["wednesdays"] = "WE",
        ["thursday"] = "TH", ["thu"] = "TH", ["thur"] = "TH", ["thurs"] = "TH", ["thursdays"] = "TH",
        ["friday"] = "FR", ["fri"] = "FR", ["fridays"] = "FR",
        ["saturday"] = "SA", ["sat"] = "SA", ["saturdays"] = "SA",
        ["sunday"] = "SU", ["sun"] = "SU", ["sundays"] = "SU",
    };

    private static readonly string[] WeekdayCodesSorted = ["FR", "MO", "TH", "TU", "WE"];

    [GeneratedRegex(@"^FREQ=(SECONDLY|MINUTELY|HOURLY|DAILY|WEEKLY|MONTHLY|YEARLY)(;[A-Z]+=[A-Z0-9,+\-:]+)*$")]
    private static partial Regex RRuleShape();

    /// <summary>
    /// Accepts friendly text or a raw RRULE and returns a normalised RRULE (without "RRULE:").
    /// Friendly forms: daily, every day, weekdays, every weekday, weekly, every week, monthly,
    /// yearly/annually, every N days|weeks|months|years, every monday, every mon,wed,fri.
    /// </summary>
    public static bool TryParse(string? text, out string rrule, out string? error)
    {
        rrule = "";
        error = null;
        var s = (text ?? "").Trim();
        if (s.Length == 0)
        {
            error = "Repeat rule is empty.";
            return false;
        }

        if (s.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)) s = s[6..];
        if (s.StartsWith("FREQ=", StringComparison.OrdinalIgnoreCase))
        {
            var upper = s.ToUpperInvariant().TrimEnd(';');
            if (!RRuleShape().IsMatch(upper) || upper.Contains("FREQ=SECONDLY", StringComparison.Ordinal)
                || upper.Contains("FREQ=MINUTELY", StringComparison.Ordinal) || upper.Contains("FREQ=HOURLY", StringComparison.Ordinal))
            {
                error = $"'{text}' isn't a supported RRULE. Use FREQ=DAILY, WEEKLY, MONTHLY or YEARLY.";
                return false;
            }
            try
            {
                _ = new RecurrencePattern(upper);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
            {
                error = $"Invalid RRULE: {ex.Message}";
                return false;
            }
            rrule = upper;
            return true;
        }

        var words = s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words[0] == "every") words = words[1..];
        if (words.Length == 0)
        {
            error = "Say what to repeat, e.g. 'every day' or 'every monday'.";
            return false;
        }

        if (TryFriendly(words, out rrule, out var consumed) && consumed == words.Length) return true;
        rrule = "";
        error = $"Don't understand '{text}'. Try: daily, weekdays, every monday, every 2 weeks, monthly, or an RRULE.";
        return false;
    }

    /// <summary>
    /// Parses the words after "every" (or a lone daily/weekly/…). <paramref name="consumed"/> is how
    /// many words were used, so the quick-add parser can keep the rest as title text.
    /// </summary>
    internal static bool TryFriendly(IReadOnlyList<string> w, out string rrule, out int consumed)
    {
        rrule = "";
        consumed = 0;
        if (w.Count == 0) return false;
        string Word(int i) => i < w.Count ? w[i].TrimEnd(',', '.', ';') : "";

        switch (Word(0))
        {
            case "day" or "daily": rrule = "FREQ=DAILY"; consumed = 1; return true;
            case "weekday" or "weekdays": rrule = "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"; consumed = 1; return true;
            case "weekend" or "weekends": rrule = "FREQ=WEEKLY;BYDAY=SA,SU"; consumed = 1; return true;
            case "week" or "weekly": rrule = "FREQ=WEEKLY"; consumed = 1; return true;
            case "month" or "monthly": rrule = "FREQ=MONTHLY"; consumed = 1; return true;
            case "year" or "yearly" or "annually": rrule = "FREQ=YEARLY"; consumed = 1; return true;
        }

        // every 2 weeks / every 3 days / every other week
        var n = Word(0) == "other" ? 2 : int.TryParse(Word(0), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        if (n is >= 1 and <= 999)
        {
            var freq = Word(1).TrimEnd('s') switch
            {
                "day" => "DAILY",
                "week" => "WEEKLY",
                "month" => "MONTHLY",
                "year" => "YEARLY",
                _ => null,
            };
            if (freq is null) return false;
            rrule = n == 1 ? $"FREQ={freq}" : $"FREQ={freq};INTERVAL={n}";
            consumed = 2;
            return true;
        }

        // every monday / every mon,wed,fri / every monday and thursday
        var codes = new List<string>();
        var i = 0;
        for (; i < w.Count; i++)
        {
            var word = w[i].Trim(',', '.', ';');
            if (word is "and" or "&" && codes.Count > 0) continue;
            var parts = word.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !parts.All(p => DayWords.ContainsKey(p))) break;
            foreach (var p in parts)
            {
                var code = DayWords[p];
                if (!codes.Contains(code)) codes.Add(code);
            }
        }
        if (codes.Count == 0) return false;
        // a trailing "and" that wasn't followed by a day belongs to the title
        while (i > 0 && w[i - 1].Trim(',', '.', ';') is "and" or "&") i--;
        var ordered = Days.Where(d => codes.Contains(d.Code)).Select(d => d.Code);
        rrule = $"FREQ=WEEKLY;BYDAY={string.Join(',', ordered)}";
        consumed = i;
        return true;
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="afterLocal"/> for a series that started at
    /// <paramref name="startLocal"/> (both floating local times). Null when the series has ended.
    /// </summary>
    public static DateTime? NextOccurrence(string rrule, DateTime startLocal, DateTime afterLocal)
    {
        var start = DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified);
        var after = DateTime.SpecifyKind(afterLocal, DateTimeKind.Unspecified);
        var evt = new CalendarEvent { DtStart = new CalDateTime(start) };
        evt.RecurrenceRules.Add(new RecurrencePattern(rrule));

        // Search widening windows so sparse rules (every 10 years) still resolve quickly.
        var from = after > start ? after : start;
        foreach (var span in new[] { TimeSpan.FromDays(40), TimeSpan.FromDays(400), TimeSpan.FromDays(4000), TimeSpan.FromDays(40000) })
        {
            var next = evt.GetOccurrences(from, from + span)
                .Select(o => o.Period.StartTime.Value)
                .Where(d => d > after)
                .OrderBy(d => d)
                .Cast<DateTime?>()
                .FirstOrDefault();
            if (next is not null) return DateTime.SpecifyKind(next.Value, DateTimeKind.Unspecified);
        }
        return null;
    }

    /// <summary>Human description, e.g. "Every 2 weeks on Mon, Thu".</summary>
    public static string Describe(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return "Does not repeat";
        var parts = rrule.ToUpperInvariant().Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]);
        var interval = parts.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, CultureInfo.InvariantCulture, out var n) && n > 1 ? n : 1;
        var unit = parts.GetValueOrDefault("FREQ") switch
        {
            "DAILY" => "day",
            "WEEKLY" => "week",
            "MONTHLY" => "month",
            "YEARLY" => "year",
            _ => null,
        };
        if (unit is null) return rrule;

        var text = interval == 1 ? $"Every {unit}" : $"Every {interval} {unit}s";
        if (parts.TryGetValue("BYDAY", out var byDay))
        {
            var codes = byDay.Split(',');
            if (unit == "week" && interval == 1 && codes.Order(StringComparer.Ordinal).SequenceEqual(WeekdayCodesSorted))
                text = "Every weekday";
            else if (unit == "week" && interval == 1 && codes.Length == 1)
                text = "Every " + Days.FirstOrDefault(d => d.Code == codes[0]).Day.ToString();
            else
                text += " on " + string.Join(", ", codes.Select(c => Days.FirstOrDefault(d => c.EndsWith(d.Code, StringComparison.Ordinal)).Name ?? c));
        }
        if (parts.TryGetValue("COUNT", out var count)) text += $", {count} times";
        if (parts.TryGetValue("UNTIL", out var until) && until.Length >= 8)
            text += $", until {until[..4]}-{until[4..6]}-{until[6..8]}";
        return text;
    }

    /// <summary>For "every monday": the weekday the series is anchored to, if the rule is a single BYDAY.</summary>
    internal static DayOfWeek? SingleWeekday(string rrule)
    {
        var m = Regex.Match(rrule, @"BYDAY=([A-Z]{2})(?:;|$)");
        return m.Success ? Days.First(d => d.Code == m.Groups[1].Value).Day : null;
    }

    /// <summary>The first day the series can fall on, on or after <paramref name="from"/> (date-level).</summary>
    internal static DateOnly FirstDate(string rrule, DateOnly from)
    {
        var byDay = Regex.Match(rrule, @"BYDAY=([A-Z,]+)");
        if (!byDay.Success) return from;
        var allowed = byDay.Groups[1].Value.Split(',').Select(c => Days.First(d => d.Code == c[^2..]).Day).ToHashSet();
        var d = from;
        while (!allowed.Contains(d.DayOfWeek)) d = d.AddDays(1);
        return d;
    }
}
