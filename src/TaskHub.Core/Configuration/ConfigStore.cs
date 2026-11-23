using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;

namespace TaskHub.Core.Configuration;

/// <summary>
/// Holds the current config and hot-reloads it when config.toml changes.
/// Publishes <c>config.changed</c> on success and <c>config.invalid</c> (keeping the
/// previous config) when the edited file doesn't parse.
/// </summary>
public sealed class ConfigStore : IHostedService, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly AppPaths _paths;
    private readonly IEventBus _bus;
    private readonly ILogger<ConfigStore> _log;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private TaskHubOptions _current;

    public ConfigStore(AppPaths paths, IEventBus bus, ILogger<ConfigStore> log, TaskHubOptions initial)
    {
        _paths = paths;
        _bus = bus;
        _log = log;
        _current = initial;
    }

    public TaskHubOptions Current => Volatile.Read(ref _current);
    public string FilePath => _paths.ConfigFile;

    /// <summary>Re-reads the file now. Returns false (and keeps the old config) if it is invalid.</summary>
    public bool Reload()
    {
        lock (_gate)
        {
            string text;
            try
            {
                text = ReadWithRetry(_paths.ConfigFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not read {Path}; keeping previous config", _paths.ConfigFile);
                return false;
            }

            try
            {
                var next = ConfigLoader.Parse(text, _paths.ConfigFile);
                Volatile.Write(ref _current, next);
                _log.LogInformation("Config reloaded from {Path}", _paths.ConfigFile);
                _bus.Publish(EventTypes.ConfigChanged);
                return true;
            }
            catch (ConfigException ex)
            {
                _log.LogWarning("Config file is invalid, keeping previous config: {Error}", ex.Message);
                _bus.Publish(EventTypes.ConfigInvalid, new ConfigInvalidEvent(_paths.ConfigFile, ex.Message));
                return false;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_paths.ConfigFile)!;
        _debounceTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(_paths.ConfigFile))
        {
            // Editors save in different ways (write in place, or write temp + rename).
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
        _log.LogDebug("Watching {Path} for changes", _paths.ConfigFile);
        return Task.CompletedTask;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) =>
        _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher is not null) _watcher.EnableRaisingEvents = false;
        return Task.CompletedTask;
    }

    private static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ConfigLoader.ReadShared(path);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}
