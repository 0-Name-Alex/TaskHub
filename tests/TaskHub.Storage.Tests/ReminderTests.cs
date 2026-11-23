using TaskHub.Abstractions.Events;
using TaskHub.Core.Domain;
using TaskHub.Core.Services;

namespace TaskHub.Storage.Tests;

public class ReminderTests
{
    // Harness "now": Mon 2 Nov 2026 10:30 IST (05:00 UTC)

    [Fact]
    public async Task New_timed_task_gets_a_reminder_at_due_by_default()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("call bob today 11am");
        Assert.Equal([0], t.ReminderOffsets);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero), t.NextReminderUtc);
    }

    [Fact]
    public async Task Scheduler_fires_when_due_and_not_before()
    {
        await using var h = await Harness.CreateAsync();
        var scheduler = h.CreateScheduler();
        var t = await h.Tasks.QuickAddAsync("call bob today 11am");

        Assert.Equal(0, await scheduler.FireDueAsync());

        h.Clock.Advance(TimeSpan.FromMinutes(30)); // 11:00
        Assert.Equal(1, await scheduler.FireDueAsync());
        var e = await h.WaitForEventAsync(EventTypes.ReminderDue);
        var r = e.DataAs<ReminderEvent>()!;
        Assert.Equal(t.Id, r.Task.Id);
        Assert.False(r.Missed);

        var after = await h.Tasks.GetRequiredAsync(t.Id);
        Assert.Null(after.NextReminderUtc);           // single reminder: done
        Assert.Equal(h.Clock.UtcNow, after.LastReminderUtc);
        Assert.Equal(0, await scheduler.FireDueAsync()); // doesn't fire twice
    }

    [Fact]
    public async Task Multiple_offsets_fire_in_order()
    {
        await using var h = await Harness.CreateAsync();
        var scheduler = h.CreateScheduler();
        var t = await h.Tasks.CreateAsync(new NewTask
        {
            Title = "meeting", DueDate = Harness.Today, DueTime = new TimeOnly(12, 0), ReminderOffsets = [0, 15],
        });
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 15, 0, TimeSpan.Zero), t.NextReminderUtc); // 11:45 IST

        h.Clock.UtcNow = new DateTimeOffset(2026, 11, 2, 6, 15, 0, TimeSpan.Zero);
        Assert.Equal(1, await scheduler.FireDueAsync());
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), (await h.Tasks.GetRequiredAsync(t.Id)).NextReminderUtc);
    }

    [Fact]
    public async Task Nag_keeps_reminding_until_completed()
    {
        await using var h = await Harness.CreateAsync();
        var scheduler = h.CreateScheduler();
        var t = await h.Tasks.CreateAsync(new NewTask
        {
            Title = "take pills", DueDate = Harness.Today, DueTime = new TimeOnly(11, 0), RepeatReminderMinutes = 10,
        });

        h.Clock.UtcNow = new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero); // 11:00
        await scheduler.FireDueAsync();
        Assert.Equal(h.Clock.UtcNow.AddMinutes(10), (await h.Tasks.GetRequiredAsync(t.Id)).NextReminderUtc);

        await h.Tasks.CompleteAsync(t.Id);
        Assert.Null((await h.Tasks.GetRequiredAsync(t.Id)).NextReminderUtc);
    }

    [Fact]
    public async Task Missed_reminders_are_flagged_and_grouped_when_many()
    {
        await using var h = await Harness.CreateAsync();
        var scheduler = h.CreateScheduler();
        for (var i = 0; i < 5; i++) await h.Tasks.QuickAddAsync($"task {i} today 11am");
        var single = await h.Tasks.QuickAddAsync("lonely today 2pm");

        // Laptop sleeps from 10:30 to 15:00
        h.Clock.UtcNow = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);
        Assert.Equal(6, await scheduler.FireDueAsync());

        var summary = await h.WaitForEventAsync(EventTypes.ReminderMissed);
        Assert.Equal(6, summary.DataAs<ReminderSummaryEvent>()!.Tasks.Count); // 6 > group_missed_above (3)
        Assert.Empty(h.EventsOf(EventTypes.ReminderDue));
        _ = single;
    }

    [Fact]
    public async Task A_few_missed_reminders_come_individually_marked_missed()
    {
        await using var h = await Harness.CreateAsync();
        var scheduler = h.CreateScheduler();
        await h.Tasks.QuickAddAsync("one today 11am");
        await h.Tasks.QuickAddAsync("two today 11:15am");

        h.Clock.UtcNow = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);
        await scheduler.FireDueAsync();
        await Task.Delay(100);

        var due = h.EventsOf(EventTypes.ReminderDue);
        Assert.Equal(2, due.Count);
        Assert.All(due, e => Assert.True(e.DataAs<ReminderEvent>()!.Missed));
        Assert.Empty(h.EventsOf(EventTypes.ReminderMissed));
    }

    [Fact]
    public async Task Snooze_postpones_and_fires_again()
    {
        await using var h = await Harness.CreateAsync();
        var scheduler = h.CreateScheduler();
        var t = await h.Tasks.QuickAddAsync("call bob today 11am");
        h.Clock.UtcNow = new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero);
        await scheduler.FireDueAsync();

        await h.Tasks.SnoozeAsync(t.Id, TimeSpan.FromMinutes(10));
        Assert.Equal(h.Clock.UtcNow.AddMinutes(10), (await h.Tasks.GetRequiredAsync(t.Id)).NextReminderUtc);
        await h.WaitForEventAsync(EventTypes.ReminderSnoozed);

        h.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, await scheduler.FireDueAsync());
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await scheduler.FireDueAsync());
    }

    [Fact]
    public async Task Snooze_rejects_closed_tasks_and_silly_durations()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("x today 11am");
        await Assert.ThrowsAsync<TaskValidationException>(() => h.Tasks.SnoozeAsync(t.Id, TimeSpan.Zero));
        await h.Tasks.CompleteAsync(t.Id);
        await Assert.ThrowsAsync<TaskValidationException>(() => h.Tasks.SnoozeAsync(t.Id, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Renaming_keeps_a_snooze_but_changing_due_replans()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("x today 11am");
        await h.Tasks.SnoozeAsync(t.Id, TimeSpan.FromHours(2));
        var snoozed = (await h.Tasks.GetRequiredAsync(t.Id)).NextReminderUtc;

        await h.Tasks.UpdateAsync(t.Id, new TaskPatch { Title = "renamed" });
        Assert.Equal(snoozed, (await h.Tasks.GetRequiredAsync(t.Id)).NextReminderUtc);

        await h.Tasks.UpdateAsync(t.Id, new TaskPatch { DueTime = new TimeOnly(16, 0) });
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 30, 0, TimeSpan.Zero), (await h.Tasks.GetRequiredAsync(t.Id)).NextReminderUtc);
    }

    [Fact]
    public async Task Deleting_cancels_and_restoring_replans()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("x today 11am");
        await h.Tasks.DeleteAsync(t.Id);
        Assert.Null(await h.Store.GetNextReminderTimeAsync());
        await h.Tasks.RestoreAsync(t.Id);
        Assert.NotNull(await h.Store.GetNextReminderTimeAsync());
    }

    [Fact]
    public async Task Next_reminder_time_is_the_earliest_open_one()
    {
        await using var h = await Harness.CreateAsync();
        await h.Tasks.QuickAddAsync("later today 4pm");
        await h.Tasks.QuickAddAsync("sooner today 11am");
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero), await h.Store.GetNextReminderTimeAsync());
    }
}

