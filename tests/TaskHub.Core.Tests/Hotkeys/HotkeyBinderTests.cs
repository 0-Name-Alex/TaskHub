using Microsoft.Extensions.Logging.Abstractions;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Hosting;
using TaskHub.Core.Hotkeys;

namespace TaskHub.Core.Tests.Hotkeys;

public class HotkeyBinderTests
{
    private static (HotkeyBinder binder, FakeHotkeyService fake, ConfigStore config) Create(
        Core.Events.InMemoryEventBus bus, TempDir dir, TaskHubOptions? options = null)
    {
        var fake = new FakeHotkeyService();
        var config = new ConfigStore(AppPaths.UnderRoot(dir.Path), bus, NullLogger<ConfigStore>.Instance, options ?? new TaskHubOptions());
        var binder = new HotkeyBinder(fake, config, bus, NullLogger<HotkeyBinder>.Instance);
        return (binder, fake, config);
    }

    [Fact]
    public async Task Registers_all_configured_hotkeys_on_start()
    {
        await using var bus = TestBus.Create();
        using var dir = new TempDir();
        var (binder, fake, _) = Create(bus, dir);

        await binder.StartAsync(default);

        Assert.Equal("Ctrl+Alt+T", fake.Registered[HotkeyIds.ShowMainWindow].ToString());
        Assert.Equal("Ctrl+Alt+N", fake.Registered[HotkeyIds.QuickAdd].ToString());
        Assert.Equal("Ctrl+Alt+Space", fake.Registered[HotkeyIds.Launcher].ToString());
        Assert.All(binder.Status.Values.Where(s => s.Gesture.Length > 0), s => Assert.True(s.Registered));
        // toggle_dnd has no default gesture: reported as disabled, not as a failure
        Assert.False(binder.Status[HotkeyIds.ToggleDnd].Registered);
        Assert.Null(binder.Status[HotkeyIds.ToggleDnd].Error);
    }

    [Fact]
    public async Task Pressing_a_hotkey_publishes_hotkey_pressed()
    {
        await using var bus = TestBus.Create();
        using var dir = new TempDir();
        using var rec = new TestBus.Recorder(bus, EventTypes.HotkeyPressed);
        var (binder, fake, _) = Create(bus, dir);
        await binder.StartAsync(default);

        fake.Press(HotkeyIds.QuickAdd);

        var events = await rec.WaitForAsync(e => e.Count == 1);
        Assert.Equal(HotkeyIds.QuickAdd, events[0].DataAs<HotkeyPressedEvent>()!.HotkeyId);
        Assert.Equal(EventSources.Platform, events[0].Source);
    }

    [Fact]
    public async Task Reports_conflicts_and_bad_syntax_per_hotkey_without_failing_the_rest()
    {
        await using var bus = TestBus.Create();
        using var dir = new TempDir();
        using var rec = new TestBus.Recorder(bus, EventTypes.HotkeyFailed);
        var options = new TaskHubOptions { Hotkeys = { QuickAdd = "Ctrl+Banana", Launcher = "Ctrl+Alt+Space" } };
        var (binder, fake, _) = Create(bus, dir, options);
        fake.TakenByOtherApps.Add("Ctrl+Alt+Space");

        await binder.StartAsync(default);

        Assert.True(binder.Status[HotkeyIds.ShowMainWindow].Registered);
        Assert.False(binder.Status[HotkeyIds.QuickAdd].Registered);
        Assert.Contains("Banana", binder.Status[HotkeyIds.QuickAdd].Error);
        Assert.False(binder.Status[HotkeyIds.Launcher].Registered);
        Assert.Contains("already used", binder.Status[HotkeyIds.Launcher].Error);

        var failures = await rec.WaitForAsync(e => e.Count == 2);
        Assert.Equal(
            new[] { HotkeyIds.Launcher, HotkeyIds.QuickAdd },
            failures.Select(f => f.DataAs<HotkeyFailedEvent>()!.HotkeyId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Empty_gesture_disables_the_hotkey()
    {
        await using var bus = TestBus.Create();
        using var dir = new TempDir();
        var (binder, fake, _) = Create(bus, dir, new TaskHubOptions { Hotkeys = { Launcher = "" } });

        await binder.StartAsync(default);

        Assert.False(fake.Registered.ContainsKey(HotkeyIds.Launcher));
        Assert.Null(binder.Status[HotkeyIds.Launcher].Error);
    }

    [Fact]
    public async Task Rebinds_only_changed_hotkeys_when_config_file_changes()
    {
        await using var bus = TestBus.Create();
        using var dir = new TempDir();
        var (binder, fake, config) = Create(bus, dir);
        await binder.StartAsync(default);
        var callsAfterStart = fake.RegisterCalls;

        Directory.CreateDirectory(Path.GetDirectoryName(config.FilePath)!);
        File.WriteAllText(config.FilePath, """
            [hotkeys]
            show_main_window = "Win+Shift+T"
            quick_add = "Ctrl+Alt+N"
            launcher = "Ctrl+Alt+Space"
            """);
        Assert.True(config.Reload()); // publishes config.changed → binder rebinds on the bus thread

        var deadline = Environment.TickCount64 + 3000;
        while (fake.Registered[HotkeyIds.ShowMainWindow].ToString() != "Shift+Win+T" && Environment.TickCount64 < deadline)
            await Task.Delay(10);

        Assert.Equal("Shift+Win+T", fake.Registered[HotkeyIds.ShowMainWindow].ToString());
        Assert.Equal(callsAfterStart + 1, fake.RegisterCalls);
    }

    [Fact]
    public async Task Stop_unregisters_everything()
    {
        await using var bus = TestBus.Create();
        using var dir = new TempDir();
        var (binder, fake, _) = Create(bus, dir);
        await binder.StartAsync(default);

        await binder.StopAsync(default);

        Assert.Empty(fake.Registered);
    }
}
