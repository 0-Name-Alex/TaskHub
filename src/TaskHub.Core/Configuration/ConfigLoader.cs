using TaskHub.Core.Hotkeys;
using Tomlyn;

namespace TaskHub.Core.Configuration;

public sealed record ConfigLoadResult(TaskHubOptions Options, string? Error, bool Created, IReadOnlyList<string>? AddedSections = null);

public sealed class ConfigException(string message) : Exception(message);

public static class ConfigLoader
{
    /// <summary>Written on first run. Kept in sync with the defaults in <see cref="TaskHubOptions"/>.</summary>
    public const string DefaultToml = """
        # TaskHub configuration. Changes apply automatically when you save this file.

        [general]
        # Show the main window when TaskHub is launched manually (autostart always starts in the tray).
        show_main_window_on_launch = true

        [hotkeys]
        # Format: Ctrl+Alt+T, Win+Shift+N, Ctrl+F12 ... Use "" to disable a hotkey.
        show_main_window = "Ctrl+Alt+T"
        quick_add = "Ctrl+Alt+N"
        launcher = "Ctrl+Alt+Space"
        toggle_dnd = ""
        # Clipboard (text, image or files) → new task; drag a rectangle → screenshot task.
        capture_clipboard = "Ctrl+Alt+V"
        capture_screenshot = "Ctrl+Alt+S"

        [logging]
        # Verbose, Debug, Information, Warning, Error, Fatal
        minimum_level = "Information"
        retained_days = 14

        [api]
        # Local API, bound to 127.0.0.1 only.
        port = 47821

        [reminders]
        # Reminders for new tasks, in minutes before the due time (0 = at due). [] = none.
        default_offsets = [0]
        # Tasks with a date but no time are reminded at this time ("" = never).
        all_day_time = "09:00"
        snooze_minutes = 10
        # After sleep/shutdown, more missed reminders than this are grouped into one banner.
        group_missed_above = 3

        [maintenance]
        backup_keep_days = 14
        trash_retention_days = 30

        [notifications]
        # "island": a pill at the top of the screen that grows into the notification and shrinks away.
        # "banner": separate frosted cards that stack (mode, max_visible, animation, backdrop apply).
        style = "island"
        # Briefly show which app starts using the camera or microphone.
        camera_mic_indicator = true
        # Show "Camera off" / "Camera on" when the laptop's camera key or switch is used.
        camera_switch_indicator = true
        indicator_seconds = 4
        # Keep a small coloured dot at the top while the camera/mic stays in use.
        indicator_dot = false
        # "stack" shows up to max_visible at once; "queue" shows one at a time.
        mode = "stack"
        max_visible = 3
        # Seconds before a banner hides itself (0 = stay until dismissed). Hovering pauses it.
        reminder_seconds = 60
        info_seconds = 8
        # spring | slide | drop | fade
        animation = "spring"
        sound = true
        # While Do Not Disturb is on: "queue" (show when it ends) or "silence" (history only).
        dnd_behavior = "queue"
        urgent_bypasses_dnd = true
        width = 400
        # "acrylic" (frosted glass, Windows 11 22H2+) or "solid".
        backdrop = "acrylic"
        # Use a solid background while a banner moves, if the glass effect stutters.
        solid_while_animating = false
        group_queued_above = 3

        [automation]
        # Master switch for all rules (see the examples below).
        rules_enabled = true
        # Events for rules: system.idle after this many minutes without input (0 = off),
        # system.battery.low at this percentage on battery, system.disk.low under this many GB free.
        idle_minutes = 10
        battery_low_percent = 20
        disk_low_gb = 10
        # Let a task's "When reminded" actions start programs (run …, or open anything but a web/mail
        # link). Off because tasks can also come from the API and imports. Rules here can always do it.
        allow_task_programs = false
        # Safety valve: at most this many rule runs per minute.
        max_runs_per_minute = 30
        # List for clipboard/screenshot captures ("" = Inbox).
        capture_list = ""

        # Rules: when an event happens (and the 'if' holds), do the 'then' actions. Examples:
        #
        # [[rules]]
        # name = "Stand-up link"
        # when = "reminder.due"
        # if   = "task.tags contains 'standup'"
        # then = ["open https://meet.example.com/standup"]
        #
        # [[rules]]
        # name = "Invoices to tasks"
        # when = "file.detected"
        # if   = "watch == 'downloads' and name contains 'invoice'"
        # then = ["add Pay {name} tomorrow #finance", "notify New invoice | {name}"]
        #
        # [[rules]]
        # name = "Focus mode"
        # hotkey = "Ctrl+Alt+F"
        # then = ["dnd toggle", { action = "run", program = "pwsh", args = ["-File", "C:\\scripts\\focus.ps1"] }]
        #
        # Folders to watch (each new file publishes file.detected with path, name, extension, watch):
        #
        # [[watch]]
        # name = "downloads"
        # path = "~/Downloads"
        # filter = ["*.pdf", "*.docx"]
        #
        # Extra quick-launcher entries:
        #
        # [[shortcuts]]
        # name = "Team wiki"
        # target = "https://wiki.example.com"
        # keywords = "docs notes"

        [launcher]
        include_start_menu = true
        include_tasks = true
        max_results = 9

        """;

