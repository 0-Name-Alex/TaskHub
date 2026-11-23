using Microsoft.Extensions.DependencyInjection;
using TaskHub.Core.Automation;
using TaskHub.Core.Launcher;

namespace TaskHub.Automation;

public static class AutomationServiceCollectionExtensions
{
    /// <summary>The Windows services behind actions, capture and the launcher.</summary>
    public static IServiceCollection AddWindowsAutomation(this IServiceCollection services)
    {
        services.AddSingleton<IShellLauncher, ShellLauncher>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IScreenCapture, RegionScreenCapture>();
        services.AddSingleton<IAppCatalog, StartMenuCatalog>();
        return services;
    }
}
