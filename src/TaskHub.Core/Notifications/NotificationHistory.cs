using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskHub.Core.Notifications;

public enum NotificationStatus
{
    Shown,
    Queued,
    Silenced,
    Closed,
}

/// <summary>One notification as recorded in the history panel.</summary>
public sealed record NotificationHistoryEntry
{
    public Guid Id { get; init; }
    public DateTimeOffset At { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public NotificationKind Kind { get; init; }
    public NotificationPriority Priority { get; init; }
    public string Source { get; init; } = "";
    public IReadOnlyList<Guid> TaskIds { get; init; } = [];
    public NotificationStatus Status { get; init; }
    /// <summary>What happened: the action used ("done", "snooze:10"), or how it closed ("timeout", "dismissed").</summary>
    public string? Outcome { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
}

/// <summary>
/// The most recent notifications (newest first), kept in memory and saved to a JSON file so the
/// history panel survives restarts. Thread-safe.
/// </summary>
public sealed class NotificationHistory
{
    public const int Capacity = 300;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string? _file;
    private readonly List<NotificationHistoryEntry> _entries = [];
    private readonly Lock _gate = new();
    private Timer? _saveTimer;

    /// <param name="file">Where to persist (null = memory only, e.g. tests).</param>
    public NotificationHistory(string? file = null)
    {
        _file = file;
        Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<NotificationHistoryEntry> Snapshot()
    {
        lock (_gate) return [.. _entries];
    }

    public NotificationHistoryEntry? Find(Guid id)
    {
        lock (_gate) return _entries.FirstOrDefault(e => e.Id == id);
    }

    public void Add(NotificationRequest r, NotificationStatus status, DateTimeOffset at) => Mutate(list =>
    {
        list.RemoveAll(e => e.Id == r.Id);
        list.Insert(0, new NotificationHistoryEntry
        {
            Id = r.Id, At = at, Title = r.Title, Body = r.Body, Kind = r.Kind, Priority = r.Priority,
            Source = r.Source, TaskIds = r.TaskIds, Status = status,
        });
        if (list.Count > Capacity) list.RemoveRange(Capacity, list.Count - Capacity);
    });

    public void Update(Guid id, Func<NotificationHistoryEntry, NotificationHistoryEntry> change) => Mutate(list =>
    {
        var i = list.FindIndex(e => e.Id == id);
        if (i >= 0) list[i] = change(list[i]);
    });

    public void Clear() => Mutate(list => list.Clear());

    private void Mutate(Action<List<NotificationHistoryEntry>> change)
    {
        lock (_gate) change(_entries);
        ScheduleSave();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<NotificationHistoryEntry>>(File.ReadAllText(_file), Json);
            if (loaded is not null) _entries.AddRange(loaded.Take(Capacity));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt history file isn't worth failing over; start fresh.
        }
    }

    private void ScheduleSave()
    {
        if (_file is null) return;
        _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes the file now (also called on shutdown).</summary>
    public void SaveNow()
    {
        if (_file is null) return;
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_entries, Json);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (IOException)
        {
            // try again on the next change
        }
    }
}
