using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskHub.Core.Hosting;
using TaskHub.Core.Hotkeys;
using TaskHub.Core.Platform;
using TaskHub.Platform.Windows.Autostart;
using TaskHub.Platform.Windows.Hotkeys;
using TaskHub.Platform.Windows.Power;
using TaskHub.Platform.Windows.SystemInfo;
using TaskHub.Core.Automation;

namespace TaskHub.Platform.Windows;

public static class WindowsPlatformServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton<WindowsHotkeyService>();
        services.AddSingleton<IHotkeyService>(sp => sp.GetRequiredService<WindowsHotkeyService>());

        // A dev instance (TASKHUB_HOME) gets its own Run entry so it can't clobber the real one.
        var valueName = paths.IsCustomHome ? $"{AppPaths.AppName} (dev)" : AppPaths.AppName;
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the executable path.");
        services.AddSingleton<IAutostartService>(sp => new RegistryAutostartService(
            valueName, exePath, sp.GetRequiredService<ILogger<RegistryAutostartService>>()));
        services.AddHostedService<PowerEventsService>();
        services.AddSingleton<ISystemStatus, WindowsSystemStatus>();
        services.AddHostedService<DeviceUsageMonitor>();
        return services;
    }
}
