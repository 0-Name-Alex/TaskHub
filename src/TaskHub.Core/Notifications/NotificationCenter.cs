using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;

namespace TaskHub.Core.Notifications;

/// <summary>
/// The brain of the notification system; the WPF surface only draws. Decides whether a notification
/// is shown now, queued (stack full, queue mode, Do Not Disturb) or silenced; records history;
/// turns clicks and replies into <c>notification.action</c> events; and shows queued items as
/// space frees up. Thread-safe — callable from any thread.
/// </summary>
public sealed class NotificationCenter : INotificationPresenter, IDoNotDisturb, IDisposable
{
    private readonly IBannerSurface _surface;
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly Func<NotificationOptions> _options;
    private readonly ILogger<NotificationCenter> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, NotificationRequest> _visible = new();
    private readonly LinkedList<NotificationRequest> _queue = new();
    private bool _dnd;

    public NotificationCenter(IBannerSurface surface, IEventBus bus, IClock clock, NotificationHistory history,
        Func<NotificationOptions> options, ILogger<NotificationCenter> log)
    {
        _surface = surface;
        _bus = bus;
        _clock = clock;
        History = history;
        _options = options;
        _log = log;
        _surface.ActionInvoked += OnSurfaceAction;
        _surface.Closed += OnSurfaceClosed;
    }

    public NotificationHistory History { get; }

    public bool DoNotDisturb
    {
        get { lock (_gate) return _dnd; }
    }

    public int VisibleCount
    {
        get { lock (_gate) return _visible.Count; }
    }

    public int QueuedCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    public event EventHandler? DoNotDisturbChanged;

    public void Show(NotificationRequest request)
    {
        var o = _options();
        var toPresent = new List<NotificationRequest>();
        NotificationStatus status;

        lock (_gate)
        {
            if (_dnd && !BypassesDnd(request, o))
            {
                if (o.SilenceDuringDnd())
                {
                    status = NotificationStatus.Silenced;
                }
                else
                {
                    _queue.AddLast(request);
                    status = NotificationStatus.Queued;
                }
            }
            else if (_visible.Count < Capacity(o))
            {
                _visible[request.Id] = request;
                toPresent.Add(request);
                status = NotificationStatus.Shown;
            }
            else
            {
                // Urgent ones jump the queue.
                if (request.Priority == NotificationPriority.Urgent) _queue.AddFirst(request);
                else _queue.AddLast(request);
                status = NotificationStatus.Queued;
            }
        }

        History.Add(request, status, _clock.UtcNow);
        _log.LogDebug("Notification {Id} '{Title}': {Status}", request.Id, request.Title, status);
        Present(toPresent, o);
    }

    public void DismissForTasks(IReadOnlyCollection<Guid> taskIds)
    {
        if (taskIds.Count == 0) return;
        List<Guid> close;
        lock (_gate)
        {
            bool OnlyAbout(NotificationRequest r) => r.TaskIds.Count > 0 && r.TaskIds.All(taskIds.Contains);
            for (var node = _queue.First; node is not null;)
            {
                var next = node.Next;
                if (OnlyAbout(node.Value))
                {
                    History.Update(node.Value.Id, e => e with { Status = NotificationStatus.Closed, Outcome = "resolved elsewhere", ClosedAt = _clock.UtcNow });
                    _queue.Remove(node);
                }
                node = next;
            }
            close = _visible.Values.Where(OnlyAbout).Select(r => r.Id).ToList();
        }
        foreach (var id in close) _surface.Close(id); // → Closed event → OnSurfaceClosed
    }

    /// <summary>Turns Do Not Disturb on/off. Turning it off shows what was queued (grouped if many).</summary>
    public void SetDoNotDisturb(bool enabled)
    {
        var o = _options();
        List<NotificationRequest> released = [];
        lock (_gate)
        {
            if (_dnd == enabled) return;
            _dnd = enabled;
            if (!enabled && _queue.Count > o.GroupQueuedAbove && _queue.Count > 1)
            {
                var all = _queue.ToList();
                _queue.Clear();
                foreach (var r in all)
                    History.Update(r.Id, e => e with { Status = NotificationStatus.Closed, Outcome = "grouped", ClosedAt = _clock.UtcNow });
                released.Add(Summary(all));
            }
        }

        _log.LogInformation("Do Not Disturb {State}", enabled ? "on" : "off");
        _bus.Publish(EventTypes.DndChanged, new DndChangedEvent(enabled), EventSources.Ui);
        DoNotDisturbChanged?.Invoke(this, EventArgs.Empty);

        foreach (var r in released) Show(r);
        PumpQueue();
    }

