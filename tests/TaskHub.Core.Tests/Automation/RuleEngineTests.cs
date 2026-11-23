using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using TaskHub.Abstractions.Contracts;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Automation;
using TaskHub.Core.Configuration;
using TaskHub.Core.Hosting;

namespace TaskHub.Core.Tests.Automation;

/// <summary>Records what it was asked to do instead of doing it.</summary>
internal sealed class RecordingAction(string name) : IAutomationAction
{
    public ConcurrentQueue<ActionInvocation> Calls { get; } = new();
    public Func<ActionInvocation, Task<string?>>? Behavior { get; set; }

    public string Name => name;

    public Task<string?> ExecuteAsync(ActionInvocation invocation, CancellationToken ct)
    {
        Calls.Enqueue(invocation);
        return Behavior?.Invoke(invocation) ?? Task.FromResult<string?>($"{name} ok");
    }
}

public class RuleEngineTests
{
    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDir _dir = new();

        public Rig(string rulesToml, string extra = "")
        {
            Bus = TestBus.Create();
            var options = ConfigLoader.Parse(extra + "\n" + rulesToml);
            Config = new ConfigStore(AppPaths.UnderRoot(_dir.Path), Bus, NullLogger<ConfigStore>.Instance, options);
            Notify = new RecordingAction(ActionNames.Notify);
            Open = new RecordingAction(ActionNames.Open);
            Run = new RecordingAction(ActionNames.Run);
            Add = new RecordingAction(ActionNames.TaskAdd);
            Dispatcher = new ActionDispatcher([Notify, Open, Run, Add], () => Config.Current.Automation, NullLogger<ActionDispatcher>.Instance);
            Activity = new AutomationActivity();
            Engine = new RuleEngine(Config, Bus, Dispatcher, Activity, Clock, NullLogger<RuleEngine>.Instance);
        }

