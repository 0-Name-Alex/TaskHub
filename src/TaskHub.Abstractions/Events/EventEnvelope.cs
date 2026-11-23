namespace TaskHub.Abstractions.Events;

/// <summary>
/// Every event on the bus uses this envelope. It is also the exact JSON shape sent
/// to external clients over the WebSocket API.
/// </summary>
/// <param name="Id">Time-ordered unique id (Guid v7).</param>
/// <param name="Type">Dotted lowercase name, e.g. <c>task.completed</c>.</param>
/// <param name="Time">When the event was published (UTC).</param>
/// <param name="Source">Who caused it: <c>ui</c>, <c>core</c>, <c>api:greeter</c>, <c>plugin:telegram</c>, <c>rule:42</c> …</param>
/// <param name="Data">Event payload (a record from this namespace), or null.</param>
public sealed record EventEnvelope(Guid Id, string Type, DateTimeOffset Time, string Source, object? Data)
{
    public static EventEnvelope Create(string type, object? data, string source, DateTimeOffset time) =>
        new(Guid.CreateVersion7(time), type, time, source, data);

    /// <summary>Returns the payload as <typeparamref name="T"/>, or default if it is another type.</summary>
    public T? DataAs<T>() => Data is T t ? t : default;
}
