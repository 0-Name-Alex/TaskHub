using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Automation;
using TaskHub.Core.Configuration;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;

namespace TaskHub.App.ViewModels;

public sealed record RuleRow(string Name, string Trigger, string? Condition, string Actions, string State, bool Ok, string? LastRun, bool LastOk);

public sealed record WatchRow(string Name, string Path, string State, bool Ok);

public sealed record ActivityRow(string Time, string Rule, string Trigger, string Message, bool Ok);

/// <summary>
/// The Automation tab: rules from config.toml with their state and last run, "Run now", watched
/// folders, what the system monitor sees, and a log of recent runs.
/// </summary>
public sealed partial class AutomationViewModel : ObservableObject, IDisposable
{
    private readonly RuleEngine _rules;
    private readonly FileWatchService _watches;
    private readonly SystemMonitor _monitor;
    private readonly ConfigStore _config;
    private readonly IUiDispatcher _ui;
    private readonly IDisposable _subscription;
    private bool _refreshQueued;

    public AutomationViewModel(RuleEngine rules, FileWatchService watches, SystemMonitor monitor, ConfigStore config, IEventBus bus, IUiDispatcher ui)
    {
        _rules = rules;
        _watches = watches;
        _monitor = monitor;
        _config = config;
        _ui = ui;
        _subscription = bus.Subscribe(EventTypes.ConfigChanged, _ => QueueRefresh());
        _rules.Activity.Changed += (_, _) => QueueRefresh();
        _watches.StatusChanged += (_, _) => QueueRefresh();
        _monitor.Updated += (_, _) => _ui.Post(RefreshSystem);
        Refresh();
    }

    public ObservableCollection<RuleRow> Rules { get; } = [];
    public ObservableCollection<WatchRow> Watches { get; } = [];
    public ObservableCollection<ActivityRow> Activity { get; } = [];
    public ObservableCollection<string> ConfigErrors { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _systemText = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageIsError;

    public string ConfigPath => _config.FilePath;

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ui.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        var culture = CultureInfo.CurrentCulture;
        var automation = _config.Current.GetAutomationConfig();
        var enabled = _rules.Enabled;

        Rules.Clear();
        foreach (var r in automation.Rules)
        {
            var last = _rules.Activity.LastFor(r.Name);
            var state = r.Error is not null ? r.Error
                : !enabled ? "Rules are switched off (rules_enabled = false)"
                : !r.Enabled ? "Disabled"
                : r.CooldownSeconds > 0 ? $"Active · cooldown {r.CooldownSeconds} s"
                : "Active";
            Rules.Add(new RuleRow(
                r.Name,
                r.TriggerText.Length == 0 ? "—" : r.TriggerText,
                r.If,
                string.Join("\n", r.Then.Select(a => a.Describe())),
                state,
                r.Error is null,
                last is null ? null : $"{FormatTime(last.At, culture)} · {last.Message}",
                last?.Ok ?? true));
        }

        Watches.Clear();
        foreach (var w in _watches.Status)
            Watches.Add(new WatchRow(w.Name, w.Path, w.Active ? "Watching" : w.Error ?? "Not active", w.Active));

        ConfigErrors.Clear();
        foreach (var e in automation.Errors) ConfigErrors.Add(e);

        Activity.Clear();
        foreach (var a in _rules.Activity.Snapshot().Take(100))
            Activity.Add(new ActivityRow(FormatTime(a.At, culture), a.Rule, a.Trigger, a.Message, a.Ok));

        var problems = automation.Rules.Count(r => r.Error is not null);
        Summary = automation.Rules.Count == 0
            ? "No rules yet. Add [[rules]] to the config file — it has commented examples under [automation]."
            : $"{automation.Rules.Count} rule(s){(problems > 0 ? $", {problems} with problems" : "")}{(enabled ? "" : " — rules are switched off")}. Changes apply when you save the config file.";
        RefreshSystem();
    }

    private void RefreshSystem()
    {
        var s = _monitor.Snapshot;
        var parts = new List<string>();
        if (s.Idle is { } idle) parts.Add(s.IsIdle ? $"Idle for {(int)idle.TotalMinutes} min" : idle.TotalMinutes >= 1 ? $"Last input {(int)idle.TotalMinutes} min ago" : "Active");
        if (s.Power is { } p) parts.Add(p.Percent is { } pct ? $"Battery {pct}%{(p.OnBattery ? " (on battery)" : " (plugged in)")}" : p.OnBattery ? "On battery" : "Plugged in");
        foreach (var d in s.Drives) parts.Add($"{d.Name} {d.FreeGb:0} GB free");
        SystemText = parts.Count == 0 ? "Checking…" : string.Join(" · ", parts);
    }

    private static string FormatTime(DateTimeOffset at, CultureInfo culture)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("T", culture) : local.ToString("ddd d MMM, t", culture);
    }

    [RelayCommand]
    private async Task RunRuleAsync(RuleRow? row)
    {
        if (row is null) return;
        try
        {
            Message = $"Running “{row.Name}”…";
            MessageIsError = false;
            var run = await _rules.RunNowAsync(row.Name);
            Message = run.Ok ? $"“{row.Name}”: {run.Message}" : $"“{row.Name}” failed: {run.Message}";
            MessageIsError = !run.Ok;
        }
        catch (AutomationException ex)
        {
            Message = ex.Message;
            MessageIsError = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Running rule {Rule} failed", row.Name);
            Message = ex.Message;
            MessageIsError = true;
        }
    }

    [RelayCommand]
    private void OpenConfig() => TrayIconService.OpenPath(_config.FilePath);

    [RelayCommand]
    private void ClearActivity() => _rules.Activity.Clear();

    public void Dispose() => _subscription.Dispose();
}