public class RecurringTaskTests
{
    [Fact]
    public async Task Completing_a_recurring_task_moves_it_to_the_next_occurrence()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("standup every weekday 11am");
        Assert.Equal(Harness.Today, t.DueDate); // 11:00 hasn't passed yet

        var done = await h.Tasks.CompleteAsync(t.Id);

        Assert.True(done.IsOpen);
        Assert.Equal(Harness.Today.AddDays(1), done.DueDate);
        Assert.Equal(new TimeOnly(11, 0), done.DueTime);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 5, 30, 0, TimeSpan.Zero), done.DueAtUtc);
        Assert.Equal(done.DueAtUtc, done.NextReminderUtc);
        await h.WaitForEventAsync(EventTypes.TaskCompleted);
    }

    [Fact]
    public async Task Undo_brings_the_occurrence_back()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("water plants every 3 days");
        await h.Tasks.CompleteAsync(t.Id);
        await h.Tasks.UndoAsync();
        Assert.Equal(Harness.Today, (await h.Tasks.GetRequiredAsync(t.Id)).DueDate);
    }

    [Fact]
    public async Task Finished_series_completes_normally()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.CreateAsync(new NewTask { Title = "twice", DueDate = Harness.Today, Recurrence = "FREQ=DAILY;COUNT=1" });
        var done = await h.Tasks.CompleteAsync(t.Id);
        Assert.Equal(TaskItemStatus.Completed, done.Status);
    }

    [Fact]
    public async Task Adding_a_repeat_to_an_undated_task_gives_it_a_start_date()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("journal");
        var updated = await h.Tasks.UpdateAsync(t.Id, new TaskPatch { Recurrence = "every friday" });
        Assert.Equal("FREQ=WEEKLY;BYDAY=FR", updated.Recurrence);
        Assert.Equal(new DateOnly(2026, 11, 6), updated.DueDate);
    }

    [Fact]
    public async Task Invalid_repeat_is_rejected()
    {
        await using var h = await Harness.CreateAsync();
        await Assert.ThrowsAsync<TaskValidationException>(() =>
            h.Tasks.CreateAsync(new NewTask { Title = "x", Recurrence = "whenever I feel like it" }));
    }

    [Fact]
    public async Task Repeat_and_reminders_survive_export_import()
    {
        await using var h = await Harness.CreateAsync();
        await h.Tasks.CreateAsync(new NewTask
        {
            Title = "rr", DueDate = Harness.Today.AddDays(1), DueTime = new TimeOnly(9, 0),
            Recurrence = "every day", ReminderOffsets = [0, 30], RepeatReminderMinutes = 5,
        });
        var json = await h.ImportExport.ExportJsonAsync();

        await using var target = await Harness.CreateAsync();
        await target.ImportExport.ImportJsonAsync(json);
        var t = (await target.Query()).Single();
        Assert.Equal("FREQ=DAILY", t.Recurrence);
        Assert.Equal([0, 30], t.ReminderOffsets);
        Assert.Equal(5, t.RepeatReminderMinutes);
        Assert.NotNull(t.NextReminderUtc);
    }
}

