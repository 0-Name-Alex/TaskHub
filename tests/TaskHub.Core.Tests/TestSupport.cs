using Microsoft.Extensions.Logging.Abstractions;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;
using TaskHub.Core.Hotkeys;

namespace TaskHub.Core.Tests;

internal sealed class TestClock(DateTimeOffset now, TimeZoneInfo? zone = null) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public TimeZoneInfo LocalZone { get; set; } = zone ?? TimeZoneInfo.Utc;
}

internal static class TestBus
{
    public static InMemoryEventBus Create() =>
        new(new TestClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)), NullLogger<InMemoryEventBus>.Instance);

    /// <summary>Collects matching events; <see cref="WaitForAsync"/> waits until a predicate holds.</summary>
    public sealed class Recorder : IDisposable
    {
        private readonly List<EventEnvelope> _events = [];
        private readonly IDisposable _sub;

        public Recorder(IEventBus bus, string pattern = "*") =>
            _sub = bus.Subscribe(pattern, e => { lock (_events) _events.Add(e); });

        public IReadOnlyList<EventEnvelope> Snapshot() { lock (_events) return [.. _events]; }

        public async Task<IReadOnlyList<EventEnvelope>> WaitForAsync(Func<IReadOnlyList<EventEnvelope>, bool> done, int timeoutMs = 3000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                var snap = Snapshot();
                if (done(snap)) return snap;
                await Task.Delay(10);
            }
            throw new TimeoutException($"Condition not met; got [{string.Join(", ", Snapshot().Select(e => e.Type))}]");
        }

        public void Dispose() => _sub.Dispose();
    }
}

internal sealed class FakeHotkeyService : IHotkeyService
{
    public Dictionary<string, HotkeyGesture> Registered { get; } = new();
    public HashSet<string> TakenByOtherApps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int RegisterCalls { get; private set; }

    public event EventHandler<string>? Pressed;

    public HotkeyRegistrationResult Register(string id, HotkeyGesture gesture)
    {
        RegisterCalls++;
        Registered.Remove(id);
        if (TakenByOtherApps.Contains(gesture.ToString()))
            return HotkeyRegistrationResult.Fail($"{gesture} is already used by another app or by Windows.");
        Registered[id] = gesture;
        return HotkeyRegistrationResult.Ok;
    }

    public void Unregister(string id) => Registered.Remove(id);

    public void Press(string id) => Pressed?.Invoke(this, id);
}

/// <summary>Creates and cleans up a unique temp folder.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "taskhub-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
