using System.Text;

namespace TaskHub.Core.Automation;

/// <summary>Canonical action names. Aliases (e.g. <c>open.url</c>, <c>done</c>) are accepted too.</summary>
public static class ActionNames
{
    public const string Open = "open";
    public const string Run = "run";
    public const string Notify = "notify";
    public const string TaskAdd = "task.add";
    public const string TaskComplete = "task.complete";
    public const string TaskSnooze = "task.snooze";
    public const string Dnd = "dnd";
    public const string ClipboardSet = "clipboard.set";
    public const string Log = "log";
    public const string Wait = "wait";
    public const string CaptureScreenshot = "capture.screenshot";
    public const string CaptureClipboard = "capture.clipboard";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [Open] = Open, ["open.url"] = Open, ["open.file"] = Open, ["open.app"] = Open, ["launch"] = Open,
        [Run] = Run, ["run.script"] = Run, ["script"] = Run,
        [Notify] = Notify, ["notification"] = Notify, ["banner"] = Notify,
        [TaskAdd] = TaskAdd, ["add"] = TaskAdd, ["task.create"] = TaskAdd,
        [TaskComplete] = TaskComplete, ["done"] = TaskComplete, ["complete"] = TaskComplete,
        [TaskSnooze] = TaskSnooze, ["snooze"] = TaskSnooze,
        [Dnd] = Dnd, ["do_not_disturb"] = Dnd,
        [ClipboardSet] = ClipboardSet, ["copy"] = ClipboardSet,
        [Log] = Log,
        [Wait] = Wait, ["delay"] = Wait, ["sleep"] = Wait,
        [CaptureScreenshot] = CaptureScreenshot, ["screenshot"] = CaptureScreenshot,
        [CaptureClipboard] = CaptureClipboard, ["clip"] = CaptureClipboard, ["capture-clipboard"] = CaptureClipboard,
    };

    public static IReadOnlyList<string> All { get; } =
        [Open, Run, Notify, TaskAdd, TaskComplete, TaskSnooze, Dnd, ClipboardSet, Log, Wait, CaptureScreenshot, CaptureClipboard];

    public static bool TryNormalize(string? name, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(name) || !Aliases.TryGetValue(name.Trim(), out var c)) return false;
        canonical = c;
        return true;
    }
}

/// <summary>
/// The one-line action syntax used in rules (<c>then = ["open https://…", "notify Hi"]</c>) and in a
/// task's "when reminded" box (one action per line):
/// <code>
/// open https://meet.example.com/standup      open a URL, file, folder or app ("quote paths with spaces")
/// open "C:\Tools\app.exe" --flag             … with arguments
/// run python "C:\scripts\sync.py" {path}      start a program (arguments are passed as-is, never through a shell)
/// notify Stand-up in 5 | Grab a coffee        banner: title | body
/// add Review {name} tomorrow #inbox            add a task (quick-add syntax)
/// done                                        complete the task the event is about
/// snooze 15                                   snooze that task (minutes; default from settings)
/// dnd on | off | toggle                       Do Not Disturb
/// copy {task.title}                           put text on the clipboard
/// wait 5                                      pause (seconds, max 300)
/// log Something happened                      write to the TaskHub log
/// screenshot · clip                           capture a screenshot / the clipboard as a task
/// </code>
/// Blank lines and lines starting with <c>#</c> are ignored. <c>{…}</c> placeholders are filled in from
/// the event when the action runs.
/// </summary>
public static class ActionScript
{
    public const int MaxLines = 20;

