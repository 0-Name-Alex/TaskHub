using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Contracts;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Domain;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;
using TaskHub.Core.Parsing;
using TaskHub.Core.Scheduling;

namespace TaskHub.Core.Services;

/// <summary>"Leave unchanged" vs "set to this value (possibly null)" for PATCH-style updates.</summary>
public readonly record struct Optional<T>(T Value)
{
    public bool HasValue { get; } = true;

    public static implicit operator Optional<T>(T value) => new(value);
}

public sealed record NewTask
{
    public required string Title { get; init; }
    public string? Notes { get; init; }
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    public Priority Priority { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public Guid? ListId { get; init; }
    /// <summary>Friendly text ("every monday") or an RRULE.</summary>
    public string? Recurrence { get; init; }
    /// <summary>Minutes before due; null = the configured default.</summary>
    public IReadOnlyList<int>? ReminderOffsets { get; init; }
    public int? RepeatReminderMinutes { get; init; }
    /// <summary>Actions to run with the reminder (action syntax, one per line).</summary>
    public string? ReminderActions { get; init; }
    /// <summary>Full paths of attached files.</summary>
    public IReadOnlyList<string>? Attachments { get; init; }
}

public sealed record TaskPatch
{
    public Optional<string> Title { get; init; }
    public Optional<string?> Notes { get; init; }
    public Optional<DateOnly?> DueDate { get; init; }
    public Optional<TimeOnly?> DueTime { get; init; }
    public Optional<Priority> Priority { get; init; }
    public Optional<IReadOnlyList<string>> Tags { get; init; }
    public Optional<Guid?> ListId { get; init; }
    public Optional<string?> Recurrence { get; init; }
    public Optional<IReadOnlyList<int>> ReminderOffsets { get; init; }
    public Optional<int?> RepeatReminderMinutes { get; init; }
    public Optional<string?> ReminderActions { get; init; }
    public Optional<IReadOnlyList<string>> Attachments { get; init; }
}

/// <summary>
/// The only way tasks and lists change. Every mutation is validated, persisted, made undoable
/// and announced on the event bus — so UI, API, CLI, rules and plugins all stay consistent.
/// Writes are serialised so the undo stack always matches the database.
/// </summary>
public sealed class TaskService : IDisposable
{
    private readonly ITaskStore _store;
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly UndoService _undo;
    private readonly ReminderPlanner _reminders;
    private readonly RecurrenceEngine _recurrence;
    private readonly ILogger<TaskService> _log;
    private readonly SemaphoreSlim _write = new(1, 1);

    public TaskService(ITaskStore store, IEventBus bus, IClock clock, UndoService undo,
        ReminderPlanner reminders, RecurrenceEngine recurrence, ILogger<TaskService> log)
    {
        _store = store;
        _bus = bus;
        _clock = clock;
        _undo = undo;
        _reminders = reminders;
        _recurrence = recurrence;
        _log = log;
    }

    public UndoService Undo => _undo;
    public ReminderPlanner Reminders => _reminders;

    // ------------------------------------------------------------------ queries

    public Task<TaskItem?> GetAsync(Guid id, CancellationToken ct = default) => _store.FindAsync(id, ct);

    public async Task<TaskItem> GetRequiredAsync(Guid id, CancellationToken ct = default) =>
        await _store.FindAsync(id, ct) ?? throw new TaskNotFoundException(id);

    public Task<IReadOnlyList<TaskItem>> QueryAsync(TaskQuery query, CancellationToken ct = default) =>
        _store.QueryAsync(query, _clock.Today(), _clock.UtcNow, ct);

    public async Task<IReadOnlyList<TaskListDto>> GetListsAsync(CancellationToken ct = default)
    {
        var lists = await _store.GetListsAsync(ct);
        var (counts, _) = await _store.CountOpenByListAsync(ct);
        return lists.Select(l => l.ToDto(counts.GetValueOrDefault(l.Id))).ToList();
    }

    public async Task<int> CountInboxAsync(CancellationToken ct = default) => (await _store.CountOpenByListAsync(ct)).Inbox;

    public async Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken ct = default) =>
        (await _store.GetTagsAsync(ct)).Select(t => new TagDto(t.Tag, t.Count)).ToList();

