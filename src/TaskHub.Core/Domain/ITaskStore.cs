namespace TaskHub.Core.Domain;

/// <summary>Persistence for tasks and lists. Implemented by TaskHub.Storage (EF Core + SQLite).</summary>
public interface ITaskStore
{
    Task<TaskItem?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns matching tasks, sorted with <see cref="TaskOrdering"/>.
    /// <paramref name="today"/> and <paramref name="now"/> define what "today" and "overdue" mean.
    /// </summary>
    Task<IReadOnlyList<TaskItem>> QueryAsync(TaskQuery query, DateOnly today, DateTimeOffset now, CancellationToken ct = default);

    Task InsertAsync(TaskItem task, CancellationToken ct = default);

    /// <summary>Replaces every stored value of the task with the same id.</summary>
    Task UpdateAsync(TaskItem task, CancellationToken ct = default);

    Task<IReadOnlyList<TaskList>> GetListsAsync(CancellationToken ct = default);
    Task<TaskList?> FindListAsync(Guid id, CancellationToken ct = default);
    Task<TaskList?> FindListByNameAsync(string name, CancellationToken ct = default);
    Task InsertListAsync(TaskList list, CancellationToken ct = default);
    Task UpdateListAsync(TaskList list, CancellationToken ct = default);

    /// <summary>Open, non-deleted task counts per list, plus the Inbox (no list) count.</summary>
    Task<(IReadOnlyDictionary<Guid, int> ByList, int Inbox)> CountOpenByListAsync(CancellationToken ct = default);

    /// <summary>Tags used by open, non-deleted tasks, with counts.</summary>
    Task<IReadOnlyList<(string Tag, int Count)>> GetTagsAsync(CancellationToken ct = default);

    /// <summary>All non-deleted tasks (export).</summary>
    Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Inserts or replaces lists and tasks by id, in one transaction.</summary>
    Task<(int ListsAdded, int ListsUpdated, int TasksAdded, int TasksUpdated)> UpsertAsync(
        IReadOnlyList<TaskList> lists, IReadOnlyList<TaskItem> tasks, CancellationToken ct = default);

    /// <summary>Open, non-deleted tasks whose next reminder is at or before <paramref name="now"/>.</summary>
    Task<IReadOnlyList<TaskItem>> GetDueRemindersAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>The earliest scheduled reminder among open, non-deleted tasks.</summary>
    Task<DateTimeOffset?> GetNextReminderTimeAsync(CancellationToken ct = default);

    /// <summary>Updates only the reminder bookkeeping columns (doesn't touch the rest of the task).</summary>
    Task SetReminderStateAsync(Guid id, DateTimeOffset? next, DateTimeOffset? last, CancellationToken ct = default);

    /// <summary>Permanently removes tasks deleted before <paramref name="cutoff"/>.</summary>
    Task<int> PurgeDeletedAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
