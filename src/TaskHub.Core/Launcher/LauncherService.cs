using System.Text.Json;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Automation;
using TaskHub.Core.Configuration;
using TaskHub.Core.Domain;
using TaskHub.Core.Hosting;
using TaskHub.Core.Notifications;
using TaskHub.Core.Services;

namespace TaskHub.Core.Launcher;

public enum LauncherKind
{
    Command,
    Rule,
    Shortcut,
    App,
    Task,
    AddTask,
}

/// <summary>One row in the quick launcher.</summary>
/// <param name="Key">Stable id used to remember how often it's picked.</param>
/// <param name="Icon">Segoe Fluent Icons glyph.</param>
public sealed record LauncherItem(string Key, string Title, string? Subtitle, string Icon, LauncherKind Kind, Func<CancellationToken, Task> Run)
{
    public int Score { get; init; }

    /// <summary>Ctrl+Enter: a second action (e.g. complete a task).</summary>
    public Func<CancellationToken, Task>? AltRun { get; init; }

    public string? AltHint { get; init; }
}

/// <summary>An app the launcher can start (from the Start menu).</summary>
public sealed record AppEntry(string Name, string Path, string? Folder);

public interface IAppCatalog
{
    Task<IReadOnlyList<AppEntry>> GetAppsAsync(CancellationToken ct);
}

/// <summary>A built-in launcher command (registered by the app: "Capture screenshot", "Do Not Disturb", …).</summary>
public sealed record LauncherCommand(string Key, string Title, string Keywords, string Icon, Func<CancellationToken, Task> Run);

/// <summary>
/// The quick launcher's brain: matches what you type against commands, rules, shortcuts, Start-menu
/// apps and open tasks; always offers "Add task: …"; and remembers what you pick so frequent items
/// rise to the top.
/// </summary>
public sealed class LauncherService
{
    private const string AddTaskKey = "add-task";
    private readonly TaskService _tasks;
    private readonly RuleEngine _rules;
    private readonly IShellLauncher _shell;
    private readonly IAppCatalog _apps;
    private readonly INotificationPresenter _presenter;
    private readonly ConfigStore _config;
    private readonly ILogger<LauncherService> _log;
    private readonly string? _historyFile;
    private readonly Lock _gate = new();
    private readonly List<LauncherCommand> _commands = [];
    private Dictionary<string, int> _usage;

    public LauncherService(TaskService tasks, RuleEngine rules, IShellLauncher shell, IAppCatalog apps, INotificationPresenter presenter,
        ConfigStore config, AppPaths paths, ILogger<LauncherService> log)
    {
        _tasks = tasks;
        _rules = rules;
        _shell = shell;
        _apps = apps;
        _presenter = presenter;
        _config = config;
        _log = log;
        _historyFile = Path.Combine(paths.DataDir, "launcher.json");
        _usage = LoadUsage(_historyFile);
    }

    /// <summary>Opens a task in the main window (set by the app).</summary>
    public Func<Guid, Task>? OpenTask { get; set; }

    public void AddCommand(LauncherCommand command)
    {
        lock (_gate) _commands.Add(command);
    }

    public async Task<IReadOnlyList<LauncherItem>> SearchAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        var options = _config.Current.Launcher;
        var candidates = new List<(LauncherItem Item, string Keywords)>();

        List<LauncherCommand> commands;
        lock (_gate) commands = [.. _commands];
        foreach (var c in commands)
            candidates.Add((new LauncherItem("cmd:" + c.Key, c.Title, null, c.Icon, LauncherKind.Command, c.Run), c.Keywords));

        foreach (var rule in _rules.Rules.Where(r => r.Error is null))
        {
            var name = rule.Name;
            candidates.Add((new LauncherItem("rule:" + name, name, "Run rule" + (rule.Enabled ? "" : " (disabled)"), "", LauncherKind.Rule,
                ct2 => RunRuleAsync(name, ct2)), "rule automation " + rule.TriggerText));
        }

        foreach (var s in _config.Current.GetAutomationConfig().Shortcuts)
        {
            var sc = s;
            candidates.Add((new LauncherItem("shortcut:" + sc.Name, sc.Name, sc.Target, "", LauncherKind.Shortcut,
                _ => OpenAsync(sc.Target, sc.Arguments)), sc.Keywords ?? ""));
        }