        public Core.Events.InMemoryEventBus Bus { get; }
        public TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 3, 4, 0, 0, TimeSpan.Zero));
        public ConfigStore Config { get; }
        public RecordingAction Notify { get; }
        public RecordingAction Open { get; }
        public RecordingAction Run { get; }
        public RecordingAction Add { get; }
        public ActionDispatcher Dispatcher { get; }
        public AutomationActivity Activity { get; }
        public RuleEngine Engine { get; }

        public async Task<IReadOnlyList<RuleRun>> WaitForRunsAsync(int count, int timeoutMs = 3000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                var runs = Activity.Snapshot();
                if (runs.Count >= count) return runs;
                await Task.Delay(10);
            }
            throw new TimeoutException($"Expected {count} run(s), got {Activity.Snapshot().Count}");
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.StopAsync(default);
            Engine.Dispose();
            Config.Dispose();
            await Bus.DisposeAsync();
            _dir.Dispose();
        }
    }

    private static TaskDto MakeTask(string title, params string[] tags) =>
        new(Guid.NewGuid(), title, null, "open", "high", null, null, null, null, tags, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null);

    [Fact]
    public async Task Matching_event_and_condition_run_the_actions_in_order_with_placeholders()
    {
        await using var rig = new Rig("""
            [[rules]]
            name = "Standup"
            when = "reminder.due"
            if = "task.tags contains 'standup'"
            then = ["open https://meet.example.com/{task.title}", "notify Joining | {task.title}"]
            """);
        await rig.Engine.StartAsync(default);

        rig.Bus.Publish(EventTypes.ReminderDue, new ReminderEvent(MakeTask("other", "home"), DateTimeOffset.UtcNow, false));
        rig.Bus.Publish(EventTypes.ReminderDue, new ReminderEvent(MakeTask("daily", "standup"), DateTimeOffset.UtcNow, false));

        var runs = await rig.WaitForRunsAsync(1);
        Assert.True(runs[0].Ok, runs[0].Message);
        Assert.Equal("Standup", runs[0].Rule);
        Assert.Equal("reminder.due", runs[0].Trigger);
        var open = Assert.Single(rig.Open.Calls);
        Assert.Equal("https://meet.example.com/daily", open.Text("target"));
        Assert.Equal(ActionOrigin.Rule, open.Origin);
        Assert.Equal("rule:Standup", open.Source);
        Assert.Equal("Joining", Assert.Single(rig.Notify.Calls).Text("title"));
        Assert.Equal("daily", Assert.Single(rig.Notify.Calls).Text("body"));
    }

    [Fact]
    public async Task Events_caused_by_rules_never_trigger_rules()
    {
        await using var rig = new Rig("""
            [[rules]]
            name = "Echo"
            when = "task.*"
            then = ["add Again"]
            """);
        await rig.Engine.StartAsync(default);

        rig.Bus.Publish(EventTypes.TaskCreated, new TaskChangedEvent(MakeTask("from a rule")), EventSources.Rule("Echo"));
        rig.Bus.Publish(EventTypes.TaskCreated, new TaskChangedEvent(MakeTask("from the user")), EventSources.Ui);

        await rig.WaitForRunsAsync(1);
        await Task.Delay(100);
        Assert.Single(rig.Add.Calls);
    }

    [Fact]
    public async Task Cooldown_skips_runs_that_come_too_soon()
    {
        await using var rig = new Rig("""
            [[rules]]
            name = "Once"
            when = "system.locked"
            cooldown = 60
            then = ["notify Locked"]
            """);
        await rig.Engine.StartAsync(default);

        rig.Bus.Publish(EventTypes.SystemLocked);
        await rig.WaitForRunsAsync(1);
        rig.Bus.Publish(EventTypes.SystemLocked);
        await Task.Delay(150);
        Assert.Single(rig.Notify.Calls);

        rig.Clock.UtcNow += TimeSpan.FromSeconds(61);
        rig.Bus.Publish(EventTypes.SystemLocked);
        await rig.WaitForRunsAsync(2);
        Assert.Equal(2, rig.Notify.Calls.Count);
    }

    [Fact]
    public async Task A_failing_action_stops_the_rule_and_is_reported()
    {
        await using var rig = new Rig("""
            [[rules]]
            name = "Breaks"
            when = "app.started"
            then = ["open https://a.example", "notify never"]
            """);
        rig.Open.Behavior = _ => throw new AutomationException("open: nope");
        await rig.Engine.StartAsync(default);
        using var rec = new TestBus.Recorder(rig.Bus, EventTypes.RuleRan);

        rig.Bus.Publish(EventTypes.AppStarted);

        var runs = await rig.WaitForRunsAsync(1);
        Assert.False(runs[0].Ok);
        Assert.Equal("open: nope", runs[0].Message);
        Assert.Empty(rig.Notify.Calls);
        var ran = (await rec.WaitForAsync(e => e.Count == 1))[0].DataAs<RuleRanEvent>()!;
        Assert.False(ran.Ok);
        Assert.Equal("Breaks", ran.Rule);
    }

    [Fact]
    public async Task Rule_hotkeys_run_their_rule()
    {
        await using var rig = new Rig("""
            [[rules]]
            name = "Focus"
            hotkey = "Ctrl+Alt+F"
            then = ["notify Focus"]
            """);
        await rig.Engine.StartAsync(default);

        rig.Bus.Publish(EventTypes.HotkeyPressed, new HotkeyPressedEvent("rule:Focus"), EventSources.Platform);

        var runs = await rig.WaitForRunsAsync(1);
        Assert.Equal("hotkey", runs[0].Trigger);
        Assert.Single(rig.Notify.Calls);
        Assert.Equal("Ctrl+Alt+F", global::TaskHub.Core.Hotkeys.HotkeyBinder.HotkeyMap(rig.Config.Current)["rule:Focus"]);
    }

    [Fact]
    public async Task Run_now_ignores_trigger_and_condition_but_not_errors()
    {
        await using var rig = new Rig("""
            [[rules]]
            name = "Manual"
            when = "never.happens"
            if = "false"
            then = ["notify Hand"]

            [[rules]]
            name = "Broken"
            when = "x"
            then = ["nonsense"]
            """);

        var run = await rig.Engine.RunNowAsync("manual");
        Assert.True(run.Ok);
        Assert.Equal("manual", run.Trigger);
        await Assert.ThrowsAsync<AutomationException>(() => rig.Engine.RunNowAsync("Broken"));
        await Assert.ThrowsAsync<AutomationException>(() => rig.Engine.RunNowAsync("Missing"));
    }

    [Fact]
    public async Task Switching_rules_off_stops_them()
    {
        await using var rig = new Rig("""
            [[rules]]
            name = "Any"
            when = "*"
            then = ["notify x"]
            """, "[automation]\nrules_enabled = false");
        await rig.Engine.StartAsync(default);

        rig.Bus.Publish(EventTypes.AppStarted);
        await Task.Delay(150);
        Assert.Empty(rig.Notify.Calls);
    }

    [Fact]
    public async Task Tasks_may_only_open_links_unless_allowed()
    {
        await using var rig = new Rig("");
        var ctx = AutomationContext.Manual(rig.Clock);

        await rig.Dispatcher.ExecuteAsync(new ActionInvocation(ActionScript.ParseLine("open https://meet.example.com"), ctx, ActionOrigin.Task, "t"), default);
        await Assert.ThrowsAsync<AutomationException>(() =>
            rig.Dispatcher.ExecuteAsync(new ActionInvocation(ActionScript.ParseLine("open C:\\Windows\\notepad.exe"), ctx, ActionOrigin.Task, "t"), default));
        await Assert.ThrowsAsync<AutomationException>(() =>
            rig.Dispatcher.ExecuteAsync(new ActionInvocation(ActionScript.ParseLine("open file:///C:/x.bat"), ctx, ActionOrigin.Task, "t"), default));
        await Assert.ThrowsAsync<AutomationException>(() =>
            rig.Dispatcher.ExecuteAsync(new ActionInvocation(ActionScript.ParseLine("run python x.py"), ctx, ActionOrigin.Task, "t"), default));
        // Rules (the user's own config file) can do both.
        await rig.Dispatcher.ExecuteAsync(new ActionInvocation(ActionScript.ParseLine("run python x.py"), ctx, ActionOrigin.Rule, "r"), default);

        Assert.Single(rig.Open.Calls);
        Assert.Single(rig.Run.Calls);
    }

    [Fact]
    public async Task Reminder_actions_run_once_per_occurrence_and_not_for_old_missed_reminders()
    {
        await using var rig = new Rig("");
        var runner = new ReminderActionRunner(rig.Bus, rig.Dispatcher, rig.Activity, rig.Clock, NullLogger<ReminderActionRunner>.Instance);
        var task = MakeTask("Stand-up") with { DueDate = new DateOnly(2026, 11, 3), DueTime = "09:30", ReminderActions = "open https://meet.example.com/standup\nnotify Starting" };
        var due = EventEnvelope.Create(EventTypes.ReminderDue, null, "core", rig.Clock.UtcNow);

        var first = await runner.RunAsync(due, new ReminderEvent(task, rig.Clock.UtcNow, false), default);
        var again = await runner.RunAsync(due, new ReminderEvent(task, rig.Clock.UtcNow, false), default); // snooze / nag
        Assert.True(first!.Ok, first.Message);
        Assert.Null(again);
        Assert.Single(rig.Open.Calls);
        Assert.Equal(ActionOrigin.Task, rig.Open.Calls.Single().Origin);

        var nextOccurrence = task with { DueDate = new DateOnly(2026, 11, 4) };
        var stale = await runner.RunAsync(due, new ReminderEvent(nextOccurrence, rig.Clock.UtcNow - TimeSpan.FromHours(2), Missed: true), default);
        Assert.Null(stale);
        Assert.Single(rig.Open.Calls);
    }
}
