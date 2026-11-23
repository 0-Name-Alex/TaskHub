using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Events;

namespace TaskHub.Core.Hosting;

/// <summary>Turns command lines forwarded by later launches into <c>app.activated</c> events.</summary>
public sealed class SingleInstanceListener(SingleInstanceGuard guard, IEventBus bus, ILogger<SingleInstanceListener> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        guard.StartServer(args =>
        {
            bus.Publish(EventTypes.AppActivated, new AppActivatedEvent(args), EventSources.Instance);
            return Task.CompletedTask;
        }, log);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => guard.StopServerAsync();
}
