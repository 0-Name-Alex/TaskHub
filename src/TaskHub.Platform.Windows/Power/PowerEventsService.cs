using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Events;

namespace TaskHub.Platform.Windows.Power;

/// <summary>
/// Publishes <c>system.suspended</c>, <c>system.resumed</c> (laptop lid, sleep, hibernate) and
/// <c>system.time_changed</c> (clock or time zone changed), and <c>system.locked</c> / <c>system.unlocked</c>. The reminder scheduler listens to these
/// to catch up on anything that came due while the machine slept.
/// </summary>
public sealed class PowerEventsService(IEventBus bus, ILogger<PowerEventsService> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        return Task.CompletedTask;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                log.LogInformation("Session locked");
                bus.Publish(EventTypes.SystemLocked, null, EventSources.Platform);
                break;
            case SessionSwitchReason.SessionUnlock:
                log.LogInformation("Session unlocked");
                bus.Publish(EventTypes.SystemUnlocked, null, EventSources.Platform);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                log.LogInformation("System is going to sleep");
                bus.Publish(EventTypes.SystemSuspended, null, EventSources.Platform);
                break;
            case PowerModes.Resume:
                log.LogInformation("System resumed");
                bus.Publish(EventTypes.SystemResumed, null, EventSources.Platform);
                break;
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        TimeZoneInfo.ClearCachedData(); // pick up a changed time zone
        log.LogInformation("System time or time zone changed (now {Zone})", TimeZoneInfo.Local.Id);
        bus.Publish(EventTypes.SystemTimeChanged, null, EventSources.Platform);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        return Task.CompletedTask;
    }
}
