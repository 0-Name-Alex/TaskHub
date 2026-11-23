using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Hosting;

namespace TaskHub.Core.Events;

/// <summary>
/// In-process event bus. Each subscription owns a bounded channel and a consumer loop.
/// When a subscriber falls more than <c>capacity</c> events behind, its oldest queued
/// events are dropped (and logged) rather than blocking the publisher.
/// </summary>
public sealed class InMemoryEventBus : IEventBus, IAsyncDisposable, IDisposable
{
    private readonly IClock _clock;
    private readonly ILogger<InMemoryEventBus> _log;
    private readonly Lock _gate = new();
    private volatile Subscription[] _subscriptions = []; // copy-on-write
    private bool _disposed;

    public InMemoryEventBus(IClock clock, ILogger<InMemoryEventBus> log)
    {
        _clock = clock;
        _log = log;
    }

    public EventEnvelope Publish(string type, object? data = null, string source = EventSources.Core)
    {
        if (!TopicPattern.IsValidType(type))
            throw new ArgumentException($"Invalid event type '{type}'. Use lowercase dotted names like 'task.created'.", nameof(type));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var envelope = EventEnvelope.Create(type, data, source, _clock.UtcNow);
        foreach (var sub in _subscriptions)
        {
            if (TopicPattern.Matches(sub.Pattern, type))
                sub.Enqueue(envelope);
        }
        return envelope;
    }

    public IDisposable Subscribe(string pattern, Func<EventEnvelope, CancellationToken, ValueTask> handler, int capacity = 1024)
    {
        if (!TopicPattern.IsValidPattern(pattern))
            throw new ArgumentException($"Invalid subscription pattern '{pattern}'.", nameof(pattern));
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        var sub = new Subscription(this, pattern, handler, capacity, _log);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscriptions = [.. _subscriptions, sub];
        }
        sub.Start();
        return sub;
    }

    private void Remove(Subscription sub)
    {
        lock (_gate) _subscriptions = Array.FindAll(_subscriptions, s => !ReferenceEquals(s, sub));
    }

    /// <summary>Stops accepting events and lets every subscriber drain its queue (bounded wait).</summary>
    public async ValueTask DisposeAsync()
    {
        Subscription[] subs;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            subs = _subscriptions;
            _subscriptions = [];
        }
        await Task.WhenAll(subs.Select(s => s.CompleteAsync(TimeSpan.FromSeconds(2)))).ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Subscription : IDisposable
    {
        private readonly InMemoryEventBus _owner;
        private readonly Func<EventEnvelope, CancellationToken, ValueTask> _handler;
        private readonly ILogger _log;
        private readonly Channel<EventEnvelope> _channel;
        private readonly CancellationTokenSource _cts = new();
        private Task _loop = Task.CompletedTask;
        private long _dropped;

        public Subscription(InMemoryEventBus owner, string pattern, Func<EventEnvelope, CancellationToken, ValueTask> handler, int capacity, ILogger log)
        {
            _owner = owner;
            Pattern = pattern;
            _handler = handler;
            _log = log;
            _channel = Channel.CreateBounded<EventEnvelope>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                },
                OnDropped);
        }

        public string Pattern { get; }

        public void Start() => _loop = Task.Run(RunAsync);

        public void Enqueue(EventEnvelope e) => _channel.Writer.TryWrite(e);

        private void OnDropped(EventEnvelope e)
        {
            var n = Interlocked.Increment(ref _dropped);
            if (n == 1 || n % 100 == 0)
                _log.LogWarning("Subscriber '{Pattern}' is falling behind; dropped {Count} event(s) so far (latest {Type})", Pattern, n, e.Type);
        }

        private async Task RunAsync()
        {
            var ct = _cts.Token;
            try
            {
                await foreach (var e in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        await _handler(e, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Event handler for '{Pattern}' failed on {Type} ({Id})", Pattern, e.Type, e.Id);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // unsubscribed
            }
        }

        public async Task CompleteAsync(TimeSpan drainTimeout)
        {
            _channel.Writer.TryComplete();
            if (await Task.WhenAny(_loop, Task.Delay(drainTimeout)).ConfigureAwait(false) != _loop)
                await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
        }

        /// <summary>Unsubscribe: stop immediately, discarding anything still queued.</summary>
        public void Dispose()
        {
            _owner.Remove(this);
            _channel.Writer.TryComplete();
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
