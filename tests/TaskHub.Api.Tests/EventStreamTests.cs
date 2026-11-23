using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Services;

namespace TaskHub.Api.Tests;

public class EventStreamTests
{
    private static async Task<ClientWebSocket> ConnectAsync(ApiHarness api, string query = "", string? token = "valid")
    {
        var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        if (token is not null) ws.Options.SetRequestHeader("Authorization", "Bearer " + (token == "valid" ? api.Token : token));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{api.Server.Port}/v1/events{query}"), timeout.Token);
        return ws;
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket ws, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return JsonDocument.Parse(Encoding.UTF8.GetString(message.ToArray())).RootElement.Clone();
    }

    [Fact]
    public async Task Streams_matching_events_as_json_envelopes()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var ws = await ConnectAsync(api, "?topics=task.created,reminder.*");

        var ready = await ReceiveAsync(ws);
        Assert.Equal("stream.ready", ready.GetProperty("type").GetString());
        Assert.Equal("task.created", ready.GetProperty("data").GetProperty("topics")[0].GetString());

        api.Core.Bus.Publish(EventTypes.ListCreated, null, "test");            // not subscribed
        await api.Core.Tasks.QuickAddAsync("Water plants tomorrow #home", "ui");

        var created = await ReceiveAsync(ws);
        Assert.Equal("task.created", created.GetProperty("type").GetString());
        Assert.Equal("ui", created.GetProperty("source").GetString());
        var task = created.GetProperty("data").GetProperty("task");
        Assert.Equal("Water plants", task.GetProperty("title").GetString());
        Assert.Equal("home", task.GetProperty("tags")[0].GetString());
        Assert.True(created.TryGetProperty("id", out _));
        Assert.True(created.TryGetProperty("time", out _));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Defaults_to_every_event()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var ws = await ConnectAsync(api);
        await ReceiveAsync(ws); // stream.ready

        api.Core.Bus.Publish(EventTypes.SystemLocked, null, "platform");
        var e = await ReceiveAsync(ws);
        Assert.Equal("system.locked", e.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Needs_the_token()
    {
        await using var api = await ApiHarness.CreateAsync();
        var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(new Uri($"ws://127.0.0.1:{api.Server.Port}/v1/events"), CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, ws.HttpStatusCode);
        ws.Dispose();
    }

    [Fact]
    public async Task Rejects_bad_topics_and_plain_http()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();

        var plain = await http.GetAsync("/v1/events");
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Contains("WebSocket", await plain.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var bad = await http.GetAsync("/v1/events?topics=Not%20Valid");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Invalid topic", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