public class MaintenanceTests
{
    [Fact]
    public async Task Daily_backup_is_written_once_per_day_and_is_a_valid_database()
    {
        await using var h = await Harness.CreateAsync();
        await h.Tasks.QuickAddAsync("backed up");
        var m = h.CreateMaintenance();

        var path = m.BackupIfDue();
        Assert.NotNull(path);
        Assert.EndsWith("taskhub-2026-11-02.db", path, StringComparison.Ordinal);
        Assert.Null(m.BackupIfDue()); // already done today

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Title FROM tasks";
        Assert.Equal("backed up", await cmd.ExecuteScalarAsync());
        await h.WaitForEventAsync(EventTypes.BackupCreated);
    }

    [Fact]
    public async Task Old_daily_backups_are_pruned_but_other_files_kept()
    {
        await using var h = await Harness.CreateAsync();
        h.MaintenanceOptions.BackupKeepDays = 3;
        Directory.CreateDirectory(h.Paths.BackupsDir);
        for (var d = 1; d <= 6; d++) await File.WriteAllTextAsync(Path.Combine(h.Paths.BackupsDir, $"taskhub-2026-10-0{d}.db"), "x");
        await File.WriteAllTextAsync(Path.Combine(h.Paths.BackupsDir, "taskhub-pre-migration-20261001-000000.db"), "x");

        Assert.Equal(3, h.CreateMaintenance().PruneBackups());

        var left = Directory.GetFiles(h.Paths.BackupsDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["taskhub-2026-10-04.db", "taskhub-2026-10-05.db", "taskhub-2026-10-06.db", "taskhub-pre-migration-20261001-000000.db"], left);
    }

