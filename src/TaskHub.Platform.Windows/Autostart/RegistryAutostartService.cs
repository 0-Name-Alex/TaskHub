using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TaskHub.Core.Platform;

namespace TaskHub.Platform.Windows.Autostart;

/// <summary>
/// Autostart via HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Per-user, no admin
/// rights needed. The app is started with <c>--background</c> so it goes straight to the tray.
/// </summary>
public sealed class RegistryAutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string BackgroundArgument = "--background";

    private readonly string _valueName;
    private readonly string _exePath;
    private readonly ILogger<RegistryAutostartService> _log;

    public RegistryAutostartService(string valueName, string exePath, ILogger<RegistryAutostartService> log)
    {
        _valueName = valueName;
        _exePath = exePath;
        _log = log;
    }

    public string Command => $"\"{_exePath}\" {BackgroundArgument}";

    /// <summary>Enabled only if the Run entry points at THIS exe (a moved exe counts as disabled).</summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(_valueName) is string value
                && string.Equals(value.Trim(), Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(_valueName, Command, RegistryValueKind.String);
            _log.LogInformation("Autostart enabled: {Command}", Command);
        }
        else
        {
            key.DeleteValue(_valueName, throwOnMissingValue: false);
            _log.LogInformation("Autostart disabled");
        }
    }
}