    /// <summary>Parses quick-add text without creating anything (live preview in the UI).</summary>
    public QuickAddResult Preview(string text) => QuickAddParser.Parse(text, _clock.LocalNow());

    // ------------------------------------------------------------------ task mutations

    public async Task<TaskItem> CreateAsync(NewTask input, string source = EventSources.Core, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct);
        try
        {
            var now = _clock.UtcNow;
            var task = new TaskItem
            {
                Id = Guid.CreateVersion7(now),
                Title = ValidateTitle(input.Title),
                Notes = ValidateNotes(input.Notes),
                Priority = input.Priority,
                Tags = Domain.Tags.Normalize(input.Tags),
                ListId = await ValidateListAsync(input.ListId, ct),
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            SetDue(task, input.DueDate, input.DueTime);
            task.Recurrence = ValidateRecurrence(input.Recurrence);
            if (task.Recurrence is { } rule && task.DueDate is null)
                SetDue(task, Scheduling.Recurrence.FirstDate(rule, _clock.Today()), task.DueTime);
            task.ReminderOffsets = ValidateOffsets(input.ReminderOffsets ?? _reminders.Options.DefaultOffsets);
            task.RepeatReminderMinutes = ValidateRepeat(input.RepeatReminderMinutes);
            task.ReminderActions = ValidateReminderActions(input.ReminderActions);
            task.Attachments = ValidateAttachments(input.Attachments);
            _reminders.Replan(task, now);

            await _store.InsertAsync(task, ct);
            _undo.Push(new UndoEntry($"Add “{Short(task.Title)}”", [(task.Id, null)], now));
            Publish(EventTypes.TaskCreated, task, source);
            _log.LogInformation("Task {Id} created from {Source}", task.Id, source);
            return task;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Creates a task from quick-add text; <c>@List</c> creates the list if it doesn't exist.</summary>
    public async Task<TaskItem> QuickAddAsync(string text, string source = EventSources.Core, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new TaskValidationException("Task text is empty.");
        var parsed = Preview(text);
        Guid? listId = null;
        if (parsed.ListName is { } name)
            listId = (await _store.FindListByNameAsync(name.Trim(), ct) ?? await CreateListAsync(name, null, source, ct)).Id;

        return await CreateAsync(new NewTask
        {
            Title = parsed.Title,
            DueDate = parsed.DueDate,
            DueTime = parsed.DueTime,
            Priority = parsed.Priority ?? Priority.None,
            Tags = parsed.Tags,
            ListId = listId,
            Recurrence = parsed.Recurrence,
        }, source, ct);
    }

    public Task<TaskItem> UpdateAsync(Guid id, TaskPatch patch, string source = EventSources.Core, CancellationToken ct = default) =>
        MutateAsync(id, "Edit", EventTypes.TaskUpdated, source, async (task, _) =>
        {
            if (patch.Title.HasValue) task.Title = ValidateTitle(patch.Title.Value);
            if (patch.Notes.HasValue) task.Notes = ValidateNotes(patch.Notes.Value);
            if (patch.Priority.HasValue) task.Priority = patch.Priority.Value;
            if (patch.Tags.HasValue) task.Tags = Domain.Tags.Normalize(patch.Tags.Value);
            if (patch.ListId.HasValue) task.ListId = await ValidateListAsync(patch.ListId.Value, ct);
            if (patch.DueDate.HasValue || patch.DueTime.HasValue)
            {
                var date = patch.DueDate.HasValue ? patch.DueDate.Value : task.DueDate;
                var time = patch.DueTime.HasValue ? patch.DueTime.Value : task.DueTime;
                if (patch.DueDate.HasValue && patch.DueDate.Value is null && !patch.DueTime.HasValue)
                    time = null; // clearing the date clears the time too
                SetDue(task, date, time);
            }
            if (patch.Recurrence.HasValue) task.Recurrence = ValidateRecurrence(patch.Recurrence.Value);
            if (task.Recurrence is { } rule && task.DueDate is null)
                SetDue(task, Scheduling.Recurrence.FirstDate(rule, _clock.Today()), task.DueTime);
            if (patch.ReminderOffsets.HasValue) task.ReminderOffsets = ValidateOffsets(patch.ReminderOffsets.Value);
            if (patch.RepeatReminderMinutes.HasValue) task.RepeatReminderMinutes = ValidateRepeat(patch.RepeatReminderMinutes.Value);
            if (patch.ReminderActions.HasValue) task.ReminderActions = ValidateReminderActions(patch.ReminderActions.Value);
            if (patch.Attachments.HasValue) task.Attachments = ValidateAttachments(patch.Attachments.Value);
            return true;
        }, ct);

    public Task<TaskItem> CompleteAsync(Guid id, string source = EventSources.Core, CancellationToken ct = default) =>
        MutateAsync(id, "Complete", EventTypes.TaskCompleted, source, (task, now) =>
        {
            if (task.Status == TaskItemStatus.Completed) return Task.FromResult(false);
            if (_recurrence.TryAdvance(task, out var nextDate, out var nextTime))
            {
                // Recurring: this occurrence is done; the task moves to the next one and stays open.
                SetDue(task, nextDate, nextTime);
                task.LastReminderUtc = null;
                return Task.FromResult(true);
            }
            task.Status = TaskItemStatus.Completed;
            task.CompletedUtc = now;
            return Task.FromResult(true);
        }, ct);

    /// <summary>
    /// Postpones the task's next reminder. Not undoable (it's not a change to the task itself).
    /// </summary>
    public async Task<TaskItem> SnoozeAsync(Guid id, TimeSpan duration, string source = EventSources.Core, CancellationToken ct = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(30))
            throw new TaskValidationException("Snooze must be between 1 minute and 30 days.");
        await _write.WaitAsync(ct);
        try
        {
            var task = await _store.FindAsync(id, ct) ?? throw new TaskNotFoundException(id);
            if (!task.IsOpen || task.IsDeleted) throw new TaskValidationException("Only open tasks can be snoozed.");
            var until = _clock.UtcNow + duration;
            await _store.SetReminderStateAsync(id, until, task.LastReminderUtc, ct);
            task.NextReminderUtc = until;
            _log.LogInformation("Task {Id} snoozed until {Until:u} from {Source}", id, until, source);
            _bus.Publish(EventTypes.ReminderSnoozed, new ReminderSnoozedEvent(task.ToDto(), until), source);
            return task;
        }
        finally
        {
            _write.Release();
        }
    }

    public Task<TaskItem> ReopenAsync(Guid id, string source = EventSources.Core, CancellationToken ct = default) =>
        MutateAsync(id, "Reopen", EventTypes.TaskReopened, source, (task, _) =>
        {
            if (task.Status == TaskItemStatus.Open) return Task.FromResult(false);
            task.Status = TaskItemStatus.Open;
            task.CompletedUtc = null;
            return Task.FromResult(true);
        }, ct);

    /// <summary>Moves the task to the trash (soft delete). Undo or <see cref="RestoreAsync"/> brings it back.</summary>
    public Task<TaskItem> DeleteAsync(Guid id, string source = EventSources.Core, CancellationToken ct = default) =>
        MutateAsync(id, "Delete", EventTypes.TaskDeleted, source, (task, now) =>
        {
            if (task.IsDeleted) return Task.FromResult(false);
            task.DeletedUtc = now;
            return Task.FromResult(true);
        }, ct, allowDeleted: true);

    public Task<TaskItem> RestoreAsync(Guid id, string source = EventSources.Core, CancellationToken ct = default) =>
        MutateAsync(id, "Restore", EventTypes.TaskRestored, source, (task, _) =>
        {
            if (!task.IsDeleted) return Task.FromResult(false);
            task.DeletedUtc = null;
            return Task.FromResult(true);
        }, ct, allowDeleted: true);

    /// <summary>Reverts the most recent action. Returns its description, or null if there is nothing to undo.</summary>
    public async Task<string?> UndoAsync(string source = EventSources.Core, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct);
        try
        {
            if (!_undo.TryPop(out var entry)) return null;
            var now = _clock.UtcNow;

            foreach (var (id, before) in entry.Before)
            {
                var current = await _store.FindAsync(id, ct);
                if (before is null)
                {
                    // The action created this task: undo moves it to the trash.
                    if (current is null || current.IsDeleted) continue;
                    current.DeletedUtc = now;
                    current.UpdatedUtc = now;
                    current.NextReminderUtc = null;
                    await _store.UpdateAsync(current, ct);
                    Publish(EventTypes.TaskDeleted, current, source);
                    continue;
                }

                var restored = before.Clone();
                restored.UpdatedUtc = now;
                _reminders.Replan(restored, now);
                if (current is null) await _store.InsertAsync(restored, ct);
                else await _store.UpdateAsync(restored, ct);
                Publish(current?.IsDeleted == true && !restored.IsDeleted ? EventTypes.TaskRestored : EventTypes.TaskUpdated, restored, source);
            }

            _bus.Publish(EventTypes.UndoApplied, new UndoAppliedEvent(entry.Description), source);
            _log.LogInformation("Undid: {Description}", entry.Description);
            return entry.Description;
        }
        finally
        {
            _write.Release();
        }
    }

