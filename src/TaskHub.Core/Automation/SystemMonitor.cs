using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;

namespace TaskHub.Core.Automation;

public sealed record PowerState(bool OnBattery, int? Percent);

public sealed record DriveSpace(string Name, long FreeBytes, long TotalBytes)
{
    public double FreeGb => FreeBytes / 1024d / 1024d / 1024d;
    public double TotalGb => TotalBytes / 1024d / 1024d / 1024d;
}

/// <summary>Reads idle time, battery and disk space. Implemented with Win32 calls on Windows.</summary>
public interface ISystemStatus
{
    /// <summary>Time since the last keyboard/mouse input, or null if unknown.</summary>
    TimeSpan? GetIdleTime();

    /// <summary>Battery state, or null on machines without a battery.</summary>
    PowerState? GetPower();

    IReadOnlyList<DriveSpace> GetFixedDrives();
}

public sealed record SystemSnapshot(TimeSpan? Idle, bool IsIdle, PowerState? Power, IReadOnlyList<DriveSpace> Drives);

/// <summary>
/// Polls the machine and publishes events for rules: <c>system.idle</c> / <c>system.active</c>,
/// <c>system.power.ac</c> / <c>system.power.battery</c>, <c>system.battery.low</c> and
/// <c>system.disk.low</c>. Each fires once when its threshold is crossed (not on every poll), and
/// re-arms once things recover.
/// </summary>
public sealed class SystemMonitor : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DiskInterval = TimeSpan.FromMinutes(30);

    private readonly ISystemStatus _status;
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly Func<AutomationOptions> _options;
    private readonly ILogger<SystemMonitor> _log;
    private readonly HashSet<string> _lowDisks = new(StringComparer.OrdinalIgnoreCase);
    private bool _idle;
    private TimeSpan _lastIdleTime;
    private DateTimeOffset _idleSince;
    private bool? _onBattery;
    private bool _batteryLowFired;
    private DateTimeOffset _lastDiskCheck = DateTimeOffset.MinValue;
    private SystemSnapshot _snapshot = new(null, false, null, []);

    public SystemMonitor(ISystemStatus status, IEventBus bus, IClock clock, Func<AutomationOptions> options, ILogger<SystemMonitor> log)
    {
        _status = status;
        _bus = bus;
        _clock = clock;
        _options = options;
        _log = log;
    }

    public SystemSnapshot Snapshot => _snapshot;

    public event EventHandler? Updated;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup settle; the first disk check comes a couple of minutes in.
        _lastDiskCheck = _clock.UtcNow - DiskInterval + TimeSpan.FromMinutes(2);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                Check();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "System check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One poll. Public so tests can drive it with a fake <see cref="ISystemStatus"/>.</summary>
    public void Check(bool forceDisks = false)
    {
        var now = _clock.UtcNow;
        var o = _options();

        // ---- idle / active
        var idle = _status.GetIdleTime();
        if (idle is { } idleTime)
        {
            var threshold = TimeSpan.FromMinutes(o.IdleMinutes);
            if (!_idle && o.IdleMinutes > 0 && idleTime >= threshold)
            {
                _idle = true;
                _idleSince = now - idleTime;
                _log.LogInformation("Idle for {Minutes} min", (int)idleTime.TotalMinutes);
                _bus.Publish(EventTypes.SystemIdle, new IdleEvent((int)idleTime.TotalMinutes), EventSources.Platform);
            }
            else if (_idle && (idleTime < _lastIdleTime || o.IdleMinutes == 0))
            {
                _idle = false;
                var lasted = (int)Math.Round((now - idleTime - _idleSince).TotalMinutes);
                _log.LogInformation("Active again after {Minutes} min idle", lasted);
                _bus.Publish(EventTypes.SystemActive, new IdleEvent(Math.Max(lasted, 0)), EventSources.Platform);
            }
            _lastIdleTime = idleTime;
        }

        // ---- power / battery
        var power = _status.GetPower();
        if (power is not null)
        {
            if (_onBattery is { } was && was != power.OnBattery)
            {
                _log.LogInformation(power.OnBattery ? "Unplugged (battery {Percent}%)" : "Plugged in (battery {Percent}%)", power.Percent);
                _bus.Publish(power.OnBattery ? EventTypes.PowerBattery : EventTypes.PowerAc,
                    new BatteryEvent(power.Percent ?? -1, power.OnBattery), EventSources.Platform);
            }
            _onBattery = power.OnBattery;

            var low = o.BatteryLowPercent;
            if (power.OnBattery && power.Percent is { } pct && low > 0 && pct <= low)
            {
                if (!_batteryLowFired)
                {
                    _batteryLowFired = true;
                    _log.LogInformation("Battery low: {Percent}%", pct);
                    _bus.Publish(EventTypes.BatteryLow, new BatteryEvent(pct, true), EventSources.Platform);
                }
            }
            else if (!power.OnBattery || power.Percent is null || power.Percent > low + 3)
            {
                _batteryLowFired = false;
            }
        }

        // ---- disks
        var drives = _snapshot.Drives;
        if (forceDisks || now - _lastDiskCheck >= DiskInterval)
        {
            _lastDiskCheck = now;
            drives = _status.GetFixedDrives();
            var limitGb = o.DiskLowGb;
            foreach (var d in drives)
            {
                if (limitGb > 0 && d.FreeGb < limitGb)
                {
                    if (_lowDisks.Add(d.Name))
                    {
                        _log.LogInformation("Disk {Drive} low: {Free:0.0} GB free", d.Name, d.FreeGb);
                        _bus.Publish(EventTypes.DiskLow, new DiskLowEvent(d.Name, Math.Round(d.FreeGb, 1), Math.Round(d.TotalGb, 1)), EventSources.Platform);
                    }
                }
                else if (d.FreeGb >= limitGb + 1)
                {
                    _lowDisks.Remove(d.Name);
                }
            }
        }

        _snapshot = new SystemSnapshot(idle, _idle, power, drives);
        Updated?.Invoke(this, EventArgs.Empty);
    }
}
