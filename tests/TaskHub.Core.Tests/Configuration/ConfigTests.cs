using Microsoft.Extensions.Logging.Abstractions;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Hosting;

namespace TaskHub.Core.Tests.Configuration;

public class ConfigLoaderTests
{
    [Fact]
    public void Default_file_matches_default_options()
    {
        var parsed = ConfigLoader.Parse(ConfigLoader.DefaultToml);
        var defaults = new TaskHubOptions();

        Assert.Equal(defaults.General.ShowMainWindowOnLaunch, parsed.General.ShowMainWindowOnLaunch);
        Assert.Equal(defaults.Hotkeys.ShowMainWindow, parsed.Hotkeys.ShowMainWindow);
        Assert.Equal(defaults.Hotkeys.QuickAdd, parsed.Hotkeys.QuickAdd);
        Assert.Equal(defaults.Hotkeys.Launcher, parsed.Hotkeys.Launcher);
        Assert.Equal(defaults.Logging.MinimumLevel, parsed.Logging.MinimumLevel);
        Assert.Equal(defaults.Logging.RetainedDays, parsed.Logging.RetainedDays);
        Assert.Equal(defaults.Api.Port, parsed.Api.Port);
    }

    [Fact]
    public void Empty_and_partial_files_fall_back_to_defaults()
    {
        Assert.Equal(47821, ConfigLoader.Parse("").Api.Port);

        var partial = ConfigLoader.Parse("""
            [hotkeys]
            quick_add = "Win+Q"
            """);
        Assert.Equal("Win+Q", partial.Hotkeys.QuickAdd);
        Assert.Equal("Ctrl+Alt+T", partial.Hotkeys.ShowMainWindow);
        Assert.Equal("Information", partial.Logging.MinimumLevel);
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        var o = ConfigLoader.Parse("""
            future_setting = 1
            [general]
            something_new = "x"
            """);
        Assert.True(o.General.ShowMainWindowOnLaunch);
    }

    [Theory]
    [InlineData("[api]\nport = 80", "api.port")]
    [InlineData("[logging]\nminimum_level = \"Loud\"", "minimum_level")]
    [InlineData("[logging]\nretained_days = 0", "retained_days")]
    [InlineData("[general\nbroken", "")]                     // TOML syntax error
    [InlineData("[api]\nport = \"not a number\"", "")]       // wrong type
    public void Invalid_files_throw_readable_errors(string toml, string mentions)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(toml));
        Assert.Contains(mentions, ex.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void LoadOrCreate_writes_default_file_on_first_run()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "config.toml");

        var result = ConfigLoader.LoadOrCreate(path);

        Assert.True(result.Created);
        Assert.Null(result.Error);
        Assert.Equal(ConfigLoader.DefaultToml, File.ReadAllText(path));

        var second = ConfigLoader.LoadOrCreate(path);
        Assert.False(second.Created);
    }

    [Fact]
    public void LoadOrCreate_returns_defaults_and_error_for_invalid_file()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.toml");
        File.WriteAllText(path, "[api]\nport = 1");

        var result = ConfigLoader.LoadOrCreate(path);

        Assert.NotNull(result.Error);
        Assert.Equal(47821, result.Options.Api.Port);
        Assert.Equal("[api]\nport = 1", File.ReadAllText(path)); // never overwrites the user's file
    }
}

public class ConfigUpgradeTests
{
    [Fact]
    public void Old_config_gets_missing_sections_appended_without_touching_existing_lines()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.toml");
        const string old = "# my comment\n[general]\nshow_main_window_on_launch = false\n\n[hotkeys]\nquick_add = \"Win+Q\"\n";
        File.WriteAllText(path, old);

        var result = ConfigLoader.LoadOrCreate(path);

        Assert.Null(result.Error);
        Assert.Equal(["logging", "api", "reminders", "maintenance", "notifications", "automation", "launcher"], result.AddedSections);
        var text = File.ReadAllText(path);
        Assert.StartsWith(old, text, StringComparison.Ordinal);
        Assert.Contains("snooze_minutes = 10", text, StringComparison.Ordinal);
        Assert.Contains("# Reminders for new tasks", text, StringComparison.Ordinal);

        var reparsed = ConfigLoader.Parse(text);
        Assert.False(reparsed.General.ShowMainWindowOnLaunch);   // user's value kept
        Assert.Equal("Win+Q", reparsed.Hotkeys.QuickAdd);
        Assert.Equal(10, reparsed.Reminders.SnoozeMinutes);

