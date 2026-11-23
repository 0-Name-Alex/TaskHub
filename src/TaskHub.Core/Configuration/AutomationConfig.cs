using System.Globalization;
using TaskHub.Core.Automation;
using TaskHub.Core.Events;
using Tomlyn.Model;

namespace TaskHub.Core.Configuration;

/// <summary>
/// A rule from <c>[[rules]]</c>. Problems with one rule (bad condition, unknown action …) don't reject
/// the whole config file: the rule is kept with <see cref="Error"/> set, shown in the Automation tab,
/// and doesn't run.
/// </summary>
public sealed record RuleDefinition
{
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Event patterns: <c>reminder.due</c>, <c>task.*</c>, <c>file.detected</c> …</summary>
    public IReadOnlyList<string> When { get; init; } = [];
    /// <summary>Optional global hotkey that runs the rule.</summary>
    public string? Hotkey { get; init; }
    public string? If { get; init; }
    public Condition? Condition { get; init; }
    public IReadOnlyList<ActionSpec> Then { get; init; } = [];
    /// <summary>Minimum seconds between two runs.</summary>
    public int CooldownSeconds { get; init; }
    public string? Error { get; init; }

    public bool IsRunnable => Enabled && Error is null;

    /// <summary>Id of the rule's hotkey in <c>hotkey.pressed</c> events.</summary>
    public string HotkeyId => HotkeyIdFor(Name);

    public static string HotkeyIdFor(string ruleName) => "rule:" + ruleName;

    public string TriggerText => string.Join(", ", When.Concat(Hotkey is { Length: > 0 } h ? [h] : Array.Empty<string>()));
}

/// <summary>A folder from <c>[[watch]]</c>; new/changed files there publish <c>file.detected</c>.</summary>
public sealed record WatchDefinition
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    /// <summary>Wildcards like <c>*.pdf</c>; empty = everything.</summary>
    public IReadOnlyList<string> Filters { get; init; } = [];
    public bool Subfolders { get; init; }
    /// <summary>created, changed, deleted, renamed.</summary>
    public IReadOnlyList<string> Events { get; init; } = ["created"];
    public string? Error { get; init; }

    public static readonly string[] EventNames = ["created", "changed", "deleted", "renamed"];
}

/// <summary>An entry from <c>[[shortcuts]]</c>, offered by the quick launcher.</summary>
public sealed record ShortcutDefinition(string Name, string Target, string? Arguments, string? Keywords);

/// <summary>The table-array parts of config.toml, which the typed TOML mapper can't express.</summary>
public sealed record AutomationConfig
{
    public static readonly AutomationConfig Empty = new();

    public IReadOnlyList<RuleDefinition> Rules { get; init; } = [];
    public IReadOnlyList<WatchDefinition> Watches { get; init; } = [];
    public IReadOnlyList<ShortcutDefinition> Shortcuts { get; init; } = [];
    /// <summary>Problems that aren't about a single rule/watch (shown in the Automation tab).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public static AutomationConfig Parse(TomlTable root)
    {
        var errors = new List<string>();
        return new AutomationConfig
        {
            Rules = ParseRules(root, errors),
            Watches = ParseWatches(root, errors),
            Shortcuts = ParseShortcuts(root, errors),
            Errors = errors,
        };
    }

    // ------------------------------------------------------------------ rules

    private static List<RuleDefinition> ParseRules(TomlTable root, List<string> errors)
    {
        var result = new List<RuleDefinition>();
        var tables = Tables(root, "rules", errors);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tables.Count; i++)
        {
            var t = tables[i];
            var problems = new List<string>();
            var name = Str(t, "name")?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                name = $"Rule {i + 1}";
                problems.Add("needs a name");
            }
            else if (!names.Add(name))
            {
                problems.Add("another rule has the same name");
            }

