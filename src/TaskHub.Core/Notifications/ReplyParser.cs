using System.Globalization;
using System.Text.RegularExpressions;
using TaskHub.Core.Parsing;

namespace TaskHub.Core.Notifications;

/// <summary>
/// Understands what you type into a banner's reply box:
/// <c>done</c> · <c>snooze</c> · <c>snooze 10m</c> · <c>s 1h</c> · <c>15m</c> · <c>dismiss</c> · <c>open</c> ·
/// or a new due date/time in quick-add language (<c>tomorrow 9am</c>, <c>friday</c>, <c>in 2 hours</c>).
/// Produces a <see cref="NotificationActions"/> action string.
/// </summary>
public static partial class ReplyParser
{
    public const string Help = "Try: snooze 10m · 1h · done · tomorrow 9am";

    [GeneratedRegex(@"^(?:(?:snooze|s|z)\s*)?(\d{1,5})?\s*(m|min|mins|minute|minutes|h|hr|hrs|hour|hours)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SnoozePattern();

    public static bool TryParse(string? text, DateTime localNow, out string action, out string? error)
    {
        action = "";
        error = null;
        var s = (text ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0)
        {
            error = Help;
            return false;
        }

        switch (s)
        {
            case "done" or "d" or "complete" or "completed" or "finish" or "finished" or "✓":
                action = NotificationActions.Done;
                return true;
            case "dismiss" or "close" or "x" or "ignore":
                action = NotificationActions.Dismiss;
                return true;
            case "open" or "o" or "show":
                action = NotificationActions.Open;
                return true;
            case "snooze" or "s" or "z" or "later":
                action = NotificationActions.Snooze;
                return true;
        }

        var m = SnoozePattern().Match(s);
        if (m.Success && m.Groups[1].Success)
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var minutes = m.Groups[2].Value.StartsWith('h') ? n * 60 : n;
            if (minutes is < 1 or > 43_200)
            {
                error = "Snooze between 1 minute and 30 days.";
                return false;
            }
            action = $"{NotificationActions.Snooze}:{minutes}";
            return true;
        }

        // Anything else: a new due date/time ("tomorrow 9am", "friday", "in 2 hours").
        var parsed = QuickAddParser.Parse(s, localNow);
        if (parsed.DueDate is not null || parsed.DueTime is not null)
        {
            action = NotificationActions.ReschedulePrefix + s;
            return true;
        }

        error = Help;
        return false;
    }
}
