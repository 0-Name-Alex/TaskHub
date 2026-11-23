using TaskHub.Abstractions.Events;

namespace TaskHub.Core.Events;

public interface IEventBus
{
    /// <summary>
    /// Publishes an event. Never blocks: each subscriber has its own queue and consumer loop,
    /// so a slow subscriber cannot stall the publisher or other subscribers.
    /// </summary>
    EventEnvelope Publish(string type, object? data = null, string source = EventSources.Core);

    /// <summary>
    /// Subscribes to events whose type matches <paramref name="pattern"/>
    /// (<c>*</c>, <c>task.*</c> or an exact type). Handlers for one subscription run
    /// sequentially, in publish order, on a background thread. Dispose to unsubscribe.
    /// </summary>
    IDisposable Subscribe(string pattern, Func<EventEnvelope, CancellationToken, ValueTask> handler, int capacity = 1024);
}

public static class EventBusExtensions
{
    public static IDisposable Subscribe(this IEventBus bus, string pattern, Action<EventEnvelope> handler) =>
        bus.Subscribe(pattern, (e, _) =>
        {
            handler(e);
            return ValueTask.CompletedTask;
        });
}
