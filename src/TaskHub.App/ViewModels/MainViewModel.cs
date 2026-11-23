namespace TaskHub.App.ViewModels;

/// <summary>Main window: tasks, notification history, automation, diagnostics.</summary>
public sealed class MainViewModel(TasksViewModel tasks, NotificationsViewModel notifications, AutomationViewModel automation, DiagnosticsViewModel diagnostics)
{
    public TasksViewModel Tasks { get; } = tasks;
    public NotificationsViewModel Notifications { get; } = notifications;
    public AutomationViewModel Automation { get; } = automation;
    public DiagnosticsViewModel Diagnostics { get; } = diagnostics;
}
