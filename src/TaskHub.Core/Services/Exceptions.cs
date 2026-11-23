namespace TaskHub.Core.Services;

/// <summary>Bad input from a user or client (API → 400).</summary>
public sealed class TaskValidationException(string message) : Exception(message);

/// <summary>The task or list doesn't exist (API → 404).</summary>
public sealed class TaskNotFoundException(Guid id) : Exception($"Task or list {id} was not found.")
{
    public Guid Id { get; } = id;
}
