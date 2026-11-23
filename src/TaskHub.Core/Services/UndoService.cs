using TaskHub.Core.Domain;

namespace TaskHub.Core.Services;

/// <summary>
/// One undoable action. <see cref="Before"/> holds each affected task as it was before the
/// action; a null snapshot means the action created that task.
/// </summary>
public sealed record UndoEntry(string Description, IReadOnlyList<(Guid Id, TaskItem? Before)> Before, DateTimeOffset At);

/// <summary>Bounded in-memory undo stack (most recent first). Shared by UI, API and CLI.</summary>
public sealed class UndoService
{
    public const int Capacity = 100;

    private readonly LinkedList<UndoEntry> _stack = new();
    private readonly Lock _gate = new();

    public event EventHandler? Changed;

    public int Count
    {
        get { lock (_gate) return _stack.Count; }
    }

    /// <summary>Description of what Undo would revert, or null.</summary>
    public string? Peek()
    {
        lock (_gate) return _stack.First?.Value.Description;
    }

    public void Push(UndoEntry entry)
    {
        lock (_gate)
        {
            _stack.AddFirst(entry);
            while (_stack.Count > Capacity) _stack.RemoveLast();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TryPop(out UndoEntry entry)
    {
        lock (_gate)
        {
            if (_stack.First is null)
            {
                entry = null!;
                return false;
            }
            entry = _stack.First.Value;
            _stack.RemoveFirst();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