    /// <summary>Parses and validates TOML. Throws <see cref="ConfigException"/> with a readable message.</summary>
    public static TaskHubOptions Parse(string toml, string? sourcePath = null)
    {
        var options = new TomlModelOptions { IgnoreMissingProperties = true };
        TaskHubOptions? model;
        try
        {
            if (!Toml.TryToModel(toml, out model, out var diagnostics, sourcePath, options) || model is null)
                throw new ConfigException(string.Join(Environment.NewLine, diagnostics?.Select(d => d.ToString()) ?? Enumerable.Empty<string>()));
        }
        catch (Exception ex) when (ex is not ConfigException)
        {
            // Type mismatches (e.g. port = "abc") surface as exceptions rather than diagnostics.
            throw new ConfigException($"{sourcePath ?? "config"}: {ex.Message}");
        }

        // Sections present but empty in TOML can leave nulls behind; normalise.
        model.General ??= new();
        model.Hotkeys ??= new();
        model.Logging ??= new();
        model.Api ??= new();
        model.Reminders ??= new();
        model.Maintenance ??= new();
        model.Notifications ??= new();
        model.Automation ??= new();
        model.Launcher ??= new();
        model.Reminders.DefaultOffsets ??= [];

        // [[rules]], [[watch]], [[shortcuts]]: parsed by hand (per-entry errors don't reject the file).
        model.SetAutomationConfig(AutomationConfig.Parse(Toml.ToModel(toml, sourcePath)));

        var errors = Validate(model);
        if (errors.Count > 0)
            throw new ConfigException(string.Join(Environment.NewLine, errors));
        return model;
    }

