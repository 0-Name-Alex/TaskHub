using TaskHub.Abstractions.Events;
using TaskHub.Core.Events;

namespace TaskHub.Core.Tests.Events;

public class InMemoryEventBusTests
{
    private sealed record Payload(int N);

    [Fact]
    public async Task Delivers_envelope_with_metadata_and_payload()
    {
        await using var bus = TestBus.Create();
        using var rec = new TestBus.Recorder(bus);

        var published = bus.Publish("task.created", new Payload(7), "ui");

        var events = await rec.WaitForAsync(e => e.Count == 1);
        var e = events[0];
        Assert.Equal(published.Id, e.Id);
        Assert.Equal("task.created", e.Type);
        Assert.Equal("ui", e.Source);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero), e.Time);
        Assert.Equal(7, e.DataAs<Payload>()!.N);
        Assert.Null(e.DataAs<string>());
    }

    [Fact]
    public async Task Pattern_subscriptions_only_get_matching_events()
    {
        await using var bus = TestBus.Create();
        using var tasks = new TestBus.Recorder(bus, "task.*");
        using var exact = new TestBus.Recorder(bus, "reminder.due");
        using var all = new TestBus.Recorder(bus, "*");

        bus.Publish("task.created");
        bus.Publish("reminder.due");
        bus.Publish("task.completed");
        bus.Publish("system.resumed");

        await all.WaitForAsync(e => e.Count == 4);
        Assert.Equal(["task.created", "task.completed"], (await tasks.WaitForAsync(e => e.Count == 2)).Select(e => e.Type));
        Assert.Equal(["reminder.due"], (await exact.WaitForAsync(e => e.Count == 1)).Select(e => e.Type));
    }

    [Fact]
    public async Task Preserves_publish_order_per_subscriber()
    {
        await using var bus = TestBus.Create();
        using var rec = new TestBus.Recorder(bus);

        for (var i = 0; i < 500; i++) bus.Publish("test.seq", new Payload(i));

        var events = await rec.WaitForAsync(e => e.Count == 500);
        Assert.Equal(Enumerable.Range(0, 500), events.Select(e => e.DataAs<Payload>()!.N));
    }

    [Fact]
    public async Task Slow_subscriber_does_not_block_publisher_or_other_subscribers()
    {
        await using var bus = TestBus.Create();
        var gate = new TaskCompletionSource();
        using var slow = bus.Subscribe("*", async (_, ct) => await gate.Task.WaitAsync(ct));
        using var fast = new TestBus.Recorder(bus);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) bus.Publish("test.x");
        Assert.True(sw.ElapsedMilliseconds < 1000, "Publish should never wait for subscribers");

        await fast.WaitForAsync(e => e.Count == 100);
        gate.SetResult();
    }

    [Fact]
    public async Task Full_queue_drops_oldest_instead_of_blocking()
    {
        await using var bus = TestBus.Create();
        var gate = new TaskCompletionSource();
        var seen = new List<int>();
        using var sub = bus.Subscribe("*", async (e, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            lock (seen) seen.Add(e.DataAs<Payload>()!.N);
        }, capacity: 10);

        for (var i = 0; i < 50; i++) bus.Publish("test.x", new Payload(i));
        gate.SetResult();

        await Task.Delay(300);
        lock (seen)
        {
            Assert.True(seen.Count <= 11, $"expected at most capacity+1 events, got {seen.Count}");
            Assert.Equal(49, seen[^1]); // newest event survives
        }
    }

    [Fact]
    public async Task Handler_exception_does_not_stop_the_subscription()
    {
        await using var bus = TestBus.Create();
        var handled = new List<int>();
        using var sub = bus.Subscribe("*", e =>
        {
            var n = e.DataAs<Payload>()!.N;
            if (n == 1) throw new InvalidOperationException("boom");
            lock (handled) handled.Add(n);
        });

        bus.Publish("test.x", new Payload(0));
        bus.Publish("test.x", new Payload(1));
        bus.Publish("test.x", new Payload(2));

        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            lock (handled) if (handled.Count == 2) break;
            await Task.Delay(10);
        }
        lock (handled) Assert.Equal([0, 2], handled);
    }

    [Fact]
    public async Task Unsubscribed_handler_receives_nothing_further()
    {
        await using var bus = TestBus.Create();
        var rec = new TestBus.Recorder(bus);
        bus.Publish("test.a");
        await rec.WaitForAsync(e => e.Count == 1);

        rec.Dispose();
        bus.Publish("test.b");
        await Task.Delay(100);

        Assert.Single(rec.Snapshot());
    }

    [Fact]
    public async Task DisposeAsync_drains_queued_events()
    {
        var bus = TestBus.Create();
        var count = 0;
        bus.Subscribe("*", async (_, ct) =>
        {
            await Task.Delay(5, ct);
            Interlocked.Increment(ref count);
        });

        for (var i = 0; i < 20; i++) bus.Publish("test.x");
        await bus.DisposeAsync();

        Assert.Equal(20, count);
    }

    [Theory]
    [InlineData("Task.Created")]
    [InlineData("task created")]
    [InlineData("")]
    public async Task Rejects_invalid_event_types(string type)
    {
        await using var bus = TestBus.Create();
        Assert.Throws<ArgumentException>(() => bus.Publish(type));
    }

    [Fact]
    public async Task Rejects_invalid_patterns()
    {
        await using var bus = TestBus.Create();
        Assert.Throws<ArgumentException>(() => bus.Subscribe("*.created", _ => { }));
    }

    [Fact]
    public void Event_ids_are_unique_and_time_ordered()
    {
        using var bus = TestBus.Create();
        var ids = Enumerable.Range(0, 100).Select(_ => bus.Publish(EventTypes.AppStarted).Id).ToList();
        Assert.Equal(100, ids.Distinct().Count());
    }
}
