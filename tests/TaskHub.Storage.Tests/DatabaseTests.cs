using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskHub.Core.Domain;

namespace TaskHub.Storage.Tests;

public class DatabaseTests
{
    [Fact]
    public async Task Migrations_cover_the_whole_model()
    {
        // Fails if someone changed the model without adding a migration (run new-migration.cmd).
        await using var h = await Harness.CreateAsync();
        await using var db = await h.Db.CreateDbContextAsync();
        Assert.False(db.Database.HasPendingModelChanges(), "The EF model has changes that aren't in a migration.");
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Database_uses_wal_mode()
    {
        await using var h = await Harness.CreateAsync();
        await using var conn = new SqliteConnection($"Data Source={h.Paths.DatabaseFile}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Data_survives_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "taskhub-tests", Guid.NewGuid().ToString("N"));
        Guid id;
        await using (var first = await Harness.CreateAsync(root))
        {
            id = (await first.Tasks.QuickAddAsync("persist me tomorrow 9am #keep @Box")).Id;
            first.KeepFiles = true;
        }

        await using var second = await Harness.CreateAsync(root);
        var task = await second.Tasks.GetRequiredAsync(id);
        Assert.Equal("persist me", task.Title);
        Assert.Equal(["keep"], task.Tags);
        Assert.Equal(new TimeOnly(9, 0), task.DueTime);
        Assert.NotNull(task.ListId);
    }

    [Fact]
    public async Task Instants_are_stored_as_sortable_utc_milliseconds()
    {
        await using var h = await Harness.CreateAsync();
        await h.Tasks.QuickAddAsync("a today 9am");
        await using var conn = new SqliteConnection($"Data Source={h.Paths.DatabaseFile}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DueAtUtc FROM tasks";
        var raw = await cmd.ExecuteScalarAsync();
        // 9:00 IST on 2026-11-02 = 03:30 UTC
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 3, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Pending_migration_on_existing_database_triggers_a_backup()
    {
        var root = Path.Combine(Path.GetTempPath(), "taskhub-tests", Guid.NewGuid().ToString("N"));
        var paths = Core.Hosting.AppPaths.UnderRoot(root);
        paths.EnsureCreated();
        // An existing (non-empty) database with no migrations applied yet.
        await using (var conn = new SqliteConnection($"Data Source={paths.DatabaseFile};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE legacy(x INTEGER); INSERT INTO legacy VALUES (1);";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var h = await Harness.CreateAsync(root);
        Assert.Contains(Directory.GetFiles(paths.BackupsDir), f => Path.GetFileName(f).StartsWith("taskhub-pre-migration-", StringComparison.Ordinal));
    }
}