    [Fact]
    public async Task Run_once_purges_old_trash()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("old junk");
        await h.Tasks.DeleteAsync(t.Id);
        h.Clock.Advance(TimeSpan.FromDays(31));

        await h.CreateMaintenance().RunOnceAsync();

        Assert.Null(await h.Tasks.GetAsync(t.Id));
    }
}

public class ReminderNotifierTests
{
    private sealed class RecordingPresenter : TaskHub.Core.Notifications.INotificationPresenter
    {
        public List<TaskHub.Core.Notifications.NotificationRequest> Shown { get; } = [];
        public List<Guid> Dismissed { get; } = [];
        public void Show(TaskHub.Core.Notifications.NotificationRequest request) { lock (Shown) Shown.Add(request); }
        public void DismissForTasks(IReadOnlyCollection<Guid> taskIds) { lock (Dismissed) Dismissed.AddRange(taskIds); }
    }

    private static async Task<(Harness H, RecordingPresenter P, TaskHub.Core.Notifications.ReminderNotifier N)> Setup()
    {
        var h = await Harness.CreateAsync();
        var p = new RecordingPresenter();
        var n = new TaskHub.Core.Notifications.ReminderNotifier(h.Bus, p, h.Tasks,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskHub.Core.Notifications.ReminderNotifier>.Instance);
        await n.StartAsync(default);
        return (h, p, n);
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Reminder_event_becomes_a_banner_with_reply()
    {
        var (h, p, n) = await Setup();
        await using var _ = h;
        var t = await h.Tasks.QuickAddAsync("call bob today 11am !urgent");
        await h.CreateScheduler().FireDueAsync(); // nothing yet
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        await h.CreateScheduler().FireDueAsync();

        await Until(() => Task.FromResult(p.Shown.Count == 1));
        var banner = p.Shown[0];
        Assert.Equal("call bob", banner.Title);
        Assert.Equal(TaskHub.Core.Notifications.NotificationKind.Reminder, banner.Kind);
        Assert.Equal(TaskHub.Core.Notifications.NotificationPriority.Urgent, banner.Priority);
        Assert.Equal([t.Id], banner.TaskIds);
        await n.StopAsync(default);
    }

    [Fact]
    public async Task Typed_reschedule_moves_the_due_date()
    {
        var (h, _, n) = await Setup();
        await using var _h = h;
        var t = await h.Tasks.QuickAddAsync("call bob today 11am");

        h.Bus.Publish(EventTypes.NotificationAction, new NotificationActionEvent(Guid.NewGuid(), "reschedule:tomorrow 4pm", [t.Id]));

        await Until(async () => (await h.Tasks.GetRequiredAsync(t.Id)).DueDate == Harness.Today.AddDays(1));
        var moved = await h.Tasks.GetRequiredAsync(t.Id);
        Assert.Equal(new TimeOnly(16, 0), moved.DueTime);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 10, 30, 0, TimeSpan.Zero), moved.NextReminderUtc);
        await n.StopAsync(default);
    }

    [Fact]
    public async Task Done_and_custom_snooze_from_a_banner()
    {
        var (h, p, n) = await Setup();
        await using var _h = h;
        var a = await h.Tasks.QuickAddAsync("a today 11am");
        var b = await h.Tasks.QuickAddAsync("b today 11am");

        h.Bus.Publish(EventTypes.NotificationAction, new NotificationActionEvent(Guid.NewGuid(), "done", [a.Id]));
        h.Bus.Publish(EventTypes.NotificationAction, new NotificationActionEvent(Guid.NewGuid(), "snooze:45", [b.Id]));

        await Until(async () => !(await h.Tasks.GetRequiredAsync(a.Id)).IsOpen);
        await Until(async () => (await h.Tasks.GetRequiredAsync(b.Id)).NextReminderUtc == h.Clock.UtcNow.AddMinutes(45));
        await Until(() => Task.FromResult(p.Dismissed.Contains(a.Id))); // completed → its banner closes
        await n.StopAsync(default);
    }
}