    public void ToggleDoNotDisturb() => SetDoNotDisturb(!DoNotDisturb);

    /// <summary>Resolved per-banner style: duration by kind, preset, sound, reply box.</summary>
    public static BannerStyle StyleFor(NotificationRequest r, NotificationOptions o)
    {
        var seconds = r.Kind is NotificationKind.Reminder or NotificationKind.Summary ? o.ReminderSeconds : o.InfoSeconds;
        var duration = r.Duration ?? TimeSpan.FromSeconds(seconds);
        return new BannerStyle(
            duration > TimeSpan.Zero ? duration : null,
            o.GetAnimation(),
            r.PlaySound ?? o.Sound,
            r.AllowReply ?? (r.Kind == NotificationKind.Reminder && r.TaskIds.Count > 0));
    }

    private static int Capacity(NotificationOptions o) => o.IsQueueMode() ? 1 : Math.Max(1, o.MaxVisible);

    private static bool BypassesDnd(NotificationRequest r, NotificationOptions o) =>
        o.UrgentBypassesDnd && r.Priority == NotificationPriority.Urgent;

    private static NotificationRequest Summary(List<NotificationRequest> items) => new()
    {
        Title = $"{items.Count} notifications while Do Not Disturb was on",
        Body = string.Join("\n", items.Take(5).Select(r => "• " + (r.Title.Length > 60 ? r.Title[..59] + "…" : r.Title)))
               + (items.Count > 5 ? $"\n…and {items.Count - 5} more" : ""),
        Kind = NotificationKind.Summary,
        Priority = items.Max(r => r.Priority),
        TaskIds = items.SelectMany(r => r.TaskIds).Distinct().ToList(),
        Buttons = [new(NotificationActions.Open, "Review")],
        Icon = "",
    };

    private void Present(List<NotificationRequest> requests, NotificationOptions o)
    {
        foreach (var r in requests)
        {
            try
            {
                _surface.Show(r, StyleFor(r, o));
                _bus.Publish(EventTypes.NotificationShown, new NotificationShownEvent(r.Id, r.Title), EventSources.Ui);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not show notification {Id}", r.Id);
                lock (_gate) _visible.Remove(r.Id);
            }
        }
    }

    private void PumpQueue()
    {
        var o = _options();
        var toPresent = new List<NotificationRequest>();
        lock (_gate)
        {
            for (var node = _queue.First; node is not null && _visible.Count < Capacity(o);)
            {
                var next = node.Next;
                if (!_dnd || BypassesDnd(node.Value, o))
                {
                    _visible[node.Value.Id] = node.Value;
                    toPresent.Add(node.Value);
                    _queue.Remove(node);
                }
                node = next;
            }
        }
        foreach (var r in toPresent)
            History.Update(r.Id, e => e with { Status = NotificationStatus.Shown });
        Present(toPresent, o);
    }

    private void OnSurfaceAction(object? sender, BannerActionEventArgs e)
    {
        NotificationRequest? r;
        lock (_gate) _visible.TryGetValue(e.Id, out r);
        History.Update(e.Id, h => h with { Outcome = e.Action });
        _bus.Publish(EventTypes.NotificationAction, new NotificationActionEvent(e.Id, e.Action, r?.TaskIds ?? []), EventSources.Ui);
    }

    private void OnSurfaceClosed(object? sender, BannerClosedEventArgs e)
    {
        lock (_gate) _visible.Remove(e.Id);
        var reason = e.Reason.ToString().ToLowerInvariant();
        History.Update(e.Id, h => h with
        {
            Status = NotificationStatus.Closed,
            Outcome = h.Outcome ?? reason,
            ClosedAt = _clock.UtcNow,
        });
        _bus.Publish(EventTypes.NotificationClosed, new NotificationClosedEvent(e.Id, reason), EventSources.Ui);
        PumpQueue();
    }

    public void Dispose()
    {
        _surface.ActionInvoked -= OnSurfaceAction;
        _surface.Closed -= OnSurfaceClosed;
        History.SaveNow();
    }
}