    // ------------------------------------------------------------------ lists

    public async Task<TaskList> CreateListAsync(string name, string? color = null, string source = EventSources.Core, CancellationToken ct = default)
    {
        name = ValidateListName(name);
        if (await _store.FindListByNameAsync(name, ct) is not null)
            throw new TaskValidationException($"A list named '{name}' already exists.");

        var lists = await _store.GetListsAsync(ct);
        var list = new TaskList
        {
            Id = Guid.CreateVersion7(_clock.UtcNow),
            Name = name,
            Color = ValidateListColor(color),
            SortOrder = lists.Count == 0 ? 0 : lists.Max(l => l.SortOrder) + 1,
            CreatedUtc = _clock.UtcNow,
        };
        await _store.InsertListAsync(list, ct);
        _bus.Publish(EventTypes.ListCreated, new ListChangedEvent(list.ToDto()), source);
        return list;
    }

    public async Task<TaskList> RenameListAsync(Guid id, string name, string source = EventSources.Core, CancellationToken ct = default)
    {
        name = ValidateListName(name);
        var list = await _store.FindListAsync(id, ct) ?? throw new TaskNotFoundException(id);
        var clash = await _store.FindListByNameAsync(name, ct);
        if (clash is not null && clash.Id != id)
            throw new TaskValidationException($"A list named '{name}' already exists.");
        list.Name = name;
        await _store.UpdateListAsync(list, ct);
        _bus.Publish(EventTypes.ListUpdated, new ListChangedEvent(list.ToDto()), source);
        return list;
    }

