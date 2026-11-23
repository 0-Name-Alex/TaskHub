using Microsoft.Extensions.Hosting;
using Serilog.Core;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Events;

namespace TaskHub.App;

/// <summary>Applies logging.minimum_level changes from config.toml without a restart.</summary>
internal sealed class LogLevelSync(IEventBus bus, ConfigStore config, LoggingLevelSwitch levelSwitch) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Subscribe(EventTypes.ConfigChanged,
            _ => levelSwitch.MinimumLevel = Logging.ParseLevel(config.Current.Logging.MinimumLevel));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
