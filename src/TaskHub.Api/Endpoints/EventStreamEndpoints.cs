using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Contracts;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;

namespace TaskHub.Api.Endpoints;

/// <summary>
/// <c>GET /v1/events?topics=task.*,reminder.due</c> as a WebSocket: every matching event on the bus is
/// sent as one JSON text message in the <see cref="EventEnvelope"/> shape
/// (<c>{ id, type, time, source, data }</c>). The first message is <c>stream.ready</c>. Same token,
/// Host and Origin checks as the rest of the API (so browsers can't connect). A client that falls far
/// behind loses its oldest unsent events rather than slowing TaskHub down.
/// </summary>
internal static class EventStreamEndpoints
{
    public const int QueueLimit = 1000;
    public const int MaxConnections = 32;
    private static int _connections;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/events", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(HttpContext http, [FromServices] IEventBus bus, [FromServices] IClock clock,
        [FromServices] IHostApplicationLifetime lifetime, [FromServices] ILoggerFactory loggers, [FromQuery] string? topics)
    {
        var patterns = (topics ?? "*").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0) patterns = ["*"];
        foreach (var p in patterns)
        {
            if (!TopicPattern.IsValidPattern(p))
                return Results.BadRequest(new ApiError($"Invalid topic '{p}'. Use *, an event type like task.created, or a prefix like task.*"));
        }
        if (!http.WebSockets.IsWebSocketRequest)
            return Results.BadRequest(new ApiError("This endpoint needs a WebSocket connection: ws://127.0.0.1:<port>/v1/events?topics=task.*"));
        if (Interlocked.Increment(ref _connections) > MaxConnections)
        {
            Interlocked.Decrement(ref _connections);
            return Results.Json(new ApiError("Too many event streams are open."), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var log = loggers.CreateLogger("TaskHub.Api.Events");
        try
        {
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
            var queue = Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(QueueLimit)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            using var subscription = bus.Subscribe("*", e =>
            {
                foreach (var p in patterns)
                {
                    if (TopicPattern.Matches(p, e.Type))
                    {
                        queue.Writer.TryWrite(e);
                        return;
                    }
                }
            });
            log.LogInformation("Event stream opened ({Topics})", string.Join(",", patterns));

            // Watch for the client closing (we never expect messages from it).
            var receiving = ReceiveUntilClosedAsync(socket, cts);

            await SendAsync(socket, EventEnvelope.Create("stream.ready", new { topics = patterns }, EventSources.Api, clock.UtcNow), cts.Token);
            try
            {
                await foreach (var e in queue.Reader.ReadAllAsync(cts.Token))
                    await SendAsync(socket, e, cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // client went away or TaskHub is stopping
            }

            await cts.CancelAsync();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    var reason = lifetime.ApplicationStopping.IsCancellationRequested ? "TaskHub is closing" : "bye";
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, closeTimeout.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
                {
                    // best effort
                }
            }
            try { await receiving; } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            log.LogInformation("Event stream closed");
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
        }
        return Results.Empty;
    }

    private static async Task SendAsync(WebSocket socket, EventEnvelope e, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(e, ApiJson.Options);
        }
        catch (NotSupportedException)
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(e with { Data = null }, ApiJson.Options);
        }
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task ReceiveUntilClosedAsync(WebSocket socket, CancellationTokenSource cts)
    {
        var buffer = new byte[1024];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        finally
        {
            await cts.CancelAsync();
        }
    }
}
