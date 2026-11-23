using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Domain;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;
using TaskHub.Core.Scheduling;
using TaskHub.Core.Services;
using TaskHub.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace TaskHub.Storage.Tests;

public sealed class TestClock(DateTimeOffset now, TimeZoneInfo zone) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public TimeZoneInfo LocalZone { get; set; } = zone;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// A real SQLite database in a temp folder, migrated exactly like the app does it, plus the
/// application services on top. One per test.
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    // Monday 2 Nov 2026, 10:30 in India (05:00 UTC)
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Today = new(2026, 11, 2);

    private readonly ServiceProvider _provider;
    private readonly List<EventEnvelope> _events = [];
    private readonly IDisposable _subscription;

    private Harness(string root, ServiceProvider provider)
    {
        Root = root;
        _provider = provider;
        _subscription = Bus.Subscribe("*", e => { lock (_events) _events.Add(e); });
    }

    public string Root { get; }

    /// <summary>Set before disposing to keep the database (simulated restart).</summary>
    public bool KeepFiles { get; set; }
    public AppPaths Paths => _provider.GetRequiredService<AppPaths>();
    public TestClock Clock => (TestClock)_provider.GetRequiredService<IClock>();
    public TaskService Tasks => _provider.GetRequiredService<TaskService>();
    public ImportExportService ImportExport => _provider.GetRequiredService<ImportExportService>();
    public ITaskStore Store => _provider.GetRequiredService<ITaskStore>();
    public IEventBus Bus => _provider.GetRequiredService<IEventBus>();
    public ReminderOptions ReminderOptions => _provider.GetRequiredService<ReminderOptions>();
    public MaintenanceOptions MaintenanceOptions => _provider.GetRequiredService<MaintenanceOptions>();

    /// <summary>A scheduler wired to this database; call FireDueAsync() directly (no background loop).</summary>
    public ReminderScheduler CreateScheduler() => new(Store, Bus, Clock, _provider.GetRequiredService<ReminderPlanner>(), NullLogger<ReminderScheduler>.Instance);

    public MaintenanceService CreateMaintenance() =>
        new(Paths, Store, Bus, Clock, () => MaintenanceOptions, NullLogger<MaintenanceService>.Instance);

    public IDbContextFactory<TaskHubDbContext> Db => _provider.GetRequiredService<IDbContextFactory<TaskHubDbContext>>();

    public static async Task<Harness> CreateAsync(string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "taskhub-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.UnderRoot(root);
        paths.EnsureCreated();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(paths);
        services.AddSingleton<IClock>(new TestClock(Start, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")));
        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());
        var reminderOptions = new ReminderOptions();
        var maintenanceOptions = new MaintenanceOptions();
        services.AddSingleton(reminderOptions);
        services.AddSingleton(maintenanceOptions);
        services.AddSingleton(sp => new ReminderPlanner(sp.GetRequiredService<IClock>(), () => reminderOptions));
        services.AddSingleton<RecurrenceEngine>();
        services.AddSingleton<UndoService>();
        services.AddSingleton<TaskService>();
        services.AddSingleton<ImportExportService>();
        services.AddTaskHubStorage(paths);

        var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);
        return new Harness(root, provider);
    }

    public IReadOnlyList<EventEnvelope> Events
    {
        get { lock (_events) return [.. _events]; }
    }

    /// <summary>Waits until an event of the given type has been seen (the bus is asynchronous).</summary>
    public async Task<EventEnvelope> WaitForEventAsync(string type, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var match = Events.LastOrDefault(e => e.Type == type);
            if (match is not null) return match;
            await Task.Delay(10);
        }
        throw new TimeoutException($"No '{type}' event; saw [{string.Join(", ", Events.Select(e => e.Type))}]");
    }

    /// <summary>Events of a type seen so far (the bus is async — use after WaitForEventAsync or a short delay).</summary>
    public IReadOnlyList<EventEnvelope> EventsOf(string type) => Events.Where(e => e.Type == type).ToList();

    public void ClearEvents()
    {
        lock (_events) _events.Clear();
    }

    public Task<IReadOnlyList<TaskItem>> Query(TaskView view = TaskView.All, string? text = null, string? tag = null, Guid? list = null, bool includeCompleted = false) =>
        Tasks.QueryAsync(new TaskQuery { View = view, Text = text, Tag = tag, ListId = list, IncludeCompleted = includeCompleted });

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        var databaseFile = Paths.DatabaseFile;
        await _provider.DisposeAsync();
        // Release this database's pooled connections so the file can be deleted. Only this one:
        // ClearAllPools() would also close connections that tests running in parallel are using.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(StorageServiceCollectionExtensions.ConnectionStringFor(databaseFile)))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        if (KeepFiles) return;
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
