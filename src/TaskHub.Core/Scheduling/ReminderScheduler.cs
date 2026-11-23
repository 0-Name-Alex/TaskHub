using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Domain;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;
using TaskHub.Core.Services;

namespace TaskHub.Core.Scheduling;

/// <summary>
/// Fires reminders. The database (indexed NextReminderUtc) is the queue: the loop sleeps until the
/// earliest reminder, a 30-second heartbeat, or a wake-up (any task change, resume from sleep,
/// clock change) — whichever comes first — then fires everything due.
/// <para>Catch-up: on start and on resume, everything that came due while the app was off or asleep
/// fires immediately, marked <c>Missed</c>. More than <c>group_missed_above</c> missed reminders are
/// published as a single <c>reminder.missed</c> summary instead of a flood of banners.</para>
/// </summary>
public sealed class ReminderScheduler : BackgroundService
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);
    /// <summary>A reminder older than this when fired counts as missed.</summary>
    public static readonly TimeSpan MissedGrace = TimeSpan.FromMinutes(2);

    private readonly ITaskStore _store;
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly ReminderPlanner _planner;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly ILogger<ReminderScheduler> _log;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _firing = new(1, 1);
    private IDisposable[] _subscriptions = [];

    public ReminderScheduler(ITaskStore store, IEventBus bus, IClock clock, ReminderPlanner planner,
        ILogger<ReminderScheduler> log, IHostApplicationLifetime? lifetime = null)
    {
        _store = store;
        _bus = bus;
        _clock = clock;
        _planner = planner;
        _log = log;
        _lifetime = lifetime;
    }

    /// <summary>Wake the loop now (re-check the next reminder).</summary>
    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't touch the database until every hosted service (incl. migrations) has started.
        if (_lifetime is not null)
        {
            var started = new TaskCompletionSource();
            using (_lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
            using (stoppingToken.Register(() => started.TrySetCanceled()))
            {
                try { await started.Task; }
                catch (TaskCanceledException) { return; }
            }
        }

        _subscriptions =
        [
            _bus.Subscribe("task.*", _ => Wake()),
            _bus.Subscribe("data.*", _ => Wake()),
            _bus.Subscribe(EventTypes.ReminderSnoozed, _ => Wake()),
            _bus.Subscribe("system.*", e =>
            {
                if (e.Type == EventTypes.SystemResumed) _log.LogInformation("Resumed from sleep; catching up on reminders");
                Wake();
            }),
        ];

        _log.LogInformation("Reminder scheduler started");
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FireDueAsync(stoppingToken);
                var delay = await DelayUntilNextAsync(stoppingToken);
                failures = 0;
                await _wake.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                _log.LogError(ex, "Reminder scheduler error (attempt {Failures})", failures);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 * failures)), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<TimeSpan> DelayUntilNextAsync(CancellationToken ct)
    {
        var next = await _store.GetNextReminderTimeAsync(ct);
        if (next is null) return Heartbeat;
        var wait = next.Value - _clock.UtcNow;
        if (wait < TimeSpan.Zero) return TimeSpan.Zero;
        return wait < Heartbeat ? wait : Heartbeat;
    }

    /// <summary>Fires every reminder that is due now. Returns how many tasks reminded.</summary>
    public async Task<int> FireDueAsync(CancellationToken ct = default)
    {
        await _firing.WaitAsync(ct);
        try
        {
            var now = _clock.UtcNow;
            var due = await _store.GetDueRemindersAsync(now, ct);
            if (due.Count == 0) return 0;

            var missed = due.Where(t => t.NextReminderUtc < now - MissedGrace).ToList();
            var onTime = due.Except(missed).ToList();

            foreach (var task in onTime)
                _bus.Publish(EventTypes.ReminderDue, new ReminderEvent(task.ToDto(), task.NextReminderUtc!.Value, Missed: false));

            if (missed.Count > _planner.Options.GroupMissedAbove)
            {
                _bus.Publish(EventTypes.ReminderMissed, new ReminderSummaryEvent(missed.Select(t => t.ToDto()).ToList()));
                _log.LogInformation("{Count} missed reminders grouped into one summary", missed.Count);
            }
            else
            {
                foreach (var task in missed)
                    _bus.Publish(EventTypes.ReminderDue, new ReminderEvent(task.ToDto(), task.NextReminderUtc!.Value, Missed: true));
            }

            // Advance each task to its next reminder (or none) — targeted update, no undo entry.
            foreach (var task in due)
            {
                var next = _planner.NextAfter(task, now);
                await _store.SetReminderStateAsync(task.Id, next, now, ct);
                _log.LogInformation("Reminded {TaskId} (scheduled {Scheduled:u}); next {Next}", task.Id, task.NextReminderUtc, next?.ToString("u") ?? "none");
            }
            return due.Count;
        }
        finally
        {
            _firing.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var s in _subscriptions) s.Dispose();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _wake.Dispose();
        _firing.Dispose();
        base.Dispose();
    }
}
