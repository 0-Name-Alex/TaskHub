using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Core.Hosting;

namespace TaskHub.Storage;

/// <summary>
/// Runs at startup before anything else touches the database: backs the file up if a schema
/// upgrade is pending, applies EF Core migrations, and switches SQLite to WAL mode.
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<TaskHubDbContext> factory,
    AppPaths paths,
    IClock clock,
    ILogger<DatabaseInitializer> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            if (File.Exists(paths.DatabaseFile) && new FileInfo(paths.DatabaseFile).Length > 0)
                BackupBeforeMigration(pending);
            log.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(cancellationToken);
        }

        // WAL: readers don't block the writer, and it survives crashes better. Persistent per file.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        log.LogInformation("Database ready at {Path}", paths.DatabaseFile);
    }

    private void BackupBeforeMigration(List<string> pending)
    {
        Directory.CreateDirectory(paths.BackupsDir);
        var stamp = clock.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(paths.BackupsDir, $"taskhub-pre-migration-{stamp}.db");

        // SQLite online backup API: consistent copy even with WAL files present.
        using var source = new SqliteConnection($"Data Source={paths.DatabaseFile};Mode=ReadOnly;Pooling=False");
        using var dest = new SqliteConnection($"Data Source={target};Pooling=False");
        source.Open();
        dest.Open();
        source.BackupDatabase(dest);
        log.LogInformation("Backed up database to {Target} before applying {Migrations}", target, string.Join(", ", pending));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
