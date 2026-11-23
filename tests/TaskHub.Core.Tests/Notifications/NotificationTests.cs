using Microsoft.Extensions.Logging.Abstractions;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Notifications;

namespace TaskHub.Core.Tests.Notifications;

/// <summary>Records what the center asks it to draw; tests simulate clicks and closes.</summary>
internal sealed class FakeSurface : IBannerSurface
{
    public List<(NotificationRequest Request, BannerStyle Style)> Shown { get; } = [];
    public HashSet<Guid> Open { get; } = [];

    public event EventHandler<BannerActionEventArgs>? ActionInvoked;
    public event EventHandler<BannerClosedEventArgs>? Closed;

    public void Show(NotificationRequest request, BannerStyle style)
    {
        Shown.Add((request, style));
        Open.Add(request.Id);
    }

    public void Close(Guid id) => UserClose(id, BannerCloseReason.Programmatic);

    public void Click(Guid id, string action)
    {
        ActionInvoked?.Invoke(this, new BannerActionEventArgs(id, action));
        UserClose(id, BannerCloseReason.Action);
    }

    public void UserClose(Guid id, BannerCloseReason reason)
    {
        if (Open.Remove(id)) Closed?.Invoke(this, new BannerClosedEventArgs(id, reason));
    }
}

public class NotificationCenterTests
{
    private static (NotificationCenter Center, FakeSurface Surface, NotificationOptions Options, global::TaskHub.Core.Events.InMemoryEventBus Bus) Create()
    {
        var surface = new FakeSurface();
        var options = new NotificationOptions();
        var bus = TestBus.Create();
        var center = new NotificationCenter(surface, bus, new TestClock(DateTimeOffset.UnixEpoch), new NotificationHistory(),
            () => options, NullLogger<NotificationCenter>.Instance);
        return (center, surface, options, bus);
    }

    private static NotificationRequest N(string title, NotificationPriority p = NotificationPriority.Normal, params Guid[] tasks) =>
        new() { Title = title, Priority = p, TaskIds = tasks };

    [Fact]
    public void Stack_mode_shows_up_to_max_visible_then_queues()
    {
        var (center, surface, options, _) = Create();
        options.MaxVisible = 2;

        center.Show(N("a"));
        center.Show(N("b"));
        center.Show(N("c"));

        Assert.Equal(["a", "b"], surface.Shown.Select(s => s.Request.Title));
        Assert.Equal(1, center.QueuedCount);

        surface.UserClose(surface.Shown[0].Request.Id, BannerCloseReason.Dismissed);
        Assert.Equal(["a", "b", "c"], surface.Shown.Select(s => s.Request.Title));
        Assert.Equal(0, center.QueuedCount);
    }

    [Fact]
    public void Queue_mode_shows_one_at_a_time()
    {
        var (center, surface, options, _) = Create();
        options.Mode = "queue";
        center.Show(N("a"));
        center.Show(N("b"));
        Assert.Single(surface.Shown);
        surface.UserClose(surface.Shown[0].Request.Id, BannerCloseReason.Timeout);
        Assert.Equal(2, surface.Shown.Count);
    }

    [Fact]
    public void Urgent_jumps_the_queue()
    {
        var (center, surface, options, _) = Create();
        options.Mode = "queue";
        center.Show(N("first"));
        center.Show(N("normal"));
        center.Show(N("urgent", NotificationPriority.Urgent));
        surface.UserClose(surface.Shown[0].Request.Id, BannerCloseReason.Dismissed);
        Assert.Equal("urgent", surface.Shown[1].Request.Title);
    }

    [Fact]
    public void Dnd_queue_holds_until_off_then_shows()
    {
        var (center, surface, options, _) = Create();
        center.SetDoNotDisturb(true);
        center.Show(N("a"));
        center.Show(N("b"));
        Assert.Empty(surface.Shown);
        Assert.Equal(NotificationStatus.Queued, center.History.Snapshot()[0].Status);

        center.SetDoNotDisturb(false);
        Assert.Equal(["a", "b"], surface.Shown.Select(s => s.Request.Title));
        _ = options;
    }

    [Fact]
    public void Dnd_off_with_many_queued_shows_one_summary()
    {
        var (center, surface, options, _) = Create();
        options.GroupQueuedAbove = 3;
        center.SetDoNotDisturb(true);
        var t = Guid.NewGuid();
        for (var i = 0; i < 5; i++) center.Show(N($"n{i}", NotificationPriority.Normal, t));

        center.SetDoNotDisturb(false);

        var shown = Assert.Single(surface.Shown);
        Assert.Equal(NotificationKind.Summary, shown.Request.Kind);
        Assert.StartsWith("5 notifications", shown.Request.Title, StringComparison.Ordinal);
        Assert.Equal([t], shown.Request.TaskIds);
    }