            var when = StrList(t, "when").Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 0).ToList();
            foreach (var w in when.Where(w => !TopicPattern.IsValidPattern(w)))
                problems.Add($"'{w}' is not an event pattern (e.g. reminder.due, task.*, file.detected)");
            var hotkey = Str(t, "hotkey")?.Trim();
            if (hotkey is { Length: > 0 } && !Hotkeys.HotkeyGesture.TryParse(hotkey, out _, out var hotkeyError))
                problems.Add($"hotkey: {hotkeyError}");
            if (when.Count == 0 && string.IsNullOrEmpty(hotkey))
                problems.Add("needs 'when' (an event, e.g. \"reminder.due\") or a 'hotkey'");

            Condition? condition = null;
            var ifText = Str(t, "if")?.Trim();
            if (!string.IsNullOrEmpty(ifText) && !Condition.TryParse(ifText, out condition, out var condError))
                problems.Add($"if: {condError}");

            var actions = new List<ActionSpec>();
            if (!t.TryGetValue("then", out var thenValue) || thenValue is null)
            {
                problems.Add("needs 'then' (what to do)");
            }
            else
            {
                IEnumerable<object?> items = thenValue switch
                {
                    TomlArray arr => arr,
                    TomlTableArray tarr => tarr.Cast<object?>(),
                    _ => new object?[] { thenValue },
                };
                foreach (var item in items)
                {
                    try
                    {
                        actions.Add(ParseAction(item));
                    }
                    catch (AutomationException ex)
                    {
                        problems.Add($"then: {ex.Message}");
                    }
                }
                if (actions.Count == 0 && problems.Count == 0) problems.Add("'then' is empty");
            }

            var cooldown = Int(t, "cooldown", 0, problems);
            if (cooldown is < 0 or > 86_400) problems.Add("cooldown must be between 0 and 86400 seconds");

            result.Add(new RuleDefinition
            {
                Name = name,
                Enabled = Bool(t, "enabled", true, problems),
                When = when,
                Hotkey = string.IsNullOrEmpty(hotkey) ? null : hotkey,
                If = string.IsNullOrEmpty(ifText) ? null : ifText,
                Condition = condition,
                Then = actions,
                CooldownSeconds = cooldown,
                Error = problems.Count == 0 ? null : string.Join("; ", problems) + ".",
            });
        }
        return result;
    }

    /// <summary>"open https://…" or { action = "open", target = "https://…" }.</summary>
    internal static ActionSpec ParseAction(object? item)
    {
        switch (item)
        {
            case string line:
                return ActionScript.ParseLine(line);
            case TomlTable table:
            {
                var nameValue = Str(table, "action") ?? Str(table, "do")
                    ?? throw new AutomationException("an action table needs action = \"…\"");
                if (!ActionNames.TryNormalize(nameValue, out var name))
                    throw new AutomationException($"unknown action '{nameValue}'");
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in table)
                {
                    if (key is "action" or "do") continue;
                    args[NormalizeKey(name, key)] = ToText(value);
                }
                if (name == ActionNames.Run && !args.ContainsKey("program") && !args.ContainsKey("script"))
                    throw new AutomationException("run needs program = \"…\" (or shell + script)");
                if (name == ActionNames.Open && !args.ContainsKey("target"))
                    throw new AutomationException("open needs target = \"…\" (a URL, file, folder or program)");
                return new ActionSpec(name, args);
            }
            default:
                throw new AutomationException("each action must be text (\"open https://…\") or a table ({ action = \"open\", … })");
        }
    }

    /// <summary>Friendly synonyms: { action = "open.url", url = "…" } → target.</summary>
    private static string NormalizeKey(string action, string key) => (action, key.ToLowerInvariant()) switch
    {
        (ActionNames.Open, "url" or "path" or "file" or "app") => "target",
        (ActionNames.Open, "arguments") => "args",
        (ActionNames.Run, "file" or "exe" or "command" or "path") => "program",
        (ActionNames.Run, "arguments") => "args",
        (ActionNames.Run, "cwd" or "working_directory") => "working_dir",
        (ActionNames.Notify, "message") => "body",
        (_, var k) => k,
    };

    // ------------------------------------------------------------------ watchers

    private static List<WatchDefinition> ParseWatches(TomlTable root, List<string> errors)
    {
        var result = new List<WatchDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tables(root, "watch", errors))
        {
            var problems = new List<string>();
            var path = Str(t, "path")?.Trim() ?? "";
            if (path.Length == 0) problems.Add("needs a path");
            var name = Str(t, "name")?.Trim();
            if (string.IsNullOrEmpty(name))
                name = path.Length == 0 ? $"watch {result.Count + 1}" : System.IO.Path.GetFileName(path.TrimEnd('\\', '/')).ToLowerInvariant();
            if (!names.Add(name)) problems.Add("another watch has the same name");
            var events = StrList(t, "events").Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
            if (events.Count == 0) events.Add("created");
            foreach (var e in events.Where(e => !WatchDefinition.EventNames.Contains(e)))
                problems.Add($"unknown event '{e}' (use created, changed, deleted, renamed)");

            result.Add(new WatchDefinition
            {
                Name = name,
                Path = path,
                Filters = StrList(t, "filter").Concat(StrList(t, "filters")).Select(f => f.Trim()).Where(f => f.Length > 0 && f != "*" && f != "*.*").ToList(),
                Subfolders = Bool(t, "subfolders", false, problems),
                Events = events,
                Error = problems.Count == 0 ? null : string.Join("; ", problems) + ".",
            });
        }
        return result;
    }

    // ------------------------------------------------------------------ shortcuts

    private static List<ShortcutDefinition> ParseShortcuts(TomlTable root, List<string> errors)
    {
        var result = new List<ShortcutDefinition>();
        foreach (var t in Tables(root, "shortcuts", errors))
        {
            var name = Str(t, "name")?.Trim();
            var target = (Str(t, "target") ?? Str(t, "url") ?? Str(t, "path"))?.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(target))
            {
                errors.Add($"[[shortcuts]] entry {result.Count + 1} needs a name and a target.");
                continue;
            }
            result.Add(new ShortcutDefinition(name, target, Str(t, "args"), Str(t, "keywords")));
        }
        return result;
    }

    // ------------------------------------------------------------------ TOML helpers

    private static List<TomlTable> Tables(TomlTable root, string key, List<string> errors)
    {
        if (!root.TryGetValue(key, out var value) || value is null) return [];
        switch (value)
        {
            case TomlTableArray array:
                return array.ToList();
            case TomlTable single:
                return [single];
            case TomlArray arr when arr.All(x => x is TomlTable):
                return arr.Cast<TomlTable>().ToList();
            default:
                errors.Add($"'{key}' must be written as [[{key}]] tables.");
                return [];
        }
    }

    private static string? Str(TomlTable t, string key) =>
        t.TryGetValue(key, out var v) && v is not null and not TomlTable and not TomlArray and not TomlTableArray ? ToText(v) : null;

    private static List<string> StrList(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var v) || v is null) return [];
        return v is TomlArray arr ? arr.Where(x => x is not null).Select(x => ToText(x!)).ToList() : [ToText(v)];
    }

    private static bool Bool(TomlTable t, string key, bool fallback, List<string> problems)
    {
        if (!t.TryGetValue(key, out var v) || v is null) return fallback;
        if (v is bool b) return b;
        problems.Add($"{key} must be true or false");
        return fallback;
    }

    private static int Int(TomlTable t, string key, int fallback, List<string> problems)
    {
        if (!t.TryGetValue(key, out var v) || v is null) return fallback;
        if (v is long l && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        problems.Add($"{key} must be a whole number");
        return fallback;
    }

    private static string ToText(object value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        TomlArray arr => string.Join(' ', arr.Where(x => x is not null).Select(x => ToText(x!)).Select(s => s.Contains(' ', StringComparison.Ordinal) ? $"\"{s}\"" : s)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}