        if (options.IncludeStartMenu && query.Length > 0)
        {
            try
            {
                foreach (var app in await _apps.GetAppsAsync(ct).ConfigureAwait(false))
                {
                    var a = app;
                    candidates.Add((new LauncherItem("app:" + a.Path, a.Name, a.Folder ?? "App", "", LauncherKind.App,
                        _ => OpenAsync(a.Path, null)), ""));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not list Start menu apps");
            }
        }

        Dictionary<string, int> usage;
        lock (_gate) usage = _usage;

        var results = new List<LauncherItem>();
        foreach (var (item, keywords) in candidates)
        {
            var score = query.Length == 0 ? 1 : FuzzyMatcher.Score(query, item.Title, keywords);
            if (score <= 0) continue;
            var boost = usage.TryGetValue(item.Key, out var n) ? Math.Min(n, 10) * 15 : 0;
            if (query.Length == 0 && boost == 0 && item.Kind != LauncherKind.Command) continue;
            var kindBonus = item.Kind switch
            {
                LauncherKind.Command => 30,
                LauncherKind.Shortcut => 25,
                LauncherKind.Rule => 20,
                _ => 0,
            };
            results.Add(item with { Score = score + boost + kindBonus });
        }

        if (options.IncludeTasks && query.Length >= 2)
        {
            var tasks = await _tasks.QueryAsync(new TaskQuery { Text = query, Limit = 5 }, ct).ConfigureAwait(false);
            foreach (var t in tasks)
            {
                var id = t.Id;
                var title = t.Title;
                var due = t.DueDate is { } d ? $"Task · due {d:d MMM}{(t.DueTime is { } tm ? " " + tm.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture) : "")}" : "Task";
                results.Add(new LauncherItem("task:" + id, title, due, "", LauncherKind.Task, ct2 => OpenTaskAsync(id))
                {
                    Score = Math.Max(150, FuzzyMatcher.Score(query, title)) - 100,
                    AltRun = ct2 => CompleteTaskAsync(id, title, ct2),
                    AltHint = "Ctrl+Enter: done",
                });
            }
        }

        var max = Math.Max(3, options.MaxResults);
        var ordered = results.OrderByDescending(r => r.Score).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).Take(max - (query.Length > 0 ? 1 : 0)).ToList();
        if (query.Length > 0)
        {
            var add = new LauncherItem(AddTaskKey, $"Add task: {query}", "Enter to add · quick-add syntax works (tomorrow 9am #tag !high @List)", "",
                LauncherKind.AddTask, ct2 => AddTaskAsync(query, ct2));
            // Strong matches first; otherwise "Add task" leads.
            if (ordered.Count > 0 && ordered[0].Score >= 500) ordered.Add(add);
            else ordered.Insert(0, add);
        }
        return ordered;
    }

    /// <summary>Runs an item and remembers the pick.</summary>
    public async Task ExecuteAsync(LauncherItem item, bool alt = false, CancellationToken ct = default)
    {
        if (item.Kind != LauncherKind.AddTask && item.Kind != LauncherKind.Task) RecordUse(item.Key);
        try
        {
            await (alt && item.AltRun is not null ? item.AltRun(ct) : item.Run(ct)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AutomationException or TaskValidationException or TaskNotFoundException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _log.LogWarning(ex, "Launcher item {Key} failed", item.Key);
            _presenter.Show(new NotificationRequest
            {
                Title = $"Couldn't run “{item.Title}”",
                Body = ex.Message,
                Icon = "",
                Priority = NotificationPriority.High,
                Source = EventSources.Launcher,
            });
        }
    }

    private Task OpenAsync(string target, string? args)
    {
        _shell.Open(Environment.ExpandEnvironmentVariables(target), args);
        return Task.CompletedTask;
    }

    private async Task RunRuleAsync(string name, CancellationToken ct)
    {
        var run = await _rules.RunNowAsync(name, ct).ConfigureAwait(false);
        _presenter.Show(new NotificationRequest
        {
            Title = run.Ok ? $"Ran “{name}”" : $"“{name}” failed",
            Body = run.Message,
            Icon = run.Ok ? "" : "",
            Priority = run.Ok ? NotificationPriority.Low : NotificationPriority.High,
            Duration = TimeSpan.FromSeconds(5),
            PlaySound = false,
            Source = EventSources.Launcher,
        });
    }

    private async Task AddTaskAsync(string text, CancellationToken ct)
    {
        var task = await _tasks.QuickAddAsync(text, EventSources.Launcher, ct).ConfigureAwait(false);
        _presenter.Show(new NotificationRequest
        {
            Title = "Task added",
            Body = task.Title + (task.DueDate is { } d ? $" · {d:ddd d MMM}" : ""),
            Icon = "",
            TaskIds = [task.Id],
            Buttons = [new NotificationButton(NotificationActions.Open, "Open"), new NotificationButton(NotificationActions.Dismiss, "OK")],
            Duration = TimeSpan.FromSeconds(5),
            PlaySound = false,
            Source = EventSources.Launcher,
        });
    }

    private Task OpenTaskAsync(Guid id) => OpenTask?.Invoke(id) ?? Task.CompletedTask;

    private async Task CompleteTaskAsync(Guid id, string title, CancellationToken ct)
    {
        await _tasks.CompleteAsync(id, EventSources.Launcher, ct).ConfigureAwait(false);
        _presenter.Show(new NotificationRequest
        {
            Title = "Completed",
            Body = title,
            Icon = "",
            Duration = TimeSpan.FromSeconds(4),
            PlaySound = false,
            Source = EventSources.Launcher,
        });
    }

    // ------------------------------------------------------------------ usage history

    private void RecordUse(string key)
    {
        Dictionary<string, int> snapshot;
        lock (_gate)
        {
            var next = new Dictionary<string, int>(_usage, StringComparer.OrdinalIgnoreCase);
            next[key] = next.GetValueOrDefault(key) + 1;
            // Keep it small: drop the least-used entries.
            if (next.Count > 300)
                next = next.OrderByDescending(kv => kv.Value).Take(200).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            _usage = next;
            snapshot = next;
        }
        if (_historyFile is null) return;
        try
        {
            File.WriteAllText(_historyFile, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Could not save launcher history");
        }
    }

    private static Dictionary<string, int> LoadUsage(string? file)
    {
        try
        {
            if (file is not null && File.Exists(file))
                return new Dictionary<string, int>(
                    JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(file)) ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // start fresh
        }
        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }
}
