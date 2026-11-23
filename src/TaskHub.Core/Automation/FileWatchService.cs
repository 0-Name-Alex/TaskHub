using System.Collections.Concurrent;
using System.IO.Enumeration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Events;

namespace TaskHub.Core.Automation;

public sealed record WatchStatus(string Name, string Path, bool Active, string? Error);

/// <summary>
/// Watches the <c>[[watch]]</c> folders and publishes <c>file.detected</c>. Events are debounced (a file
/// that is still being written produces one event, once it can be opened), and temporary download
/// files are ignored — a browser renaming <c>report.pdf.crdownload</c> to <c>report.pdf</c> counts as
/// <c>created</c>. Re-reads the folders whenever config.toml is saved.
/// </summary>
public sealed class FileWatchService : IHostedService, IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);
    private static readonly string[] TempExtensions = [".tmp", ".crdownload", ".part", ".partial", ".download", ".opdownload", ".!ut", ".temp"];

    private readonly ConfigStore _config;
    private readonly IEventBus _bus;
    private readonly ILogger<FileWatchService> _log;
    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WatchStatus> _status = [];
    private string _appliedKey = "";
    private IDisposable? _subscription;
    private bool _stopped;

    public FileWatchService(ConfigStore config, IEventBus bus, ILogger<FileWatchService> log)
    {
        _config = config;
        _bus = bus;
        _log = log;
    }

    public IReadOnlyList<WatchStatus> Status => _status;

    public event EventHandler? StatusChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply(_config.Current.GetAutomationConfig().Watches);
        _subscription = _bus.Subscribe(EventTypes.ConfigChanged, _ => Apply(_config.Current.GetAutomationConfig().Watches));
        return Task.CompletedTask;
    }

    /// <summary>(Re)creates the watchers if the [[watch]] entries changed.</summary>
    public void Apply(IReadOnlyList<WatchDefinition> watches)
    {
        var key = string.Join("\n", watches.Select(w =>
            $"{w.Name}|{w.Path}|{string.Join(',', w.Filters)}|{w.Subfolders}|{string.Join(',', w.Events)}|{w.Error}"));
        lock (_gate)
        {
            if (_stopped || key == _appliedKey) return;
            _appliedKey = key;
            DisposeWatchers();

            var status = new List<WatchStatus>();
            foreach (var def in watches)
            {
                var path = ExpandPath(def.Path);
                if (def.Error is not null)
                {
                    status.Add(new WatchStatus(def.Name, path, false, def.Error));
                    continue;
                }
                if (!Directory.Exists(path))
                {
                    status.Add(new WatchStatus(def.Name, path, false, "Folder not found."));
                    _log.LogWarning("Watch '{Name}': folder {Path} not found", def.Name, path);
                    continue;
                }
                try
                {
                    var watcher = new FileSystemWatcher(path)
                    {
                        IncludeSubdirectories = def.Subfolders,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    var d = def;
                    watcher.Created += (_, e) => OnChange(d, "created", e.FullPath, null);
                    watcher.Changed += (_, e) => OnChange(d, "changed", e.FullPath, null);
                    watcher.Deleted += (_, e) => OnChange(d, "deleted", e.FullPath, null);
                    watcher.Renamed += (_, e) => OnChange(d, "renamed", e.FullPath, e.OldFullPath);
                    watcher.Error += (_, e) => _log.LogWarning(e.GetException(), "Watch '{Name}': watcher error (some changes may have been missed)", d.Name);
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                    status.Add(new WatchStatus(def.Name, path, true, null));
                    _log.LogInformation("Watching {Path} as '{Name}' ({Events})", path, def.Name, string.Join(", ", def.Events));
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    status.Add(new WatchStatus(def.Name, path, false, ex.Message));
                    _log.LogWarning(ex, "Watch '{Name}': can't watch {Path}", def.Name, path);
                }
            }
            _status = status;
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string ExpandPath(string path)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim());
        if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 2 ? p[2..] : "");
        try
        {
            return Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return p;
        }
    }

    public static bool IsTemporary(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal)
        || name.StartsWith(".~", StringComparison.Ordinal)
        || TempExtensions.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    public static bool MatchesFilters(WatchDefinition def, string name) =>
        def.Filters.Count == 0 || def.Filters.Any(f => FileSystemName.MatchesSimpleExpression(f, name, ignoreCase: true));

    private void OnChange(WatchDefinition def, string change, string fullPath, string? oldPath)
    {
        var name = Path.GetFileName(fullPath);
        if (IsTemporary(name)) return;

        // A browser finishing a download renames "x.pdf.crdownload" → "x.pdf": that's a new file.
        if (change == "renamed" && oldPath is not null && IsTemporary(Path.GetFileName(oldPath)))
        {
            change = "created";
            oldPath = null;
        }
        // Editors and downloads write in bursts; report a new file once, as created.
        var key = def.Name + "|" + fullPath;
        var pending = _pending.AddOrUpdate(key,
            _ => new Pending(def, change, fullPath, oldPath),
            (_, existing) =>
            {
                existing.Restart(change == "changed" && existing.Change is "created" or "renamed" ? existing.Change : change, oldPath ?? existing.OldPath);
                return existing;
            });
        pending.Arm(Debounce, () => _ = FireAsync(key));
    }

    private async Task FireAsync(string key)
    {
        if (!_pending.TryRemove(key, out var p)) return;
        var def = p.Watch;
        var change = p.Change;
        if (!def.Events.Contains(change)) return;
        var name = Path.GetFileName(p.Path);
        if (!MatchesFilters(def, name)) return;

        long? size = null;
        if (change != "deleted")
        {
            if (Directory.Exists(p.Path)) return; // folders aren't reported
            size = await WaitUntilReadableAsync(p.Path).ConfigureAwait(false);
            if (size is null) return; // gone again (temp file) or never became readable
        }

        var evt = new FileDetectedEvent(
            def.Name, change, p.Path, name, Path.GetExtension(name).ToLowerInvariant(),
            Path.GetDirectoryName(p.Path) ?? "", size, p.OldPath);
        _log.LogInformation("Watch '{Name}': {Change} {Path}", def.Name, change, p.Path);
        _bus.Publish(EventTypes.FileDetected, evt, EventSources.Watcher);
    }

    /// <summary>Waits (up to ~30 s) until the file can be opened, i.e. nobody is still writing it. Returns its size.</summary>
    private static async Task<long?> WaitUntilReadableAsync(string path)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return stream.Length;
            }
            catch (IOException)
            {
                await Task.Delay(1000).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                return new FileInfo(path).Length;
            }
        }
        return null;
    }

    private void DisposeWatchers()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        lock (_gate)
        {
            _stopped = true;
            DisposeWatchers();
        }
        foreach (var p in _pending.Values) p.Dispose();
        _pending.Clear();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        lock (_gate) DisposeWatchers();
        foreach (var p in _pending.Values) p.Dispose();
    }

    private sealed class Pending(WatchDefinition watch, string change, string path, string? oldPath) : IDisposable
    {
        private Timer? _timer;

        public WatchDefinition Watch { get; } = watch;
        public string Path { get; } = path;
        public string Change { get; private set; } = change;
        public string? OldPath { get; private set; } = oldPath;

        public void Restart(string change, string? oldPath)
        {
            Change = change;
            OldPath = oldPath;
        }

        public void Arm(TimeSpan delay, Action callback)
        {
            lock (this)
            {
                _timer ??= new Timer(_ => callback(), null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            lock (this) _timer?.Dispose();
        }
    }
}
