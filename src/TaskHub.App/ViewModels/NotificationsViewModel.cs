using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Hosting;
using TaskHub.Core.Notifications;

namespace TaskHub.App.ViewModels;

public sealed record HistoryRow(string Time, string Title, string? Body, string Status, string? Outcome, string Icon, bool Missed);

/// <summary>
/// The Notifications tab: history (E), Do Not Disturb (C), and test buttons for trying the banner
/// styles, priorities and animation presets (A, F).
/// </summary>
public sealed partial class NotificationsViewModel : ObservableObject
{
    private readonly NotificationCenter _center;
    private readonly ConfigStore _config;
    private readonly IUiDispatcher _ui;
    private readonly IStatusIndicator _indicator;

    public NotificationsViewModel(NotificationCenter center, ConfigStore config, IUiDispatcher ui, IStatusIndicator indicator)
    {
        _center = center;
        _config = config;
        _ui = ui;
        _indicator = indicator;
        _center.History.Changed += (_, _) => _ui.Post(Refresh);
        _center.DoNotDisturbChanged += (_, _) => _ui.Post(() =>
        {
            _syncing = true;
            DoNotDisturb = _center.DoNotDisturb;
            _syncing = false;
            OnPropertyChanged(nameof(DndStatus));
        });
        Refresh();
    }

    private bool _syncing;

    public ObservableCollection<HistoryRow> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DndStatus))]
    private bool _doNotDisturb;

    public string DndStatus => DoNotDisturb
        ? (_config.Current.Notifications.SilenceDuringDnd()
            ? "On — notifications are silenced (they still appear in the history below)."
            : $"On — notifications are held and shown when you turn it off ({_center.QueuedCount} waiting).")
        : "Off";

    public string SettingsSummary
    {
        get
        {
            var o = _config.Current.Notifications;
            if (o.UseIsland())
                return $"Style: island · Reminders stay {(o.ReminderSeconds == 0 ? "until dismissed" : o.ReminderSeconds + " s")} · " +
                       $"Sound: {(o.Sound ? "on" : "off")} · Camera/mic indicator: {(o.CameraMicIndicator ? "on" : "off")}. " +
                       "Change these in the config file under [notifications] (style = \"banner\" for the classic cards).";
            return $"Animation: {o.Animation} · Mode: {o.Mode} (max {o.MaxVisible}) · Reminders stay {(o.ReminderSeconds == 0 ? "until dismissed" : o.ReminderSeconds + " s")} · " +
                   $"Sound: {(o.Sound ? "on" : "off")} · Backdrop: {o.Backdrop}. Change these in the config file under [notifications].";
        }
    }

    partial void OnDoNotDisturbChanged(bool value)
    {
        if (!_syncing) _center.SetDoNotDisturb(value);
    }

    private void Refresh()
    {
        History.Clear();
        var culture = CultureInfo.CurrentCulture;
        foreach (var e in _center.History.Snapshot())
        {
            var local = e.At.ToLocalTime();
            var time = local.Date == DateTime.Today ? local.ToString("t", culture) : local.ToString("ddd d MMM, t", culture);
            var status = e.Status switch
            {
                NotificationStatus.Queued => "Waiting (Do Not Disturb)",
                NotificationStatus.Silenced => "Silenced",
                NotificationStatus.Shown => "On screen",
                _ => "",
            };
            var outcome = e.Outcome switch
            {
                null => null,
                NotificationActions.Done => "Marked done",
                NotificationActions.Open => "Opened",
                NotificationActions.Dismiss or "dismissed" => "Dismissed",
                "timeout" => "Timed out",
                "action" => null,
                var s when s.StartsWith(NotificationActions.Snooze, StringComparison.Ordinal) =>
                    NotificationActions.TryGetSnoozeMinutes(s, _config.Current.Reminders.SnoozeMinutes, out var m) ? $"Snoozed {m} min" : "Snoozed",
                var s when s.StartsWith(NotificationActions.ReschedulePrefix, StringComparison.Ordinal) =>
                    "Rescheduled: " + s[NotificationActions.ReschedulePrefix.Length..],
                var s => s,
            };
            var icon = e.Kind switch
            {
                NotificationKind.Reminder => "",
                NotificationKind.Summary => "",
                _ => "",
            };
            History.Add(new HistoryRow(time, e.Title, e.Body, status, outcome, icon, e.Priority >= NotificationPriority.High));
        }
        OnPropertyChanged(nameof(DndStatus));
        OnPropertyChanged(nameof(SettingsSummary));
    }

    [RelayCommand]
    private void ClearHistory() => _center.History.Clear();

    [RelayCommand]
    private void TestIndicator(string? kind)
    {
        var camera = kind != "mic";
        _indicator.Show(new IndicatorRequest(
            "test:" + (camera ? "camera" : "mic"),
            camera ? IndicatorKind.Camera : IndicatorKind.Microphone,
            camera ? "Test app is using your camera" : "Test app is using your microphone",
            TimeSpan.FromSeconds(_config.Current.Notifications.IndicatorSeconds),
            KeepDot: false));
    }

    [RelayCommand]
    private void TestNotification(string? kind)
    {
        var (title, body, priority, icon) = kind switch
        {
            "urgent" => ("Server is down!", "Urgent banners get a red accent and a stronger bounce.", NotificationPriority.Urgent, ""),
            "high" => ("Pay rent today", "High-priority banners get an orange accent.", NotificationPriority.High, ""),
            _ => ("Hello from TaskHub", "This is how banners look. Hover to pause, drag up or click to dismiss.", NotificationPriority.Normal, ""),
        };
        _center.Show(new NotificationRequest
        {
            Title = title,
            Body = body,
            Detail = $"Test · {_config.Current.Notifications.Animation} animation",
            Icon = icon,
            Priority = priority,
            Buttons = [new(NotificationActions.Dismiss, "Got it")],
            AllowReply = kind == "reply",
            Source = EventSources.Ui,
        });
    }
}