        Assert.Empty(ConfigLoader.LoadOrCreate(path).AddedSections!); // idempotent
    }

    [Fact]
    public void Default_sections_cover_every_section_of_the_default_file()
    {
        Assert.Equal(["general", "hotkeys", "logging", "api", "reminders", "maintenance", "notifications", "automation", "launcher"],
            ConfigLoader.DefaultSections().Select(s => s.Name));
        Assert.All(ConfigLoader.DefaultSections(), s => Assert.StartsWith("[" + s.Name + "]", s.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Defaults_include_reminder_settings()
    {
        var o = ConfigLoader.Parse(ConfigLoader.DefaultToml);
        Assert.Equal([0], o.Reminders.DefaultOffsets);
        Assert.Equal(new TimeOnly(9, 0), o.Reminders.GetAllDayTime());
        Assert.Equal(14, o.Maintenance.BackupKeepDays);
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse("[reminders]\nall_day_time = \"25:99\""));
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse("[reminders]\ndefault_offsets = [-5]"));
    }

    [Theory]
    [InlineData("[notifications]\nmode = \"pile\"", "mode")]
    [InlineData("[notifications]\nmax_visible = 0", "max_visible")]
    [InlineData("[notifications]\nanimation = \"wobble\"", "animation")]
    [InlineData("[notifications]\ndnd_behavior = \"ignore\"", "dnd_behavior")]
    [InlineData("[notifications]\nwidth = 100", "width")]
    [InlineData("[notifications]\nbackdrop = \"mica\"", "backdrop")]
    [InlineData("[notifications]\nstyle = \"bubble\"", "style")]
    [InlineData("[notifications]\nindicator_seconds = 0", "indicator_seconds")]
    public void Notification_settings_are_validated(string toml, string mentions)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(toml));
        Assert.Contains(mentions, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Notification_defaults()
    {
        var o = ConfigLoader.Parse(ConfigLoader.DefaultToml).Notifications;
        Assert.False(o.IsQueueMode());
        Assert.Equal(3, o.MaxVisible);
        Assert.Equal(TaskHub.Core.Notifications.AnimationPreset.Spring, o.GetAnimation());
        Assert.True(o.UseAcrylic());
        Assert.True(o.UseIsland());
        Assert.True(o.CameraMicIndicator);
        Assert.Equal(4, o.IndicatorSeconds);
        Assert.False(o.IndicatorDot);
        Assert.Equal("", ConfigLoader.Parse(ConfigLoader.DefaultToml).Hotkeys.ToggleDnd);
    }
}

public class ConfigStoreTests
{
    [Fact]
    public async Task Reload_applies_valid_changes_and_publishes_config_changed()
    {
        await using var bus = TestBus.Create();
        using var dir = new TempDir();
        using var rec = new TestBus.Recorder(bus, "config.*");
        var paths = AppPaths.UnderRoot(dir.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.ConfigFile, ConfigLoader.DefaultToml);
        using var store = new ConfigStore(paths, bus, NullLogger<ConfigStore>.Instance, ConfigLoader.Parse(ConfigLoader.DefaultToml));

        File.WriteAllText(paths.ConfigFile, "[api]\nport = 50000");
        Assert.True(store.Reload());

        Assert.Equal(50000, store.Current.Api.Port);
        var events = await rec.WaitForAsync(e => e.Count == 1);
        Assert.Equal(EventTypes.ConfigChanged, events[0].Type);
    }

    [Fact]
    public async Task Reload_keeps_previous_config_when_file_is_invalid()
    {
        await using var bus = TestBus.Create();
        using var dir = new TempDir();
        using var rec = new TestBus.Recorder(bus, "config.*");
        var paths = AppPaths.UnderRoot(dir.Path);
        paths.EnsureCreated();
        using var store = new ConfigStore(paths, bus, NullLogger<ConfigStore>.Instance, new TaskHubOptions());

        File.WriteAllText(paths.ConfigFile, "[api\nport = ");
        Assert.False(store.Reload());

        Assert.Equal(47821, store.Current.Api.Port);
        var events = await rec.WaitForAsync(e => e.Count == 1);
        Assert.Equal(EventTypes.ConfigInvalid, events[0].Type);
        Assert.Equal(paths.ConfigFile, events[0].DataAs<ConfigInvalidEvent>()!.Path);
    }

    [Fact]
    public async Task Watcher_picks_up_saved_changes()
    {
        await using var bus = TestBus.Create();
        using var dir = new TempDir();
        using var rec = new TestBus.Recorder(bus, EventTypes.ConfigChanged);
        var paths = AppPaths.UnderRoot(dir.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.ConfigFile, ConfigLoader.DefaultToml);
        using var store = new ConfigStore(paths, bus, NullLogger<ConfigStore>.Instance, new TaskHubOptions());
        await store.StartAsync(default);

        File.WriteAllText(paths.ConfigFile, "[logging]\nminimum_level = \"Debug\"");

        await rec.WaitForAsync(e => e.Count >= 1, timeoutMs: 5000);
        Assert.Equal("Debug", store.Current.Logging.MinimumLevel);
        await store.StopAsync(default);
    }
}
