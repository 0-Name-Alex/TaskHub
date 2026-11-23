using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using TaskHub.Api;
using TaskHub.Automation;
using TaskHub.App.ViewModels;
using TaskHub.Core.Notifications;
using TaskHub.Notifications;
using TaskHub.Core;
using TaskHub.Core.Configuration;
using TaskHub.Core.Hosting;
using TaskHub.Platform.Windows;
using TaskHub.Storage;

namespace TaskHub.App;

/// <summary>The composition root: the only place services are wired together.</summary>
internal static class AppHost
{
    public static IHost Build(
        string[] args,
        AppPaths paths,
        TaskHubOptions options,
        IUiDispatcher ui,
        SingleInstanceGuard guard,
        LoggingLevelSwitch levelSwitch,
        StartupOptions startup,
        string version)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ApplicationName = AppPaths.AppName,
            ContentRootPath = AppContext.BaseDirectory,
            // No appsettings.json / env-var config: config.toml is the single source of truth.
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(dispose: false);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(startup);
        builder.Services.AddSingleton(levelSwitch);
        builder.Services.AddSingleton(ui);
        builder.Services.AddSingleton(guard);

        builder.Services.AddTaskHubCore(options);
        builder.Services.AddWindowsPlatform(paths);
        // Hosted services start in registration order: database before the API.
        builder.Services.AddTaskHubStorage(paths);
        builder.Services.AddSingleton<Func<MaintenanceOptions>>(sp => () => sp.GetRequiredService<ConfigStore>().Current.Maintenance);
        builder.Services.AddTaskHubMaintenance();
        builder.Services.AddSingleton(sp => new BannerSurface(
            sp.GetRequiredService<IUiDispatcher>(),
            () => sp.GetRequiredService<ConfigStore>().Current.Notifications,
            sp.GetRequiredService<ILogger<BannerSurface>>()));
        builder.Services.AddSingleton(sp => new IslandSurface(
            sp.GetRequiredService<IUiDispatcher>(),
            () => sp.GetRequiredService<ConfigStore>().Current.Notifications,
            sp.GetRequiredService<ILogger<IslandSurface>>()));
        builder.Services.AddSingleton<IStatusIndicator>(sp => sp.GetRequiredService<IslandSurface>());
        // [notifications] style picks island or banners for each notification (live).
        builder.Services.AddSingleton<IBannerSurface>(sp => new NotificationSurfaceRouter(
            sp.GetRequiredService<BannerSurface>(),
            sp.GetRequiredService<IslandSurface>(),
            () => sp.GetRequiredService<ConfigStore>().Current.Notifications));
        builder.Services.AddHostedService(sp => new DeviceIndicatorService(
            sp.GetRequiredService<Core.Events.IEventBus>(),
            sp.GetRequiredService<IStatusIndicator>(),
            () => sp.GetRequiredService<ConfigStore>().Current.Notifications,
            sp.GetRequiredService<ILogger<DeviceIndicatorService>>()));
        builder.Services.AddTaskHubNotifications(paths.NotificationHistoryFile);
        builder.Services.AddTaskHubReminders();
        builder.Services.AddWindowsAutomation();
        builder.Services.AddTaskHubAutomation();
        builder.Services.AddTaskHubApi(sp => new ApiServerOptions
        {
            Port = sp.GetRequiredService<ConfigStore>().Current.Api.Port,
            TokenFile = paths.ApiTokenFile,
            DiscoveryFile = paths.ApiDiscoveryFile,
            Version = version,
        });
        builder.Services.AddHostedService<LogLevelSync>();

        // UI
        builder.Services.AddSingleton<TasksViewModel>();
        builder.Services.AddSingleton<DiagnosticsViewModel>();
        builder.Services.AddSingleton<NotificationsViewModel>();
        builder.Services.AddSingleton<AutomationViewModel>();
        builder.Services.AddSingleton<LauncherWindow>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<TrayIconService>();

        return builder.Build();
    }
}
