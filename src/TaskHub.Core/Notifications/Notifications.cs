using TaskHub.Abstractions.Events;

namespace TaskHub.Core.Notifications;

public enum NotificationPriority
{
    Low,
    Normal,
    High,
    Urgent,
}

public enum NotificationKind
{
    /// <summary>A task reminder (uses reminder_seconds).</summary>
    Reminder,
    /// <summary>Several reminders/notifications grouped into one.</summary>
    Summary,
    /// <summary>Anything else: API/scripts, app messages (uses info_seconds).</summary>
    Info,
}

public sealed record NotificationButton(string Action, string Label);

/// <summary>Something to show the user. Button clicks and replies come back as <c>notification.action</c>.</summary>
public sealed record NotificationRequest
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Title { get; init; }
    public string? Body { get; init; }
    /// <summary>Small line under the body, e.g. "Due 09:00 · Missed at 08:58".</summary>
    public string? Detail { get; init; }
    /// <summary>A Segoe Fluent Icons glyph (e.g. "") or a short emoji. Null = default for the kind.</summary>
    public string? Icon { get; init; }
    public NotificationKind Kind { get; init; } = NotificationKind.Info;
    public NotificationPriority Priority { get; init; } = NotificationPriority.Normal;
    public IReadOnlyList<Guid> TaskIds { get; init; } = [];
    public IReadOnlyList<NotificationButton> Buttons { get; init; } = [];
    /// <summary>Show a reply box ("snooze 10m", "done", "tomorrow 9am"). Defaults to on for reminders.</summary>
    public bool? AllowReply { get; init; }
    /// <summary>Overrides the configured duration. <see cref="TimeSpan.Zero"/> = stay until dismissed.</summary>
    public TimeSpan? Duration { get; init; }
    /// <summary>Overrides the configured sound setting.</summary>
    public bool? PlaySound { get; init; }
    public string Source { get; init; } = EventSources.Core;
}

/// <summary>Accepts notifications from anywhere in the app (reminders, API, plugins).</summary>
public interface INotificationPresenter
{
    void Show(NotificationRequest request);

    /// <summary>Closes visible and drops queued notifications that are only about these tasks.</summary>
    void DismissForTasks(IReadOnlyCollection<Guid> taskIds);
}

/// <summary>Do Not Disturb switch (implemented by <see cref="NotificationCenter"/>).</summary>
public interface IDoNotDisturb
{
    bool DoNotDisturb { get; }
    void SetDoNotDisturb(bool enabled);
}

public static class NotificationActions
{
    public const string Done = "done";
    /// <summary>Default length; <c>snooze:30</c> = 30 minutes.</summary>
    public const string Snooze = "snooze";
    public const string Open = "open";
    public const string Dismiss = "dismiss";
    /// <summary><c>reschedule:&lt;quick-add date text&gt;</c>, e.g. <c>reschedule:tomorrow 9am</c>.</summary>
    public const string ReschedulePrefix = "reschedule:";

    public static bool TryGetSnoozeMinutes(string action, int defaultMinutes, out int minutes)
    {
        minutes = defaultMinutes;
        if (action == Snooze) return true;
        return action.StartsWith(Snooze + ":", StringComparison.Ordinal)
               && int.TryParse(action.AsSpan(Snooze.Length + 1), System.Globalization.CultureInfo.InvariantCulture, out minutes)
               && minutes > 0;
    }
}

public enum BannerCloseReason
{
    /// <summary>The countdown ran out.</summary>
    Timeout,
    /// <summary>Closed by the user (✕, click, drag up).</summary>
    Dismissed,
    /// <summary>A button or reply was used.</summary>
    Action,
    /// <summary>Closed by the app (e.g. the task was completed elsewhere).</summary>
    Programmatic,
}

public enum AnimationPreset
{
    Spring,
    Slide,
    Drop,
    Fade,
}

/// <summary>How the surface should present one banner (resolved from options + request).</summary>
public sealed record BannerStyle(TimeSpan? Duration, AnimationPreset Animation, bool PlaySound, bool AllowReply);

public sealed class BannerActionEventArgs(Guid id, string action) : EventArgs
{
    public Guid Id { get; } = id;
    public string Action { get; } = action;
}

public sealed class BannerClosedEventArgs(Guid id, BannerCloseReason reason) : EventArgs
{
    public Guid Id { get; } = id;
    public BannerCloseReason Reason { get; } = reason;
}

/// <summary>
/// Renders banners (WPF in the app). Must be safe to call from any thread; raises its events
/// when the user acts or a banner goes away.
/// </summary>
public interface IBannerSurface
{
    void Show(NotificationRequest request, BannerStyle style);
    void Close(Guid id);
    event EventHandler<BannerActionEventArgs>? ActionInvoked;
    event EventHandler<BannerClosedEventArgs>? Closed;
}
