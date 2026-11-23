namespace TaskHub.Core.Configuration;

/// <summary>[automation] in config.toml. Rules, folder watchers and shortcuts are separate
/// <c>[[rules]]</c>, <c>[[watch]]</c> and <c>[[shortcuts]]</c> tables (see <see cref="AutomationConfig"/>).</summary>
public sealed class AutomationOptions
{
    /// <summary>Master switch for rules (watchers and system events still publish events).</summary>
    public bool RulesEnabled { get; set; } = true;

    /// <summary>No keyboard/mouse input for this many minutes → <c>system.idle</c> (0 = off).</summary>
    public int IdleMinutes { get; set; } = 10;

    /// <summary>On battery at or below this percentage → <c>system.battery.low</c> (0 = off).</summary>
    public int BatteryLowPercent { get; set; } = 20;

    /// <summary>A fixed drive with less free space than this (GB) → <c>system.disk.low</c> (0 = off).</summary>
    public int DiskLowGb { get; set; } = 10;

    /// <summary>
    /// Let a task's "when reminded" actions start programs (<c>run</c>, or <c>open</c> of anything but a
    /// web/mail link). Off by default because tasks can also come from the API, imports and plugins.
    /// Rules in this file can always do both.
    /// </summary>
    public bool AllowTaskPrograms { get; set; }

    /// <summary>Safety valve against runaway rules: at most this many rule runs per minute in total.</summary>
    public int MaxRunsPerMinute { get; set; } = 30;

    /// <summary>List that clipboard/screenshot captures go to ("" = Inbox). Created if missing.</summary>
    public string CaptureList { get; set; } = "";

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (IdleMinutes is < 0 or > 1440) errors.Add("automation.idle_minutes must be between 0 and 1440.");
        if (BatteryLowPercent is < 0 or > 99) errors.Add("automation.battery_low_percent must be between 0 and 99.");
        if (DiskLowGb is < 0 or > 100_000) errors.Add("automation.disk_low_gb must be between 0 and 100000.");
        if (MaxRunsPerMinute is < 1 or > 1000) errors.Add("automation.max_runs_per_minute must be between 1 and 1000.");
        if (CaptureList.Length > Domain.TaskList.MaxNameLength) errors.Add("automation.capture_list is too long.");
        return errors;
    }
}

/// <summary>[launcher] in config.toml.</summary>
public sealed class LauncherOptions
{
    /// <summary>Offer apps from the Start menu.</summary>
    public bool IncludeStartMenu { get; set; } = true;

    /// <summary>Search open tasks as you type.</summary>
    public bool IncludeTasks { get; set; } = true;

    public int MaxResults { get; set; } = 9;

    public List<string> Validate() =>
        MaxResults is < 3 or > 30 ? ["launcher.max_results must be between 3 and 30."] : [];
}
