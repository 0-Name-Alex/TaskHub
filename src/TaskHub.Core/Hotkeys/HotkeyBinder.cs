using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Events;

namespace TaskHub.Core.Hotkeys;

public sealed record HotkeyStatus(string Id, string Gesture, bool Registered, string? Error);

/// <summary>
/// Registers the hotkeys from config, re-binds them when config changes, and turns key
/// presses into <c>hotkey.pressed</c> events. Failures (bad syntax, combination taken by
/// another app) are reported per hotkey via <c>hotkey.failed</c> and <see cref="Status"/>.
/// </summary>
public sealed class HotkeyBinder : IHostedService, IDisposable
{
    private readonly IHotkeyService _hotkeys;
    private readonly ConfigStore _config;
    private readonly IEventBus _bus;
    private readonly ILogger<HotkeyBinder> _log;
    private readonly ConcurrentDictionary<string, HotkeyStatus> _status = new();
    private readonly Dictionary<string, string> _bound = new();
    private readonly Lock _gate = new();
    private IDisposable? _configSubscription;

    public HotkeyBinder(IHotkeyService hotkeys, ConfigStore config, IEventBus bus, ILogger<HotkeyBinder> log)
    {
        _hotkeys = hotkeys;
        _config = config;
        _bus = bus;
        _log = log;
    }

    public IReadOnlyDictionary<string, HotkeyStatus> Status => _status;

    public event EventHandler? StatusChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _hotkeys.Pressed += OnPressed;
        Bind(_config.Current);
        _configSubscription = _bus.Subscribe(EventTypes.ConfigChanged, _ => Bind(_config.Current));
        return Task.CompletedTask;
    }

    /// <summary>The [hotkeys] section plus the hotkeys of enabled rules (ids <c>rule:&lt;name&gt;</c>).</summary>
    public static Dictionary<string, string> HotkeyMap(TaskHubOptions options)
    {
        var map = new Dictionary<string, string>(options.Hotkeys.AsMap(), StringComparer.Ordinal);
        foreach (var rule in options.GetAutomationConfig().Rules)
        {
            if (rule.Enabled && rule.Hotkey is { Length: > 0 } gesture && !map.ContainsKey(rule.HotkeyId))
                map[rule.HotkeyId] = gesture;
        }
        return map;
    }

    public void Bind(TaskHubOptions options) => Bind(HotkeyMap(options));

    public void Bind(HotkeyOptions options) => Bind(new Dictionary<string, string>(options.AsMap(), StringComparer.Ordinal));

    /// <summary>Applies a hotkey configuration, only touching entries that changed. Ids not in the map are unregistered.</summary>
    public void Bind(IReadOnlyDictionary<string, string> map)
    {
        lock (_gate)
        {
            foreach (var gone in _bound.Keys.Where(id => !map.ContainsKey(id)).ToList())
            {
                _hotkeys.Unregister(gone);
                _bound.Remove(gone);
                _status.TryRemove(gone, out _);
                _log.LogInformation("Hotkey {Id} removed", gone);
            }

            foreach (var (id, text) in map)
            {
                var trimmed = text.Trim();
                if (_bound.TryGetValue(id, out var previous) && previous == trimmed && _status.ContainsKey(id))
                    continue;
                _bound[id] = trimmed;

                if (trimmed.Length == 0)
                {
                    _hotkeys.Unregister(id);
                    _status[id] = new HotkeyStatus(id, "", Registered: false, Error: null);
                    _log.LogInformation("Hotkey {Id} disabled", id);
                    continue;
                }

                if (!HotkeyGesture.TryParse(trimmed, out var gesture, out var parseError))
                {
                    _hotkeys.Unregister(id);
                    Fail(id, trimmed, parseError!);
                    continue;
                }

                var result = _hotkeys.Register(id, gesture);
                if (result.Success)
                {
                    _status[id] = new HotkeyStatus(id, gesture.ToString(), Registered: true, Error: null);
                    _log.LogInformation("Hotkey {Id} registered as {Gesture}", id, gesture);
                }
                else
                {
                    Fail(id, gesture.ToString(), result.Error ?? "Registration failed.");
                }
            }
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Fail(string id, string gesture, string error)
    {
        _status[id] = new HotkeyStatus(id, gesture, Registered: false, Error: error);
        _log.LogWarning("Hotkey {Id} ({Gesture}) not registered: {Error}", id, gesture, error);
        _bus.Publish(EventTypes.HotkeyFailed, new HotkeyFailedEvent(id, gesture, error), EventSources.Platform);
    }

    private void OnPressed(object? sender, string id)
    {
        _log.LogDebug("Hotkey {Id} pressed", id);
        _bus.Publish(EventTypes.HotkeyPressed, new HotkeyPressedEvent(id), EventSources.Platform);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _configSubscription?.Dispose();
        _hotkeys.Pressed -= OnPressed;
        // A rebind may be mid-flight on the bus thread, marshalling to the UI thread we are
        // probably on; don't wait on it forever.
        if (_gate.TryEnter(TimeSpan.FromSeconds(1)))
        {
            try
            {
                foreach (var id in _bound.Keys)
                    _hotkeys.Unregister(id);
                _bound.Clear();
            }
            finally
            {
                _gate.Exit();
            }
        }
        return Task.CompletedTask;
    }

    public void Dispose() => _configSubscription?.Dispose();
}
