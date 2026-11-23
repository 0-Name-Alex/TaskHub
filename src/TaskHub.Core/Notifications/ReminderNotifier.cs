using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Contracts;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Events;
using TaskHub.Core.Services;

namespace TaskHub.Core.Notifications;

/// <summary>
/// Turns reminder events into notifications and carries out what the user does with them —
/// Done, Snooze (any length), or a typed new due time ("tomorrow 9am") — via <see cref="TaskService"/>.
/// "Open" is handled by the UI.
/// </summary>
public sealed class ReminderNotifier(IEventBus bus, INotificationPresenter presenter, TaskService tasks, ILogger<ReminderNotifier> log) : IHostedService
{
    public const string BellIcon = "";
    private IDisposable[] _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions =
        [
            bus.Subscribe(EventTypes.ReminderDue, e => { if (e.DataAs<ReminderEvent>() is { } r) presenter.Show(ForReminder(r)); }),
            bus.Subscribe(EventTypes.ReminderMissed, e => { if (e.DataAs<ReminderSummaryEvent>() is { } s) presenter.Show(ForSummary(s)); }),
            bus.Subscribe(EventTypes.NotificationAction, HandleActionAsync),
            // A task completed/deleted/rescheduled elsewhere shouldn't leave its banner up.
            bus.Subscribe("task.*", e =>
            {
                if (e.Type is EventTypes.TaskCompleted or EventTypes.TaskDeleted && e.DataAs<TaskChangedEvent>() is { } c)
                    presenter.DismissForTasks([c.Task.Id]);
            }),
        ];
        return Task.CompletedTask;
    }

    public static NotificationRequest ForReminder(ReminderEvent r)
    {
        var t = r.Task;
        var due = t.DueTime is not null ? $"Due {t.DueTime}" : t.DueDate is not null ? "Due today" : null;
        var detail = string.Join(" · ", new[] { due, r.Missed ? $"Missed at {r.ScheduledUtc.ToLocalTime():t}" : null, t.RecurrenceText }
            .Where(x => !string.IsNullOrEmpty(x)));
        return new NotificationRequest
        {
            Title = t.Title,
            Body = string.IsNullOrWhiteSpace(t.Notes) ? null : Trim(t.Notes, 140),
            Detail = detail.Length == 0 ? null : detail,
            Icon = BellIcon,
            Kind = NotificationKind.Reminder,
            Priority = t.Priority switch
            {
                PriorityNames.Urgent => NotificationPriority.Urgent,
                PriorityNames.High => NotificationPriority.High,
                _ => NotificationPriority.Normal,
            },
            TaskIds = [t.Id],
            Buttons = [new(NotificationActions.Done, "Done"), new(NotificationActions.Snooze, "Snooze"), new(NotificationActions.Open, "Open")],
            Source = "reminder",
        };
    }

    public static NotificationRequest ForSummary(ReminderSummaryEvent s) => new()
    {
        Title = $"{s.Tasks.Count} missed reminders",
        Body = string.Join("\n", s.Tasks.Take(5).Select(t => "• " + Trim(t.Title, 60))) + (s.Tasks.Count > 5 ? $"\n…and {s.Tasks.Count - 5} more" : ""),
        Detail = "While your laptop was asleep or TaskHub wasn't running",
        Icon = "",
        Kind = NotificationKind.Summary,
        Priority = NotificationPriority.High,
        TaskIds = s.Tasks.Select(t => t.Id).ToList(),
        Buttons = [new(NotificationActions.Open, "Review"), new(NotificationActions.Snooze, "Snooze all")],
        Source = "reminder",
    };

    private async ValueTask HandleActionAsync(EventEnvelope e, CancellationToken ct)
    {
        if (e.DataAs<NotificationActionEvent>() is not { } a || a.TaskIds.Count == 0) return;
        try
        {
            if (a.Action == NotificationActions.Done)
            {
                foreach (var id in a.TaskIds) await tasks.CompleteAsync(id, "notification", ct);
            }
            else if (NotificationActions.TryGetSnoozeMinutes(a.Action, tasks.Reminders.Options.SnoozeMinutes, out var minutes))
            {
                foreach (var id in a.TaskIds) await tasks.SnoozeAsync(id, TimeSpan.FromMinutes(minutes), "notification", ct);
            }
            else if (a.Action.StartsWith(NotificationActions.ReschedulePrefix, StringComparison.Ordinal))
            {
                var parsed = tasks.Preview(a.Action[NotificationActions.ReschedulePrefix.Length..]);
                if (parsed.DueDate is null) return;
                foreach (var id in a.TaskIds)
                    await tasks.UpdateAsync(id, new TaskPatch { DueDate = parsed.DueDate, DueTime = parsed.DueTime }, "notification", ct);
                log.LogInformation("Rescheduled {Count} task(s) to {Date} {Time} from a notification", a.TaskIds.Count, parsed.DueDate, parsed.DueTime);
            }
        }
        catch (Exception ex) when (ex is TaskValidationException or TaskNotFoundException)
        {
            log.LogInformation("Notification action {Action} skipped: {Reason}", a.Action, ex.Message);
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var s in _subscriptions) s.Dispose();
        return Task.CompletedTask;
    }
}
