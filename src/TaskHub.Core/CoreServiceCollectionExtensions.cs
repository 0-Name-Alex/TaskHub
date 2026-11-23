using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Core.Automation;
using TaskHub.Core.Configuration;
using TaskHub.Core.Launcher;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;
using TaskHub.Core.Hotkeys;
using TaskHub.Core.Notifications;
using TaskHub.Core.Scheduling;
using TaskHub.Core.Services;

namespace TaskHub.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers the platform-neutral core: clock, event bus, config store, hotkey binder.</summary>
    public static IServiceCollection AddTaskHubCore(this IServiceCollection services, TaskHubOptions initialOptions)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());

        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ConfigStore>(sp, initialOptions));
        services.AddHostedService(sp => sp.GetRequiredService<ConfigStore>());

        services.AddSingleton<HotkeyBinder>();
        services.AddHostedService(sp => sp.GetRequiredService<HotkeyBinder>());

        services.AddHostedService<SingleInstanceListener>();

        // Application services (need an ITaskStore, registered by TaskHub.Storage)
        services.AddSingleton(sp => new ReminderPlanner(sp.GetRequiredService<IClock>(), () => sp.GetRequiredService<ConfigStore>().Current.Reminders));
        services.AddSingleton<RecurrenceEngine>();
        services.AddSingleton<UndoService>();
        services.AddSingleton<TaskService>();
        services.AddSingleton<ImportExportService>();
        return services;
    }

    /// <summary>
    /// Reminder scheduler + notifier. Register after storage (needs the database) and after an
    /// <see cref="INotificationPresenter"/>.
    /// </summary>
    public static IServiceCollection AddTaskHubReminders(this IServiceCollection services)
    {
        services.AddSingleton<ReminderScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<ReminderScheduler>());
        services.AddSingleton<ReminderNotifier>();
        services.AddHostedService(sp => sp.GetRequiredService<ReminderNotifier>());
        return services;
    }

    /// <summary>
    /// The notification center (DND, stack/queue, history). Needs an <see cref="IBannerSurface"/>
    /// to draw with; registers itself as the app's <see cref="INotificationPresenter"/>.
    /// </summary>
    public static IServiceCollection AddTaskHubNotifications(this IServiceCollection services, string? historyFile)
    {
        services.AddSingleton(_ => new NotificationHistory(historyFile));
        services.AddSingleton(sp => new NotificationCenter(
            sp.GetRequiredService<IBannerSurface>(),
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<NotificationHistory>(),
            () => sp.GetRequiredService<ConfigStore>().Current.Notifications,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NotificationCenter>>()));
        services.AddSingleton<INotificationPresenter>(sp => sp.GetRequiredService<NotificationCenter>());
        services.AddSingleton<IDoNotDisturb>(sp => sp.GetRequiredService<NotificationCenter>());
        return services;
    }

    /// <summary>
    /// Rules, actions, reminder actions, capture, folder watchers, system events and the quick launcher.
    /// Needs the platform services: <see cref="IShellLauncher"/>, <see cref="IClipboardService"/>,
    /// <see cref="IScreenCapture"/>, <see cref="ISystemStatus"/> and <see cref="IAppCatalog"/>.
    /// </summary>
    public static IServiceCollection AddTaskHubAutomation(this IServiceCollection services)
    {
        services.AddSingleton<Func<AutomationOptions>>(sp => () => sp.GetRequiredService<ConfigStore>().Current.Automation);
        services.AddSingleton<AutomationActivity>();

        services.AddSingleton<IAutomationAction, NotifyAction>();
        services.AddSingleton<IAutomationAction, TaskAddAction>();
        services.AddSingleton<IAutomationAction, TaskCompleteAction>();
        services.AddSingleton<IAutomationAction, TaskSnoozeAction>();
        services.AddSingleton<IAutomationAction, DndAction>();
        services.AddSingleton<IAutomationAction, LogAction>();
        services.AddSingleton<IAutomationAction, WaitAction>();
        services.AddSingleton<IAutomationAction, ClipboardSetAction>();
        services.AddSingleton<IAutomationAction, OpenAction>();
        services.AddSingleton<IAutomationAction, RunAction>();
        services.AddSingleton<IAutomationAction>(sp => new CaptureAction(ActionNames.CaptureScreenshot, sp.GetRequiredService<CaptureService>()));
        services.AddSingleton<IAutomationAction>(sp => new CaptureAction(ActionNames.CaptureClipboard, sp.GetRequiredService<CaptureService>()));
        services.AddSingleton(sp => new ActionDispatcher(
            sp.GetServices<IAutomationAction>(),
            sp.GetRequiredService<Func<AutomationOptions>>(),
            sp.GetRequiredService<ILogger<ActionDispatcher>>()));

        services.AddSingleton<CaptureService>();

        services.AddSingleton<RuleEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<RuleEngine>());
        services.AddSingleton<ReminderActionRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<ReminderActionRunner>());
        services.AddSingleton<FileWatchService>();
        services.AddHostedService(sp => sp.GetRequiredService<FileWatchService>());
        services.AddSingleton<SystemMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<SystemMonitor>());

        services.AddSingleton<LauncherService>();
        return services;
    }
}
