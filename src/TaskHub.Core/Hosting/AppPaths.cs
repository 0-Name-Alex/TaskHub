namespace TaskHub.Core.Hosting;

/// <summary>
/// Where TaskHub keeps its files. Config is roaming (%APPDATA%\TaskHub), everything else
/// is local (%LOCALAPPDATA%\TaskHub). Set TASKHUB_HOME to put both under one folder,
/// e.g. to run a separate dev instance alongside your real one.
/// </summary>
public sealed record AppPaths(string ConfigDir, string DataRoot)
{
    public const string AppName = "TaskHub";
    public const string HomeEnvironmentVariable = "TASKHUB_HOME";

    public string ConfigFile => Path.Combine(ConfigDir, "config.toml");
    public string DataDir => Path.Combine(DataRoot, "data");
    public string DatabaseFile => Path.Combine(DataDir, "taskhub.db");
    public string BackupsDir => Path.Combine(DataRoot, "backups");
    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string AttachmentsDir => Path.Combine(DataRoot, "attachments");
    public string PluginsDir => Path.Combine(DataRoot, "plugins");
    public string ApiDiscoveryFile => Path.Combine(DataRoot, "api.json");
    public string ApiTokenFile => Path.Combine(DataRoot, "api.token");
    public string NotificationHistoryFile => Path.Combine(DataDir, "notifications.json");

    /// <summary>True when running from a TASKHUB_HOME override (dev/test instance).</summary>
    public bool IsCustomHome { get; init; }

    public static AppPaths Resolve()
    {
        var custom = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var root = Path.GetFullPath(custom);
            return new AppPaths(Path.Combine(root, "config"), root) { IsCustomHome = true };
        }

        return new AppPaths(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));
    }

    public static AppPaths UnderRoot(string root) =>
        new(Path.Combine(root, "config"), root) { IsCustomHome = true };

    public void EnsureCreated()
    {
        foreach (var dir in new[] { ConfigDir, DataDir, BackupsDir, LogsDir, AttachmentsDir, PluginsDir })
            Directory.CreateDirectory(dir);
    }
}