    /// <summary>
    /// Semantic checks. Hotkey syntax errors are deliberately NOT fatal here: the hotkey
    /// binder reports them per hotkey so one typo doesn't reject the whole file.
    /// </summary>
    public static List<string> Validate(TaskHubOptions o)
    {
        var errors = new List<string>();
        if (o.Api.Port is < 1024 or > 65535)
            errors.Add($"api.port must be between 1024 and 65535 (got {o.Api.Port}).");
        if (!LoggingOptions.Levels.Contains(o.Logging.MinimumLevel, StringComparer.OrdinalIgnoreCase))
            errors.Add($"logging.minimum_level must be one of {string.Join(", ", LoggingOptions.Levels)} (got '{o.Logging.MinimumLevel}').");
        if (o.Reminders.DefaultOffsets.Any(m => m is < 0 or > 525_600))
            errors.Add("reminders.default_offsets must be minutes between 0 and 525600.");
        if (o.Reminders.AllDayTime.Length > 0 && o.Reminders.GetAllDayTime() is null)
            errors.Add($"reminders.all_day_time must be HH:mm or \"\" (got '{o.Reminders.AllDayTime}').");
        if (o.Reminders.SnoozeMinutes is < 1 or > 1440)
            errors.Add($"reminders.snooze_minutes must be between 1 and 1440 (got {o.Reminders.SnoozeMinutes}).");
        if (o.Reminders.GroupMissedAbove is < 0 or > 100)
            errors.Add("reminders.group_missed_above must be between 0 and 100.");
        if (o.Maintenance.BackupKeepDays is < 1 or > 365)
            errors.Add("maintenance.backup_keep_days must be between 1 and 365.");
        if (o.Maintenance.TrashRetentionDays is < 1 or > 3650)
            errors.Add("maintenance.trash_retention_days must be between 1 and 3650.");
        errors.AddRange(o.Notifications.Validate());
        errors.AddRange(o.Automation.Validate());
        errors.AddRange(o.Launcher.Validate());
        if (o.Logging.RetainedDays is < 1 or > 365)
            errors.Add($"logging.retained_days must be between 1 and 365 (got {o.Logging.RetainedDays}).");
        return errors;
    }

    /// <summary>
    /// Loads the config file, creating it with defaults if missing. If the file is invalid,
    /// returns defaults plus the error so the app still starts.
    /// </summary>
    public static ConfigLoadResult LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DefaultToml);
            return new ConfigLoadResult(Parse(DefaultToml, path), null, Created: true);
        }

        try
        {
            var text = ReadShared(path);
            var options = Parse(text, path);
            var added = AppendMissingSections(path, text);
            return new ConfigLoadResult(options, null, Created: false, added);
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            return new ConfigLoadResult(new TaskHubOptions(), ex.Message, Created: false);
        }
    }

    /// <summary>
    /// Config files written by an older TaskHub lack newer sections. Appends each missing
    /// <c>[section]</c> from <see cref="DefaultToml"/> (with its comments) to the end of the file, so new
    /// settings are discoverable. Existing content is never modified. Returns the sections added.
    /// </summary>
    public static IReadOnlyList<string> AppendMissingSections(string path, string currentText)
    {
        var present = Toml.ToModel(currentText).Keys.ToHashSet(StringComparer.Ordinal);
        var missing = DefaultSections().Where(s => !present.Contains(s.Name)).ToList();
        if (missing.Count == 0) return [];

        var sb = new System.Text.StringBuilder();
        if (!currentText.EndsWith('\n')) sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("# --- Added by a newer TaskHub version (defaults; edit as you like) ---");
        foreach (var section in missing)
        {
            sb.AppendLine();
            sb.Append(section.Text.TrimEnd()).AppendLine();
        }
        File.AppendAllText(path, sb.ToString());
        return missing.Select(s => s.Name).ToList();
    }

    /// <summary>Splits <see cref="DefaultToml"/> into its [sections], each with the comment lines above it.</summary>
    internal static IEnumerable<(string Name, string Text)> DefaultSections()
    {
        var lines = DefaultToml.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var start = -1;
        string? name = null;
        for (var i = 0; i <= lines.Length; i++)
        {
            var isHeader = i < lines.Length && lines[i].StartsWith('[');
            if ((isHeader || i == lines.Length) && name is not null)
            {
                yield return (name, string.Join('\n', lines[start..i]).TrimEnd());
            }
            if (isHeader)
            {
                name = lines[i].Trim().Trim('[', ']');
                start = i;
            }
        }
    }

    /// <summary>Reads while an editor may still hold the file open.</summary>
    internal static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Convenience used by the UI to show which hotkeys are syntactically valid.</summary>
    public static bool IsValidHotkey(string text) => HotkeyGesture.TryParse(text, out _, out _);
}
