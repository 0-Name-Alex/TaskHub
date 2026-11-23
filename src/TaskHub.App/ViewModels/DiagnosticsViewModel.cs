using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;
using TaskHub.Core.Hotkeys;
using TaskHub.Core.Platform;

namespace TaskHub.App.ViewModels;

public sealed record EventRow(string Time, string Type, string Source, string Data);

public sealed record HotkeyRow(string Name, string Gesture, string State, bool Ok);

/// <summary>
/// Diagnostics tab: app status, hotkeys, autostart, and a live view of the event bus.
/// (Phase 0's main window, kept as a tab.)
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private const int MaxEvents = 300;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IAutostartService _autostart;
    private readonly HotkeyBinder _hotkeys;
    private readonly IUiDispatcher _ui;
    private readonly IDisposable _subscription;
    private bool _syncingAutostart;

    public DiagnosticsViewModel(IEventBus bus, IUiDispatcher ui, ConfigStore config, AppPaths paths,
        IAutostartService autostart, HotkeyBinder hotkeys)
    {
        _ui = ui;
        _autostart = autostart;
        _hotkeys = hotkeys;
        ConfigPath = config.FilePath;
        DataPath = paths.DataRoot;
        LogsPath = paths.LogsDir;

        _subscription = bus.Subscribe("*", env => ui.Post(() => AddEvent(env)));
        _hotkeys.StatusChanged += (_, _) => ui.Post(RefreshHotkeys);
        RefreshHotkeys();
        RefreshAutostart();
    }

    public string ConfigPath { get; }
    public string DataPath { get; }
    public string LogsPath { get; }

    public ObservableCollection<EventRow> Events { get; } = [];
    public ObservableCollection<HotkeyRow> Hotkeys { get; } = [];

    [ObservableProperty]
    private bool _autostartEnabled;

    [ObservableProperty]
    private string? _autostartError;

    partial void OnAutostartEnabledChanged(bool value)
    {
        if (_syncingAutostart) return;
        try
        {
            _autostart.SetEnabled(value);
            AutostartError = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not change autostart");
            AutostartError = ex.Message;
        }
        RefreshAutostart();
    }

    public void RefreshAutostart()
    {
        _syncingAutostart = true;
        try { AutostartEnabled = _autostart.IsEnabled; }
        finally { _syncingAutostart = false; }
    }

    private void RefreshHotkeys()
    {
        Hotkeys.Clear();
        foreach (var s in _hotkeys.Status.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var state = s.Registered ? "Active" : s.Gesture.Length == 0 ? "Disabled" : $"Not registered: {s.Error}";
            Hotkeys.Add(new HotkeyRow(Describe(s.Id), s.Gesture.Length == 0 ? "—" : s.Gesture, state, s.Registered || s.Gesture.Length == 0));
        }
    }

    private static string Describe(string id) => id switch
    {
        HotkeyIds.ShowMainWindow => "Show / hide main window",
        HotkeyIds.QuickAdd => "Quick add task",
        HotkeyIds.Launcher => "Quick launcher",
        HotkeyIds.ToggleDnd => "Toggle Do Not Disturb",
        HotkeyIds.CaptureClipboard => "Clipboard → task",
        HotkeyIds.CaptureScreenshot => "Screenshot → task",
        _ when id.StartsWith("rule:", StringComparison.Ordinal) => "Rule: " + id[5..],
        _ => id,
    };

    private void AddEvent(EventEnvelope env)
    {
        string data;
        try { data = env.Data is null ? "" : JsonSerializer.Serialize(env.Data, env.Data.GetType(), JsonOptions); }
        catch (NotSupportedException) { data = env.Data?.ToString() ?? ""; }

        Events.Insert(0, new EventRow(
            env.Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture), env.Type, env.Source, data));
        while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
    }

    [RelayCommand]
    private void OpenConfig() => TrayIconService.OpenPath(ConfigPath);

    [RelayCommand]
    private void OpenLogs() => TrayIconService.OpenPath(LogsPath);

    [RelayCommand]
    private void OpenDataFolder() => TrayIconService.OpenPath(DataPath);

    [RelayCommand]
    private void ClearEvents() => Events.Clear();

    public void Dispose() => _subscription.Dispose();
}
