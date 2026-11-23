using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Domain;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;

namespace TaskHub.Storage;

/// <summary>
/// Once a day (checked hourly, and at startup): a consistent backup of the database to
/// <c>backups\taskhub-YYYY-MM-DD.db</c>, pruning old ones; and permanent deletion of tasks that
/// have been in the trash longer than the retention period.
/// </summary>
public sealed class MaintenanceService(
    AppPaths paths,
    ITaskStore store,
    IEventBus bus,
    IClock clock,
    Func<MaintenanceOptions> options,
    ILogger<MaintenanceService> log,
    IHostApplicationLifetime? lifetime = null) : BackgroundService
{
    private const string Prefix = "taskhub-";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (lifetime is not null)
        {
            var started = new TaskCompletionSource();
            using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
            using (stoppingToken.Register(() => started.TrySetCanceled()))
            {
                try { await started.Task; }
                catch (TaskCanceledException) { return; }
            }
        }

        // Let startup settle before doing I/O.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(CheckEvery);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Maintenance failed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        BackupIfDue();
        PruneBackups();
        var cutoff = clock.UtcNow - TimeSpan.FromDays(options().TrashRetentionDays);
        var purged = await store.PurgeDeletedAsync(cutoff, ct);
        if (purged > 0) log.LogInformation("Permanently deleted {Count} task(s) from the trash", purged);
    }

    /// <summary>Today's backup path (local date).</summary>
    public string TodaysBackupPath() =>
        Path.Combine(paths.BackupsDir, $"{Prefix}{clock.Today().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.db");

    /// <summary>Creates today's backup unless it already exists. Returns the path if one was written.</summary>
    public string? BackupIfDue()
    {
        if (!File.Exists(paths.DatabaseFile)) return null;
        var target = TodaysBackupPath();
        if (File.Exists(target)) return null;

        Directory.CreateDirectory(paths.BackupsDir);
        var temp = target + ".tmp";
        using (var source = new SqliteConnection($"Data Source={paths.DatabaseFile};Mode=ReadOnly;Pooling=False"))
        using (var dest = new SqliteConnection($"Data Source={temp};Pooling=False"))
        {
            source.Open();
            dest.Open();
            source.BackupDatabase(dest); // SQLite online backup: consistent even while the app writes
        }
        File.Move(temp, target, overwrite: true);

        var bytes = new FileInfo(target).Length;
        log.LogInformation("Daily backup written to {Path} ({Bytes:N0} bytes)", target, bytes);
        bus.Publish(EventTypes.BackupCreated, new BackupCreatedEvent(target, bytes));
        return target;
    }

    /// <summary>Keeps the newest N daily backups; leaves other files (e.g. pre-migration backups) alone.</summary>
    public int PruneBackups()
    {
        if (!Directory.Exists(paths.BackupsDir)) return 0;
        var keep = options().BackupKeepDays;
        var daily = Directory.GetFiles(paths.BackupsDir, $"{Prefix}????-??-??.db")
            .Where(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f)[Prefix.Length..], "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .OrderDescending(StringComparer.Ordinal)
            .ToList();
        var removed = 0;
        foreach (var old in daily.Skip(keep))
        {
            try
            {
                File.Delete(old);
                removed++;
            }
            catch (IOException ex)
            {
                log.LogWarning(ex, "Could not delete old backup {Path}", old);
            }
        }
        if (removed > 0) log.LogInformation("Removed {Count} old backup(s)", removed);
        return removed;
    }
}