    /// <summary>Sets or clears (null/empty) a list's colour. Colours are "#RRGGBB".</summary>
    public async Task<TaskList> SetListColorAsync(Guid id, string? color, string source = EventSources.Core, CancellationToken ct = default)
    {
        var list = await _store.FindListAsync(id, ct) ?? throw new TaskNotFoundException(id);
        var normalized = ValidateListColor(color);
        if (list.Color == normalized) return list;
        list.Color = normalized;
        await _store.UpdateListAsync(list, ct);
        _bus.Publish(EventTypes.ListUpdated, new ListChangedEvent(list.ToDto()), source);
        return list;
    }

    /// <summary>Sets or clears (null/empty) a list's icon (a key from <see cref="ListIcons"/>).</summary>
    public async Task<TaskList> SetListIconAsync(Guid id, string? icon, string source = EventSources.Core, CancellationToken ct = default)
    {
        var list = await _store.FindListAsync(id, ct) ?? throw new TaskNotFoundException(id);
        var normalized = ListIcons.Normalize(icon);
        if (list.Icon == normalized) return list;
        list.Icon = normalized;
        await _store.UpdateListAsync(list, ct);
        _bus.Publish(EventTypes.ListUpdated, new ListChangedEvent(list.ToDto()), source);
        return list;
    }

