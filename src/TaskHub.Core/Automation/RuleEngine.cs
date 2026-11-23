using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Contracts;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;

namespace TaskHub.Core.Automation;

/// <param name="Rule">Rule name, or the task title for a task's reminder actions.</param>
/// <param name="Trigger">Event type, "hotkey" or "manual".</param>
public sealed record RuleRun(DateTimeOffset At, string Rule, string Trigger, bool Ok, string Message);

/// <summary>Recent rule and reminder-action runs, newest first (shown in the Automation tab).</summary>
public sealed class AutomationActivity
{
    public const int Capacity = 200;
    private readonly LinkedList<RuleRun> _runs = new();
    private readonly Lock _gate = new();

    public event EventHandler? Changed;

    public void Add(RuleRun run)
    {
        lock (_gate)
        {
            _runs.AddFirst(run);
            while (_runs.Count > Capacity) _runs.RemoveLast();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<RuleRun> Snapshot()
    {
        lock (_gate) return [.. _runs];
    }

    public RuleRun? LastFor(string rule)
    {
        lock (_gate) return _runs.FirstOrDefault(r => string.Equals(r.Rule, rule, StringComparison.OrdinalIgnoreCase));
    }

    public void Clear()
    {
        lock (_gate) _runs.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Runs <c>[[rules]]</c>: for every event on the bus, each enabled rule whose <c>when</c> matches and
/// whose <c>if</c> holds runs its <c>then</c> actions in order (stopping at the first failure).
/// <list type="bullet">
/// <item>Rules never react to events caused by rules (source <c>rule:…</c>), so they can't loop.</item>
/// <item>A rule doesn't start again while it's still running, or within its <c>cooldown</c>.</item>
/// <item>At most <c>max_runs_per_minute</c> runs in total, against runaway triggers.</item>
/// </list>
/// Rules are re-read whenever config.toml is saved.
/// </summary>
public sealed class RuleEngine : IHostedService, IDisposable
{
    private readonly ConfigStore _config;
    private readonly IEventBus _bus;
    private readonly ActionDispatcher _actions;
    private readonly IClock _clock;
    private readonly ILogger<RuleEngine> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastStart = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _recentStarts = new();
    private readonly HashSet<Task> _inFlight = [];
    private readonly CancellationTokenSource _stopping = new();
    private IDisposable? _subscription;

    public RuleEngine(ConfigStore config, IEventBus bus, ActionDispatcher actions, AutomationActivity activity, IClock clock, ILogger<RuleEngine> log)
    {
        _config = config;
        _bus = bus;
        _actions = actions;
        Activity = activity;
        _clock = clock;
        _log = log;
    }

    public AutomationActivity Activity { get; }

    public IReadOnlyList<RuleDefinition> Rules => _config.Current.GetAutomationConfig().Rules;

    public bool Enabled => _config.Current.Automation.RulesEnabled;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe("*", OnEventAsync);
        var rules = Rules;
        _log.LogInformation("Rule engine started with {Count} rule(s){Disabled}", rules.Count, Enabled ? "" : " (rules are switched off)");
        foreach (var r in rules.Where(r => r.Error is not null))
            _log.LogWarning("Rule '{Rule}' has a problem and won't run: {Error}", r.Name, r.Error);
        return Task.CompletedTask;
    }

    private ValueTask OnEventAsync(EventEnvelope e, CancellationToken ct)
    {
        if (e.Type == EventTypes.ConfigChanged)
        {
            foreach (var r in Rules.Where(r => r.Error is not null))
                _log.LogWarning("Rule '{Rule}' has a problem and won't run: {Error}", r.Name, r.Error);
            return ValueTask.CompletedTask;
        }
        if (!Enabled) return ValueTask.CompletedTask;
        // No loops: ignore what rules themselves cause, and the engine's own bookkeeping.
        if (e.Source.StartsWith(EventSources.RulePrefix, StringComparison.Ordinal)) return ValueTask.CompletedTask;
        if (e.Type.StartsWith("automation.", StringComparison.Ordinal)) return ValueTask.CompletedTask;

        if (e.Type == EventTypes.HotkeyPressed && e.DataAs<HotkeyPressedEvent>()?.HotkeyId is { } hotkeyId
            && hotkeyId.StartsWith(EventSources.RulePrefix, StringComparison.Ordinal))
        {
            var rule = Rules.FirstOrDefault(r => r.HotkeyId == hotkeyId);
            if (rule is { IsRunnable: true }) TryStart(rule, "hotkey", AutomationContext.FromEvent(e, _clock));
            return ValueTask.CompletedTask;
        }

        JsonObject? context = null;
        foreach (var rule in Rules)
        {
            if (!rule.IsRunnable || !rule.When.Any(p => TopicPattern.Matches(p, e.Type))) continue;
            context ??= AutomationContext.FromEvent(e, _clock);
            bool matches;
            try
            {
                matches = rule.Condition?.Evaluate(context) ?? true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Rule '{Rule}': evaluating 'if' failed", rule.Name);
                Record(rule.Name, e.Type, false, $"Condition failed: {ex.Message}");
                continue;
            }
            if (matches) TryStart(rule, e.Type, context.DeepClone().AsObject());
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs a rule now (Automation tab, launcher), ignoring its trigger, condition and cooldown.</summary>
    public async Task<RuleRun> RunNowAsync(string name, CancellationToken ct = default)
    {
        var rule = Rules.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new AutomationException($"There's no rule called '{name}'.");
        if (rule.Error is not null) throw new AutomationException($"'{rule.Name}' can't run: {rule.Error}");
        lock (_gate)
        {
            if (!_running.Add(rule.Name)) throw new AutomationException($"'{rule.Name}' is already running.");
            _lastStart[rule.Name] = _clock.UtcNow;
        }
        return await ExecuteAsync(rule, "manual", AutomationContext.Manual(_clock), ct).ConfigureAwait(false);
    }

    private void TryStart(RuleDefinition rule, string trigger, JsonObject context)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (_running.Contains(rule.Name))
            {
                _log.LogInformation("Rule '{Rule}' skipped ({Trigger}): still running", rule.Name, trigger);
                return;
            }
            if (rule.CooldownSeconds > 0 && _lastStart.TryGetValue(rule.Name, out var last) && now - last < TimeSpan.FromSeconds(rule.CooldownSeconds))
            {
                _log.LogDebug("Rule '{Rule}' skipped ({Trigger}): cooling down", rule.Name, trigger);
                return;
            }
            while (_recentStarts.Count > 0 && now - _recentStarts.Peek() > TimeSpan.FromMinutes(1)) _recentStarts.Dequeue();
            if (_recentStarts.Count >= _config.Current.Automation.MaxRunsPerMinute)
            {
                _log.LogWarning("Rule '{Rule}' skipped: more than {Max} rule runs in the last minute", rule.Name, _config.Current.Automation.MaxRunsPerMinute);
                Record(rule.Name, trigger, false, "Skipped: too many rule runs in the last minute (max_runs_per_minute).");
                return;
            }
            _recentStarts.Enqueue(now);
            _running.Add(rule.Name);
            _lastStart[rule.Name] = now;
        }

        var run = Task.Run(() => ExecuteAsync(rule, trigger, context, _stopping.Token));
        lock (_gate)
        {
            _inFlight.RemoveWhere(t => t.IsCompleted);
            _inFlight.Add(run);
        }
    }

    private async Task<RuleRun> ExecuteAsync(RuleDefinition rule, string trigger, JsonObject context, CancellationToken ct)
    {
        var source = EventSources.Rule(rule.Name);
        var results = new List<string>();
        RuleRun record;
        try
        {
            foreach (var spec in rule.Then)
            {
                var result = await _actions.ExecuteAsync(new ActionInvocation(spec, context, ActionOrigin.Rule, source), ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(result)) results.Add(result);
            }
            record = Record(rule.Name, trigger, true, results.Count == 0 ? "Done" : string.Join(" · ", results));
            _log.LogInformation("Rule '{Rule}' ran ({Trigger}): {Result}", rule.Name, trigger, record.Message);
        }
        catch (AutomationException ex)
        {
            record = Record(rule.Name, trigger, false, ex.Message);
            _log.LogWarning("Rule '{Rule}' failed ({Trigger}): {Error}", rule.Name, trigger, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            record = Record(rule.Name, trigger, false, "Stopped (TaskHub is closing)");
        }
        catch (Exception ex)
        {
            record = Record(rule.Name, trigger, false, ex.Message);
            _log.LogError(ex, "Rule '{Rule}' failed ({Trigger})", rule.Name, trigger);
        }
        finally
        {
            lock (_gate) _running.Remove(rule.Name);
        }
        _bus.Publish(EventTypes.RuleRan, new RuleRanEvent(rule.Name, trigger, record.Ok, record.Message), source);
        return record;
    }

    private RuleRun Record(string rule, string trigger, bool ok, string message)
    {
        var run = new RuleRun(_clock.UtcNow, rule, trigger, ok, message);
        Activity.Add(run);
        return run;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task[] running;
        lock (_gate) running = [.. _inFlight];
        if (running.Length > 0)
            await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _stopping.Dispose();
    }
}

/// <summary>
/// Runs a task's "when reminded" actions when its reminder fires — once per occurrence (snoozes and
/// repeat-until-done nags don't run them again), and not for reminders missed more than 15 minutes ago.
/// </summary>
public sealed class ReminderActionRunner(IEventBus bus, ActionDispatcher actions, AutomationActivity activity, IClock clock,
    ILogger<ReminderActionRunner> log) : IHostedService, IDisposable
{
    public static readonly TimeSpan MissedLimit = TimeSpan.FromMinutes(15);
    private readonly Lock _gate = new();
    private readonly LinkedList<string> _done = new();
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Subscribe(EventTypes.ReminderDue, async (e, ct) =>
        {
            if (e.DataAs<ReminderEvent>() is { } r) await RunAsync(e, r, ct).ConfigureAwait(false);
        });
        return Task.CompletedTask;
    }

    /// <summary>Runs (or skips) the actions for one reminder. Public for tests.</summary>
    public async Task<RuleRun?> RunAsync(EventEnvelope e, ReminderEvent r, CancellationToken ct)
    {
        var task = r.Task;
        if (string.IsNullOrWhiteSpace(task.ReminderActions)) return null;
        if (r.Missed && clock.UtcNow - r.ScheduledUtc > MissedLimit)
        {
            log.LogInformation("Not running reminder actions of task {Id}: the reminder was missed at {At:u}", task.Id, r.ScheduledUtc);
            return null;
        }

        var key = $"{task.Id}|{task.DueDate}|{task.DueTime}";
        lock (_gate)
        {
            if (_done.Contains(key)) return null;
            _done.AddLast(key);
            while (_done.Count > 500) _done.RemoveFirst();
        }

        var source = $"task:{task.Id}";
        var results = new List<string>();
        RuleRun run;
        try
        {
            var context = AutomationContext.FromEvent(e, clock);
            foreach (var spec in ActionScript.Parse(task.ReminderActions))
            {
                var result = await actions.ExecuteAsync(new ActionInvocation(spec, context, ActionOrigin.Task, EventSources.Rule("reminder")), ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(result)) results.Add(result);
            }
            run = new RuleRun(clock.UtcNow, Title(task), "reminder.due", true, results.Count == 0 ? "Done" : string.Join(" · ", results));
            log.LogInformation("Reminder actions of task {Id} ran: {Result}", task.Id, run.Message);
        }
        catch (AutomationException ex)
        {
            run = new RuleRun(clock.UtcNow, Title(task), "reminder.due", false, ex.Message);
            log.LogWarning("Reminder actions of task {Id} failed: {Error}", task.Id, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run = new RuleRun(clock.UtcNow, Title(task), "reminder.due", false, ex.Message);
            log.LogError(ex, "Reminder actions of task {Id} failed", task.Id);
        }
        activity.Add(run);
        bus.Publish(EventTypes.ReminderActionsRan, new RuleRanEvent(source, "reminder.due", run.Ok, run.Message), source);
        return run;
    }

    private static string Title(TaskDto t) => "Task: " + (t.Title.Length <= 40 ? t.Title : t.Title[..39] + "…");

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();
}
