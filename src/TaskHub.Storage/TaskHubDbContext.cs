using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TaskHub.Core.Domain;

namespace TaskHub.Storage;

public sealed class TaskHubDbContext(DbContextOptions<TaskHubDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<TaskList> Lists => Set<TaskList>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't compare or sort DateTimeOffset. Store every instant as UTC Unix milliseconds
        // (an INTEGER), which sorts and compares correctly. Applies to DateTimeOffset? too.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcMillisecondsConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var b = modelBuilder;
        b.Entity<TaskList>(e =>
        {
            e.ToTable("lists");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(TaskList.MaxNameLength).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Color).HasMaxLength(20);
            e.Property(x => x.Icon).HasMaxLength(32);
        });

        b.Entity<TaskItem>(e =>
        {
            e.ToTable("tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).IsRequired().HasMaxLength(TaskItem.MaxTitleLength);
            e.Property(x => x.Notes).HasMaxLength(TaskItem.MaxNotesLength);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Priority).HasConversion<int>();
            e.PrimitiveCollection(x => x.Tags); // primitive collection → JSON array column, queryable via json_each
            e.PrimitiveCollection(x => x.ReminderOffsets);
            e.Property(x => x.Recurrence).HasMaxLength(500);
            e.Property(x => x.ReminderActions).HasMaxLength(TaskItem.MaxReminderActionsLength);
            e.PrimitiveCollection(x => x.Attachments);
            e.Ignore(x => x.IsOpen);
            e.Ignore(x => x.IsDeleted);
            e.Ignore(x => x.IsRecurring);

            e.HasOne<TaskList>().WithMany().HasForeignKey(x => x.ListId).OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.DeletedUtc, x.Status, x.DueDate });
            e.HasIndex(x => x.ListId);
            e.HasIndex(x => x.DueAtUtc);
            e.HasIndex(x => x.NextReminderUtc);
        });
    }
}

/// <summary>DateTimeOffset ⇄ UTC Unix milliseconds. Order-preserving, so comparisons work in SQL.</summary>
public sealed class UtcMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
    v => v.ToUnixTimeMilliseconds(),
    v => DateTimeOffset.FromUnixTimeMilliseconds(v));

/// <summary>Used only by `dotnet ef migrations add`.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<TaskHubDbContext>
{
    public TaskHubDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TaskHubDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
