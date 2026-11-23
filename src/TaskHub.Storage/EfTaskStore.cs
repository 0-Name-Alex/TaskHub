using Microsoft.EntityFrameworkCore;
using TaskHub.Core.Domain;

namespace TaskHub.Storage;

/// <summary>
/// <see cref="ITaskStore"/> over EF Core + SQLite. A short-lived DbContext per operation keeps
/// it safe to call from the UI, the API and background services at the same time.
/// </summary>
public sealed class EfTaskStore(IDbContextFactory<TaskHubDbContext> factory) : ITaskStore
{
    public async Task<TaskItem?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<IReadOnlyList<TaskItem>> QueryAsync(TaskQuery query, DateOnly today, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Tasks.AsNoTracking();

        q = query.View == TaskView.Trash ? q.Where(t => t.DeletedUtc != null) : q.Where(t => t.DeletedUtc == null);

        const TaskItemStatus open = TaskItemStatus.Open;
        q = query.View switch
        {
            TaskView.Inbox => q.Where(t => t.ListId == null && t.Status == open),
            TaskView.Today => q.Where(t => t.Status == open && t.DueDate != null && t.DueDate <= today),
            TaskView.Upcoming => q.Where(t => t.Status == open && t.DueDate != null && t.DueDate > today),
            TaskView.Overdue => q.Where(t => t.Status == open && t.DueDate != null
                                             && (t.DueDate < today || (t.DueAtUtc != null && t.DueAtUtc < now))),
            TaskView.Completed => q.Where(t => t.Status == TaskItemStatus.Completed),
            TaskView.Trash => q,
            _ => query.IncludeCompleted ? q : q.Where(t => t.Status == open),
        };

        if (query.ListId is { } listId) q = q.Where(t => t.ListId == listId);
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim().TrimStart('#').ToLowerInvariant();
            q = q.Where(t => t.Tags.Contains(tag));
        }
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var pattern = "%" + EscapeLike(query.Text.Trim()) + "%";
            q = q.Where(t => EF.Functions.Like(t.Title, pattern, "\\")
                             || (t.Notes != null && EF.Functions.Like(t.Notes, pattern, "\\")));
        }

        // Sorting happens in memory: the rules (nulls last, open first, …) are clearer in C#,
        // and a personal task list is small.
        var rows = await q.ToListAsync(ct);
        return TaskOrdering.Sort(rows, query.View).Take(query.Limit).ToList();
    }

    public async Task InsertAsync(TaskItem task, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Tasks.Add(task.Clone());
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(TaskItem task, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.Tasks.FirstOrDefaultAsync(t => t.Id == task.Id, ct)
                       ?? throw new InvalidOperationException($"Task {task.Id} does not exist.");
        existing.CopyFrom(task);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TaskList>> GetListsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Lists.AsNoTracking().OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct);
    }

    public async Task<TaskList?> FindListAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
    }

    public async Task<TaskList?> FindListByNameAsync(string name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var trimmed = name.Trim();
        // Name column uses NOCASE collation, so = is case-insensitive.
        return await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.Name == trimmed, ct);
    }

    public async Task InsertListAsync(TaskList list, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Lists.Add(list);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateListAsync(TaskList list, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Lists.Update(list);
        await db.SaveChangesAsync(ct);
    }

    public async Task<(IReadOnlyDictionary<Guid, int> ByList, int Inbox)> CountOpenByListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Tasks.AsNoTracking()
            .Where(t => t.DeletedUtc == null && t.Status == TaskItemStatus.Open)
            .GroupBy(t => t.ListId)
            .Select(g => new { ListId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var byList = rows.Where(r => r.ListId != null).ToDictionary(r => r.ListId!.Value, r => r.Count);
        var inbox = rows.FirstOrDefault(r => r.ListId == null)?.Count ?? 0;
        return (byList, inbox);
    }

    public async Task<IReadOnlyList<(string Tag, int Count)>> GetTagsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var tagLists = await db.Tasks.AsNoTracking()
            .Where(t => t.DeletedUtc == null && t.Status == TaskItemStatus.Open)
            .Select(t => t.Tags)
            .ToListAsync(ct);
        return tagLists.SelectMany(t => t)
            .GroupBy(t => t)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Tasks.AsNoTracking().Where(t => t.DeletedUtc == null).ToListAsync(ct);
        return rows.OrderBy(t => t.CreatedUtc).ToList();
    }

    public async Task<(int ListsAdded, int ListsUpdated, int TasksAdded, int TasksUpdated)> UpsertAsync(
        IReadOnlyList<TaskList> lists, IReadOnlyList<TaskItem> tasks, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int la = 0, lu = 0, ta = 0, tu = 0;

        foreach (var list in lists)
        {
            var existing = await db.Lists.FirstOrDefaultAsync(l => l.Id == list.Id, ct);
            if (existing is null)
            {
                // Same name, different id: keep the existing list's name unique by suffixing.
                var name = list.Name;
                for (var n = 2; await db.Lists.AnyAsync(l => l.Name == name, ct); n++) name = $"{list.Name} ({n})";
                list.Name = name;
                db.Lists.Add(list);
                la++;
            }
            else
            {
                existing.Name = list.Name;
                existing.Color = list.Color;
                existing.Icon = list.Icon;
                existing.SortOrder = list.SortOrder;
                lu++;
            }
            await db.SaveChangesAsync(ct);
        }

        var ids = tasks.Select(t => t.Id).ToList();
        var existingTasks = await db.Tasks.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        foreach (var task in tasks)
        {
            if (existingTasks.TryGetValue(task.Id, out var existing))
            {
                existing.CopyFrom(task);
                tu++;
            }
            else
            {
                db.Tasks.Add(task.Clone());
                ta++;
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (la, lu, ta, tu);
    }

    public async Task<IReadOnlyList<TaskItem>> GetDueRemindersAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Tasks.AsNoTracking()
            .Where(t => t.NextReminderUtc != null && t.NextReminderUtc <= now
                        && t.DeletedUtc == null && t.Status == TaskItemStatus.Open)
            .ToListAsync(ct);
        return rows.OrderBy(t => t.NextReminderUtc).ToList();
    }

    public async Task<DateTimeOffset?> GetNextReminderTimeAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Tasks.AsNoTracking()
            .Where(t => t.NextReminderUtc != null && t.DeletedUtc == null && t.Status == TaskItemStatus.Open)
            .OrderBy(t => t.NextReminderUtc)
            .Select(t => t.NextReminderUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SetReminderStateAsync(Guid id, DateTimeOffset? next, DateTimeOffset? last, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Tasks.Where(t => t.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(t => t.NextReminderUtc, next)
            .SetProperty(t => t.LastReminderUtc, last), ct);
    }

    public async Task<int> PurgeDeletedAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Tasks.Where(t => t.DeletedUtc != null && t.DeletedUtc < cutoff).ExecuteDeleteAsync(ct);
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