    /// <summary>Null/empty = no colour; otherwise "#RGB" or "#RRGGBB" (returned as upper-case "#RRGGBB").</summary>
    public static string? ValidateListColor(string? color)
    {
        color = color?.Trim();
        if (string.IsNullOrEmpty(color)) return null;
        var hex = color.TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
            throw new TaskValidationException($"Invalid colour '{color}'. Use e.g. #4F8EF7.");
        return "#" + hex.ToUpperInvariant();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<TaskItem> MutateAsync(
        Guid id, string verb, string eventType, string source,
        Func<TaskItem, DateTimeOffset, Task<bool>> change, CancellationToken ct, bool allowDeleted = false)
    {
        await _write.WaitAsync(ct);
        try
        {
            var task = await _store.FindAsync(id, ct) ?? throw new TaskNotFoundException(id);
            if (task.IsDeleted && !allowDeleted)
                throw new TaskValidationException("The task is in the trash. Restore it first.");

            var before = task.Clone();
            var now = _clock.UtcNow;
            if (!await change(task, now)) return task; // no-op (e.g. completing a completed task)

            if (ReminderPlanner.AffectsReminders(before, task)) _reminders.Replan(task, now);
            task.UpdatedUtc = now;
            await _store.UpdateAsync(task, ct);
            _undo.Push(new UndoEntry($"{verb} “{Short(task.Title)}”", [(id, before)], now));
            Publish(eventType, task, source);
            return task;
        }
        finally
        {
            _write.Release();
        }
    }

    private void SetDue(TaskItem task, DateOnly? date, TimeOnly? time)
    {
        if (time is not null && date is null) date = _clock.Today(); // a bare time means today
        task.DueDate = date;
        task.DueTime = date is null ? null : time;
        task.DueAtUtc = date is { } d && time is { } t ? _clock.ToUtc(d, t) : null;
    }

    private async Task<Guid?> ValidateListAsync(Guid? listId, CancellationToken ct)
    {
        if (listId is null) return null;
        _ = await _store.FindListAsync(listId.Value, ct) ?? throw new TaskValidationException($"List {listId} does not exist.");
        return listId;
    }

    private static string ValidateTitle(string? title)
    {
        title = title?.Trim();
        if (string.IsNullOrEmpty(title)) throw new TaskValidationException("Title is required.");
        if (title.Length > TaskItem.MaxTitleLength) throw new TaskValidationException($"Title is longer than {TaskItem.MaxTitleLength} characters.");
        return title;
    }

    private static string? ValidateNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        if (notes.Length > TaskItem.MaxNotesLength) throw new TaskValidationException($"Notes are longer than {TaskItem.MaxNotesLength} characters.");
        return notes;
    }

    private static string? ValidateRecurrence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        return Scheduling.Recurrence.TryParse(text, out var rrule, out var error) ? rrule : throw new TaskValidationException(error!);
    }

    private static List<int> ValidateOffsets(IEnumerable<int> offsets)
    {
        var list = offsets.Distinct().Order().ToList();
        if (list.Count > 10) throw new TaskValidationException("At most 10 reminders per task.");
        if (list.Any(m => m is < 0 or > 525_600)) throw new TaskValidationException("Reminder offsets must be between 0 minutes and 365 days before.");
        return list;
    }

    private static int? ValidateRepeat(int? minutes) => minutes switch
    {
        null or 0 => null,
        < 1 or > 1440 => throw new TaskValidationException("Repeat-reminder interval must be between 1 and 1440 minutes."),
        _ => minutes,
    };

    private static string? ValidateReminderActions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Length > TaskItem.MaxReminderActionsLength)
            throw new TaskValidationException($"Reminder actions are longer than {TaskItem.MaxReminderActionsLength} characters.");
        if (!Automation.ActionScript.TryParse(text, out _, out var error))
            throw new TaskValidationException($"Reminder actions: {error}");
        return text;
    }

    private static List<string> ValidateAttachments(IEnumerable<string>? paths)
    {
        var list = (paths ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > TaskItem.MaxAttachments) throw new TaskValidationException($"At most {TaskItem.MaxAttachments} attachments per task.");
        if (list.Any(p => p.Length > 1000 || !Path.IsPathFullyQualified(p)))
            throw new TaskValidationException("Attachments must be full file paths.");
        return list;
    }

    private static string ValidateListName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new TaskValidationException("List name is required.");
        if (name.Length > TaskList.MaxNameLength) throw new TaskValidationException($"List name is longer than {TaskList.MaxNameLength} characters.");
        return name;
    }

    private void Publish(string type, TaskItem task, string source) =>
        _bus.Publish(type, new TaskChangedEvent(task.ToDto()), source);

    public void Dispose() => _write.Dispose();

    private static string Short(string title) => title.Length <= 40 ? title : title[..39] + "…";
}