    [Fact]
    public void Dnd_silence_records_history_only()
    {
        var (center, surface, options, _) = Create();
        options.DndBehavior = "silence";
        center.SetDoNotDisturb(true);
        center.Show(N("quiet"));
        center.SetDoNotDisturb(false);

        Assert.Empty(surface.Shown);
        Assert.Equal(NotificationStatus.Silenced, center.History.Snapshot().Single().Status);
    }

    [Fact]
    public void Urgent_bypasses_dnd_unless_disabled()
    {
        var (center, surface, options, _) = Create();
        center.SetDoNotDisturb(true);
        center.Show(N("fire", NotificationPriority.Urgent));
        Assert.Single(surface.Shown);

        options.UrgentBypassesDnd = false;
        center.Show(N("fire 2", NotificationPriority.Urgent));
        Assert.Single(surface.Shown);
    }

    [Fact]
    public async Task Clicks_become_notification_action_events_and_history_outcomes()
    {
        var (center, surface, _, bus) = Create();
        using var rec = new TestBus.Recorder(bus, EventTypes.NotificationAction);
        var task = Guid.NewGuid();
        center.Show(N("reminder", NotificationPriority.Normal, task));
        var id = surface.Shown[0].Request.Id;

        surface.Click(id, "snooze:15");

        var e = (await rec.WaitForAsync(x => x.Count == 1))[0].DataAs<NotificationActionEvent>()!;
        Assert.Equal(id, e.NotificationId);
        Assert.Equal("snooze:15", e.Action);
        Assert.Equal([task], e.TaskIds);
        var h = center.History.Find(id)!;
        Assert.Equal(NotificationStatus.Closed, h.Status);
        Assert.Equal("snooze:15", h.Outcome);
    }

    [Fact]
    public void Dismiss_for_tasks_closes_visible_and_drops_queued()
    {
        var (center, surface, options, _) = Create();
        options.MaxVisible = 1;
        var done = Guid.NewGuid();
        center.Show(N("visible", NotificationPriority.Normal, done));
        center.Show(N("queued", NotificationPriority.Normal, done));
        center.Show(N("other", NotificationPriority.Normal, Guid.NewGuid()));

        center.DismissForTasks([done]);

        Assert.Equal(["visible", "other"], surface.Shown.Select(s => s.Request.Title));
        Assert.Equal("resolved elsewhere", center.History.Snapshot().Single(h => h.Title == "queued").Outcome);
    }

    [Fact]
    public async Task Dnd_change_publishes_event_once()
    {
        var (center, _, _, bus) = Create();
        using var rec = new TestBus.Recorder(bus, EventTypes.DndChanged);
        center.SetDoNotDisturb(true);
        center.SetDoNotDisturb(true);
        center.ToggleDoNotDisturb();
        var events = await rec.WaitForAsync(x => x.Count == 2);
        Assert.Equal([true, false], events.Select(x => x.DataAs<DndChangedEvent>()!.Enabled));
    }

    [Theory]
    [InlineData(NotificationKind.Reminder, 60)]
    [InlineData(NotificationKind.Summary, 60)]
    [InlineData(NotificationKind.Info, 8)]
    public void Style_uses_duration_by_kind(NotificationKind kind, int seconds)
    {
        var style = NotificationCenter.StyleFor(new NotificationRequest { Title = "x", Kind = kind }, new NotificationOptions());
        Assert.Equal(TimeSpan.FromSeconds(seconds), style.Duration);
    }

    [Fact]
    public void Style_zero_means_sticky_and_request_overrides()
    {
        var o = new NotificationOptions { ReminderSeconds = 0, Sound = false, Animation = "drop" };
        var reminder = NotificationCenter.StyleFor(new NotificationRequest { Title = "x", Kind = NotificationKind.Reminder, TaskIds = [Guid.NewGuid()] }, o);
        Assert.Null(reminder.Duration);
        Assert.False(reminder.PlaySound);
        Assert.True(reminder.AllowReply);
        Assert.Equal(AnimationPreset.Drop, reminder.Animation);

        var custom = NotificationCenter.StyleFor(new NotificationRequest { Title = "x", Duration = TimeSpan.FromSeconds(3), PlaySound = true }, o);
        Assert.Equal(TimeSpan.FromSeconds(3), custom.Duration);
        Assert.True(custom.PlaySound);
        Assert.False(custom.AllowReply);
    }
}

