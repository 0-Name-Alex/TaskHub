using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskHub.Core.Domain;
using TaskHub.Core.Hosting;

namespace TaskHub.Storage;

public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers SQLite storage. Call before services that use the database at startup
    /// (hosted services start in registration order).
    /// </summary>
    public static IServiceCollection AddTaskHubStorage(this IServiceCollection services, AppPaths paths) =>
        services.AddTaskHubStorage(ConnectionStringFor(paths.DatabaseFile));

    public static IServiceCollection AddTaskHubStorage(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<TaskHubDbContext>(o => o.UseSqlite(connectionString));
        services.AddSingleton<ITaskStore, EfTaskStore>();
        services.AddHostedService<DatabaseInitializer>();
        return services;
    }

    /// <summary>Daily backup + trash purge. Needs a <c>Func&lt;MaintenanceOptions&gt;</c>.</summary>
    public static IServiceCollection AddTaskHubMaintenance(this IServiceCollection services)
    {
        services.AddSingleton<MaintenanceService>();
        services.AddHostedService(sp => sp.GetRequiredService<MaintenanceService>());
        return services;
    }

    public static string ConnectionStringFor(string databaseFile) => new SqliteConnectionStringBuilder
    {
        DataSource = databaseFile,
        Pooling = true,
        DefaultTimeout = 10, // seconds to wait on a locked database
    }.ToString();
}