    /// <summary>Parses a block of lines. Throws <see cref="AutomationException"/> naming the bad line.</summary>
    public static IReadOnlyList<ActionSpec> Parse(string? text)
    {
        var result = new List<ActionSpec>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            try
            {
                result.Add(ParseLine(line));
            }
            catch (AutomationException ex)
            {
                throw new AutomationException(lines.Length == 1 ? ex.Message : $"Line {i + 1}: {ex.Message}");
            }
        }
        if (result.Count > MaxLines) throw new AutomationException($"At most {MaxLines} actions.");
        return result;
    }

    /// <summary>True if the text parses; <paramref name="error"/> says what's wrong otherwise.</summary>
    public static bool TryParse(string? text, out IReadOnlyList<ActionSpec> actions, out string? error)
    {
        try
        {
            actions = Parse(text);
            error = null;
            return true;
        }
        catch (AutomationException ex)
        {
            actions = [];
            error = ex.Message;
            return false;
        }
    }

    public static ActionSpec ParseLine(string line)
    {
        line = line.Trim();
        var space = line.IndexOfAny([' ', '\t']);
        var verb = space < 0 ? line : line[..space];
        var rest = space < 0 ? "" : line[(space + 1)..].Trim();
        if (!ActionNames.TryNormalize(verb, out var name))
            throw new AutomationException($"Unknown action '{verb}'. Use one of: open, run, notify, add, done, snooze, dnd, copy, wait, log, screenshot, clip.");

        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (name)
        {
            case ActionNames.Open:
            {
                if (rest.Length == 0) throw new AutomationException("open needs a URL, file, folder or program.");
                var (first, remainder) = SplitFirst(rest);
                args["target"] = first;
                if (remainder.Length > 0) args["args"] = remainder;
                break;
            }
            case ActionNames.Run:
            {
                if (rest.Length == 0) throw new AutomationException("run needs a program, e.g. run python \"C:\\scripts\\x.py\".");
                var (first, remainder) = SplitFirst(rest);
                args["program"] = first;
                if (remainder.Length > 0) args["args"] = remainder;
                break;
            }
            case ActionNames.Notify:
            {
                if (rest.Length == 0) throw new AutomationException("notify needs a title.");
                var bar = rest.IndexOf('|', StringComparison.Ordinal);
                args["title"] = (bar < 0 ? rest : rest[..bar]).Trim();
                if (bar >= 0 && rest[(bar + 1)..].Trim() is { Length: > 0 } body) args["body"] = body;
                break;
            }
            case ActionNames.TaskAdd:
                if (rest.Length == 0) throw new AutomationException("add needs the task text.");
                args["text"] = rest;
                break;
            case ActionNames.TaskSnooze:
                if (rest.Length > 0)
                {
                    if (!int.TryParse(rest.TrimEnd('m', 'M'), out var minutes) || minutes is < 1 or > 43_200)
                        throw new AutomationException($"snooze takes minutes, e.g. snooze 15 (got '{rest}').");
                    args["minutes"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                break;
            case ActionNames.Dnd:
            {
                var state = rest.Length == 0 ? "toggle" : rest.ToLowerInvariant();
                if (state is not ("on" or "off" or "toggle"))
                    throw new AutomationException($"dnd takes on, off or toggle (got '{rest}').");
                args["state"] = state;
                break;
            }
            case ActionNames.ClipboardSet:
                if (rest.Length == 0) throw new AutomationException("copy needs some text.");
                args["text"] = rest;
                break;
            case ActionNames.Log:
                args["message"] = rest;
                break;
            case ActionNames.Wait:
                if (!double.TryParse(rest.TrimEnd('s', 'S'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                    || seconds is <= 0 or > 300)
                    throw new AutomationException($"wait takes seconds between 0 and 300 (got '{rest}').");
                args["seconds"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case ActionNames.TaskComplete:
            case ActionNames.CaptureScreenshot:
            case ActionNames.CaptureClipboard:
                if (rest.Length > 0) throw new AutomationException($"{verb} doesn't take anything after it.");
                break;
        }
        return new ActionSpec(name, args) { Text = line };
    }

    /// <summary>Splits off the first word, honouring "double quotes" (for paths with spaces).</summary>
    public static (string First, string Remainder) SplitFirst(string text)
    {
        text = text.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close < 0) throw new AutomationException("Missing closing quote.");
            return (text[1..close], text[(close + 1)..].Trim());
        }
        var space = text.IndexOfAny([' ', '\t']);
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    /// <summary>
    /// Splits a command line into arguments: whitespace separates, "double quotes" group (and are removed).
    /// Backslashes are literal, so Windows paths work without escaping.
    /// </summary>
    public static List<string> SplitArguments(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) result.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (inQuotes) throw new AutomationException("Missing closing quote.");
        if (hasToken) result.Add(current.ToString());
        return result;
    }
}