public class ReplyParserTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 10, 30, 0);

    [Theory]
    [InlineData("done", "done")]
    [InlineData("D", "done")]
    [InlineData("✓", "done")]
    [InlineData("snooze", "snooze")]
    [InlineData("s", "snooze")]
    [InlineData("snooze 10m", "snooze:10")]
    [InlineData("snooze 10", "snooze:10")]
    [InlineData("s 1h", "snooze:60")]
    [InlineData("15m", "snooze:15")]
    [InlineData("2 hours", "snooze:120")]
    [InlineData("dismiss", "dismiss")]
    [InlineData("open", "open")]
    [InlineData("tomorrow 9am", "reschedule:tomorrow 9am")]
    [InlineData("Friday", "reschedule:friday")]
    [InlineData("in 3 days", "reschedule:in 3 days")]
    public void Understands_replies(string text, string expected)
    {
        Assert.True(ReplyParser.TryParse(text, Now, out var action, out var error), error);
        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("blah blah")]
    [InlineData("snooze 0")]
    [InlineData("snooze 99999h")]
    public void Rejects_with_help(string text)
    {
        Assert.False(ReplyParser.TryParse(text, Now, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("snooze", 10, true, 10)]
    [InlineData("snooze:45", 10, true, 45)]
    [InlineData("snooze:x", 10, false, 0)]
    [InlineData("done", 10, false, 0)]
    public void Snooze_action_minutes(string action, int def, bool ok, int minutes)
    {
        Assert.Equal(ok, NotificationActions.TryGetSnoozeMinutes(action, def, out var m));
        if (ok) Assert.Equal(minutes, m);
    }
}

public class BannerLayoutTests
{
    [Fact]
    public void Stacks_downward_from_the_top_with_margin_and_gap()
    {
        Assert.Equal([12d, 132d, 222d], BannerLayout.StackTops(0, [110, 80, 100], margin: 12, gap: 10));
        Assert.Equal([1092d], BannerLayout.StackTops(1080, [50], margin: 12, gap: 10)); // second monitor below
    }

    [Fact]
    public void Centres_and_hides_above_screen()
    {
        Assert.Equal(760, BannerLayout.CenterLeft(0, 1920, 400));
        Assert.Equal(2680, BannerLayout.CenterLeft(1920, 1920, 400));
        Assert.Equal(-134, BannerLayout.AboveScreen(0, 110, 24));
    }

    [Fact]
    public void Countdown_fraction()
    {
        Assert.Equal(1, BannerLayout.Remaining(TimeSpan.FromSeconds(10), TimeSpan.Zero));
        Assert.Equal(0.5, BannerLayout.Remaining(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)), 3);
        Assert.Equal(0, BannerLayout.Remaining(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12)));
    }
}

public class NotificationHistoryTests
{
    [Fact]
    public void Persists_and_reloads_newest_first()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "notifications.json");
        var h = new NotificationHistory(file);
        h.Add(new NotificationRequest { Title = "old" }, NotificationStatus.Shown, DateTimeOffset.UnixEpoch);
        var latest = new NotificationRequest { Title = "new", Kind = NotificationKind.Reminder };
        h.Add(latest, NotificationStatus.Shown, DateTimeOffset.UnixEpoch.AddMinutes(1));
        h.Update(latest.Id, e => e with { Status = NotificationStatus.Closed, Outcome = "done" });
        h.SaveNow();

        var reloaded = new NotificationHistory(file).Snapshot();
        Assert.Equal(["new", "old"], reloaded.Select(e => e.Title));
        Assert.Equal("done", reloaded[0].Outcome);
        Assert.Equal(NotificationKind.Reminder, reloaded[0].Kind);
    }

    [Fact]
    public void Keeps_at_most_capacity_and_survives_a_corrupt_file()
    {
        var h = new NotificationHistory();
        for (var i = 0; i < NotificationHistory.Capacity + 20; i++)
            h.Add(new NotificationRequest { Title = $"n{i}" }, NotificationStatus.Shown, DateTimeOffset.UnixEpoch);
        Assert.Equal(NotificationHistory.Capacity, h.Snapshot().Count);

        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "bad.json");
        File.WriteAllText(file, "{ not json");
        Assert.Empty(new NotificationHistory(file).Snapshot());
    }
}
