using System.Text.Json;
using System.Text.Json.Nodes;
using TaskHub.Cli;
using TaskHub.Cli.Mcp;

namespace TaskHub.Api.Tests;

/// <summary>Drives `th mcp` with JSON-RPC lines, against a real API server.</summary>
public class McpTests
{
    private static McpServer Server(ApiHarness api, bool readOnly = false) =>
        new(() => ApiClient.Create(api.BaseUrl, api.Token, "mcp"), TextReader.Null, TextWriter.Null, TextWriter.Null, readOnly, "test");

    private static int _nextId;

    private static async Task<JsonObject> Request(McpServer server, string method, object? parameters = null)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _nextId),
            ["method"] = method,
        };
        if (parameters is not null) request["params"] = JsonSerializer.SerializeToNode(parameters);
        var response = await server.HandleAsync(request.ToJsonString());
        Assert.NotNull(response);
        Assert.Equal(request["id"]!.GetValue<int>(), response!["id"]!.GetValue<int>());
        return response;
    }

    private static async Task<(string Text, bool IsError)> Call(McpServer server, string tool, object? arguments = null)
    {
        var response = await Request(server, "tools/call", new { name = tool, arguments = arguments ?? new { } });
        var result = response["result"]!.AsObject();
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Initialize_and_list_tools()
    {
        await using var api = await ApiHarness.CreateAsync();
        var server = Server(api);

        var init = await Request(server, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
        Assert.Equal("2025-06-18", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("taskhub", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(init["result"]!["capabilities"]!["tools"]);

        // Unknown protocol versions get our latest; notifications get no reply.
        var old = await Request(server, "initialize", new { protocolVersion = "1999-01-01" });
        Assert.Equal(McpServer.LatestProtocol, old["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Null(await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

        var tools = (await Request(server, "tools/list"))["result"]!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("add_task", names);
        Assert.Contains("list_tasks", names);
        Assert.Contains("complete_task", names);
        Assert.All(tools, t => Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>()));
        var add = tools.Single(t => t!["name"]!.GetValue<string>() == "add_task")!;
        Assert.Equal("text", add["inputSchema"]!["required"]![0]!.GetValue<string>());
        Assert.False(add["annotations"]!["readOnlyHint"]!.GetValue<bool>());

        var readOnly = Server(api, readOnly: true);
        var readNames = (await Request(readOnly, "tools/list"))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("list_tasks", readNames);
        Assert.DoesNotContain("add_task", readNames);
        var blocked = await Call(readOnly, "add_task", new { text = "x" });
        Assert.True(blocked.IsError);
        Assert.Contains("read-only", blocked.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_list_update_complete_and_undo_through_tools()
    {
        await using var api = await ApiHarness.CreateAsync();
        var server = Server(api);

        var status = await Call(server, "get_status");
        Assert.False(status.IsError, status.Text);
        Assert.Contains("\"today\":\"2026-11-02\"", status.Text, StringComparison.Ordinal);

        var added = await Call(server, "add_task", new { text = "Pay rent tomorrow 9am #home", priority = "high", list = "Finance", remind_minutes_before = new[] { 0, 30 } });
        Assert.False(added.IsError, added.Text);
        Assert.Contains("Pay rent", added.Text, StringComparison.Ordinal);
        Assert.Contains("\"priority\":\"high\"", added.Text, StringComparison.Ordinal);
        Assert.Contains("\"list\":\"Finance\"", added.Text, StringComparison.Ordinal);

        var task = (await api.Core.Query()).Single();
        Assert.Equal([0, 30], task.ReminderOffsets);
        var shortId = CliApp.ShortId(task.Id);

        var listed = await Call(server, "list_tasks", new { view = "upcoming" });
        Assert.Contains(shortId, listed.Text, StringComparison.Ordinal);
        Assert.Contains("\"count\":1", listed.Text, StringComparison.Ordinal);

        var updated = await Call(server, "update_task", new { id = shortId, due_time = "none", tags = new[] { "bills" }, notes = "Transfer from savings" });
        Assert.False(updated.IsError, updated.Text);
        task = (await api.Core.Query()).Single();
        Assert.Null(task.DueTime);
        Assert.Equal(["bills"], task.Tags);

        var details = await Call(server, "get_task", new { id = shortId });
        Assert.Contains("Transfer from savings", details.Text, StringComparison.Ordinal);

        var done = await Call(server, "complete_task", new { id = shortId });
        Assert.False(done.IsError, done.Text);
        Assert.Contains("Completed", done.Text, StringComparison.Ordinal);
        Assert.Empty(await api.Core.Query());

        var undo = await Call(server, "undo");
        Assert.Contains("Undid", undo.Text, StringComparison.Ordinal);
        Assert.Single(await api.Core.Query());

        var e = await api.Core.WaitForEventAsync(Abstractions.Events.EventTypes.TaskCreated);
        Assert.Equal("api:mcp", e.Source);
    }

    [Fact]
    public async Task Problems_come_back_as_tool_errors()
    {
        await using var api = await ApiHarness.CreateAsync();
        var server = Server(api);

        Assert.True((await Call(server, "complete_task", new { id = "zzzz" })).IsError);
        Assert.Contains("No task", (await Call(server, "complete_task", new { id = "abcd1234" })).Text, StringComparison.Ordinal);
        Assert.Contains("required", (await Call(server, "add_task", new { })).Text, StringComparison.Ordinal);
        Assert.True((await Call(server, "add_task", new { text = "x", due_date = "next tuesday" })).IsError);
        Assert.True((await Call(server, "no_such_tool")).IsError);
        Assert.Contains("no list named", (await Call(server, "list_tasks", new { list = "Nope" })).Text, StringComparison.OrdinalIgnoreCase);

        var unknownMethod = await Request(server, "does/not/exist");
        Assert.Equal(-32601, unknownMethod["error"]!["code"]!.GetValue<int>());
        var parse = await server.HandleAsync("{not json");
        Assert.Equal(-32700, parse!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Says_so_when_TaskHub_is_not_running()
    {
        var server = new McpServer(() => throw new CliException("TaskHub doesn't seem to be running."), TextReader.Null, TextWriter.Null, TextWriter.Null, false);
        var response = await server.HandleAsync("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"list_tasks","arguments":{}}}""");
        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("running", response["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serves_newline_delimited_messages_over_streams()
    {
        await using var api = await ApiHarness.CreateAsync();
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""" + "\n" +
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n\n" +
            """{"jsonrpc":"2.0","id":"two","method":"ping"}""" + "\n");
        var output = new StringWriter();
        var server = new McpServer(() => ApiClient.Create(api.BaseUrl, api.Token), input, output, TextWriter.Null, false);

        await server.RunAsync();

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(1, JsonNode.Parse(lines[0])!["id"]!.GetValue<int>());
        Assert.Equal("two", JsonNode.Parse(lines[1])!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Th_mcp_command_runs_the_server()
    {
        await using var api = await ApiHarness.CreateAsync();
        var input = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""" + "\n");
        var output = new StringWriter();
        var cli = new CliApp(() => ApiClient.Create(api.BaseUrl, api.Token), output, TextWriter.Null, input);

        Assert.Equal(0, await cli.RunAsync(["mcp", "--read-only"]));
        var tools = JsonNode.Parse(output.ToString().Trim())!["result"]!["tools"]!.AsArray();
        Assert.DoesNotContain(tools, t => t!["name"]!.GetValue<string>() == "delete_task");
    }
}
