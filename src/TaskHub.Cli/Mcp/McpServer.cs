using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskHub.Abstractions.Contracts;

namespace TaskHub.Cli.Mcp;

/// <summary>
/// A Model Context Protocol server over stdio (<c>th mcp</c>), so AI assistants such as Claude Desktop
/// can read and change your tasks. It speaks JSON-RPC 2.0, one message per line, and turns tool calls
/// into calls to the running TaskHub's local API — so everything an assistant does has the same
/// validation, undo, events and notifications as the app itself. Nothing but protocol messages is
/// written to stdout; diagnostics go to stderr.
/// </summary>
public sealed class McpServer
{
    public const string LatestProtocol = "2025-06-18";
    private static readonly string[] SupportedProtocols = [LatestProtocol, "2025-03-26", "2024-11-05"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Func<ApiClient> _clientFactory;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _log;
    private readonly bool _readOnly;
    private readonly string _version;
    private readonly List<Tool> _tools;
    private ApiClient? _client;

    public McpServer(Func<ApiClient> clientFactory, TextReader input, TextWriter output, TextWriter log, bool readOnly, string version = "0.0.0")
    {
        _clientFactory = clientFactory;
        _input = input;
        _output = output;
        _log = log;
        _readOnly = readOnly;
        _version = version;
        _tools = BuildTools();
    }

    public IReadOnlyList<string> ToolNames => _tools.Where(t => !_readOnly || t.ReadOnly).Select(t => t.Name).ToList();

    /// <summary>Serves until stdin closes.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        await _log.WriteLineAsync($"TaskHub MCP server {_version} ready ({(_readOnly ? "read-only" : "read and write")}).");
        while (await _input.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? response;
            try
            {
                response = await HandleAsync(line, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _log.WriteLineAsync("Unexpected error: " + ex);
                response = Error(null, -32603, "Internal error: " + ex.Message);
            }
            if (response is null) continue;
            await _output.WriteLineAsync(response.ToJsonString());
            await _output.FlushAsync(ct);
        }
        _client?.Dispose();
    }

    /// <summary>Handles one incoming line. Returns the response, or null for notifications.</summary>
    public async Task<JsonObject?> HandleAsync(string line, CancellationToken ct = default)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            return Error(null, -32700, "Parse error: " + ex.Message);
        }
        if (message is not JsonObject request)
            return Error(null, -32600, "Invalid request: expected a JSON object (batches aren't supported).");

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValueKind() == JsonValueKind.String ? request["method"]!.GetValue<string>() : null;
        if (method is null) return null; // a response to something we never send, or junk: ignore
        var isNotification = !request.ContainsKey("id");
        var parameters = request["params"] as JsonObject ?? new JsonObject();

        if (isNotification) return null; // notifications/initialized, notifications/cancelled …

        switch (method)
        {
            case "initialize":
                return Result(id, Initialize(parameters));
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = new JsonArray(_tools.Where(t => !_readOnly || t.ReadOnly).Select(t => (JsonNode)t.Describe()).ToArray()) });
            case "tools/call":
                return Result(id, await CallToolAsync(parameters, ct));
            case "resources/list":
                return Result(id, new JsonObject { ["resources"] = new JsonArray() });
            case "prompts/list":
                return Result(id, new JsonObject { ["prompts"] = new JsonArray() });
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private JsonObject Initialize(JsonObject p)
    {
        var requested = p["protocolVersion"]?.GetValueKind() == JsonValueKind.String ? p["protocolVersion"]!.GetValue<string>() : null;
        var version = requested is not null && SupportedProtocols.Contains(requested) ? requested : LatestProtocol;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "taskhub", ["title"] = "TaskHub", ["version"] = _version },
            ["instructions"] =
                "TaskHub is the user's personal to-do and reminder app on this PC. Dates and times are local; " +
                "call get_status for today's date before working out relative dates. Task ids are short 8-character ids " +
                "(full ids work too). add_task accepts quick-add text such as \"Pay rent tomorrow 9am #home !high @Finance\". " +
                "Deleting moves a task to the trash (restore_task brings it back) and undo reverts the last change." +
                (_readOnly ? " This connection is read-only." : ""),
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject p, CancellationToken ct)
    {
        var name = p["name"]?.GetValueKind() == JsonValueKind.String ? p["name"]!.GetValue<string>() : "";
        var args = p["arguments"] as JsonObject ?? new JsonObject();
        var tool = _tools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return ToolError($"Unknown tool '{name}'.");
        if (_readOnly && !tool.ReadOnly) return ToolError($"'{name}' changes tasks, and this TaskHub connection is read-only.");

        try
        {
            var result = await tool.Run(new Args(args), ct);
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result }),
                ["isError"] = false,
            };
        }
        catch (CliException ex)
        {
            // Can't reach TaskHub: forget the client so the next call re-reads api.json (TaskHub may have restarted).
            if (ex.Message.Contains("running", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("reach", StringComparison.OrdinalIgnoreCase))
            {
                _client?.Dispose();
                _client = null;
            }
            return ToolError(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ToolError(ex.Message);
        }
    }

    private ApiClient Client => _client ??= _clientFactory();

    // ------------------------------------------------------------------ tools

    private sealed record Tool(string Name, string Title, string Description, bool ReadOnly, bool Destructive, JsonObject Schema,
        Func<Args, CancellationToken, Task<string>> Run)
    {
        public JsonObject Describe() => new()
        {
            ["name"] = Name,
            ["title"] = Title,
            ["description"] = Description,
            ["inputSchema"] = Schema.DeepClone(),
            ["annotations"] = new JsonObject
            {
                ["title"] = Title,
                ["readOnlyHint"] = ReadOnly,
                ["destructiveHint"] = Destructive,
                ["openWorldHint"] = false,
            },
        };
    }

    private List<Tool> BuildTools()
    {
        var id = Prop("id", "string", "Task id (the 8-character short id from list_tasks, or the full id)");
        return
        [
            new("get_status", "TaskHub status", "Today's date, the current local time, weekday and time zone, and whether TaskHub is running.",
                true, false, Schema(), GetStatusAsync),

            new("list_tasks", "List tasks",
                "List tasks. Views: today (due today or earlier, open), upcoming, overdue, inbox (no list), all (open; add include_completed), completed, trash. " +
                "Filter by search text, tag or list name. Returns id, title, due, priority, list, tags, repeat and reminder info.",
                true, false,
                Schema(
                    Prop("view", "string", "Which tasks (default: all)", ["today", "upcoming", "overdue", "inbox", "all", "completed", "trash"]),
                    Prop("search", "string", "Text to find in title or notes"),
                    Prop("tag", "string", "Only tasks with this tag (without #)"),
                    Prop("list", "string", "Only tasks in this list (name)"),
                    Prop("include_completed", "boolean", "Include completed tasks (view 'all' only)"),
                    Prop("limit", "integer", "Maximum number of tasks (default 50, max 200)")),
                ListTasksAsync),

            new("get_task", "Get a task", "All details of one task, including notes.", true, false, Schema(Required(id)), GetTaskAsync),

            new("list_lists", "List lists", "The user's lists with open-task counts.", true, false, Schema(), ListListsAsync),

            new("list_tags", "List tags", "Tags in use on open tasks, with counts.", true, false, Schema(), ListTagsAsync),

            new("add_task", "Add a task",
                "Add a task. 'text' uses TaskHub quick-add syntax: dates (today, tomorrow, fri, next week, 20 nov, in 3 days), " +
                "times (9am, 18:30, evening), #tag, @List (created if missing), !low/!med/!high/!urgent, repeats (daily, weekdays, every monday, every 2 weeks). " +
                "Example: \"Pay rent tomorrow 9am #home !high @Finance\". Explicit fields override what the text says. " +
                "Tasks with a due time remind at that time by default.",
                false, false,
                Schema(
                    Required(Prop("text", "string", "The task in quick-add syntax (or just a title)")),
                    Prop("notes", "string", "Longer notes"),
                    Prop("due_date", "string", "Due date yyyy-MM-dd"),
                    Prop("due_time", "string", "Due time HH:mm (24-hour, local)"),
                    Prop("priority", "string", "Priority", ["none", "low", "medium", "high", "urgent"]),
                    ArrayProp("tags", "string", "Tags (without #)"),
                    Prop("list", "string", "List name (created if missing)"),
                    Prop("repeat", "string", "Repeat rule, e.g. 'every monday', 'weekdays', 'every 2 weeks', 'monthly'"),
                    ArrayProp("remind_minutes_before", "integer", "Reminders, in minutes before the due time (0 = at due). Omit for the user's default; [] for none."),
                    Prop("nag_every_minutes", "integer", "After the due time, keep reminding every N minutes until done")),
                AddTaskAsync),

            new("update_task", "Change a task",
                "Change fields of a task; only the fields you give change. Use \"none\" to clear due_date, due_time, notes or repeat, " +
                "and list \"inbox\" to remove it from its list.",
                false, false,
                Schema(
                    Required(id),
                    Prop("title", "string", "New title"),
                    Prop("notes", "string", "New notes, or \"none\""),
                    Prop("due_date", "string", "yyyy-MM-dd, or \"none\""),
                    Prop("due_time", "string", "HH:mm, or \"none\""),
                    Prop("priority", "string", "Priority", ["none", "low", "medium", "high", "urgent"]),
                    ArrayProp("tags", "string", "Replaces all tags"),
                    Prop("list", "string", "Move to this list (name), or \"inbox\""),
                    Prop("repeat", "string", "Repeat rule, or \"none\""),
                    ArrayProp("remind_minutes_before", "integer", "Replaces the reminders ([] = none)"),
                    Prop("nag_every_minutes", "integer", "Remind every N minutes after due until done (0 = off)")),
                UpdateTaskAsync),

            new("complete_task", "Complete a task",
                "Mark a task done. A repeating task moves to its next occurrence instead.", false, false, Schema(Required(id)),
                (a, ct) => TaskActionAsync(a, "Completed", (c, i, t) => c.CompleteAsync(i, t), ct)),

            new("reopen_task", "Reopen a task", "Mark a completed task as not done.", false, false, Schema(Required(id)),
                (a, ct) => TaskActionAsync(a, "Reopened", (c, i, t) => c.ReopenAsync(i, t), ct)),

            new("delete_task", "Delete a task", "Move a task to the trash (restore_task or undo brings it back).", false, true, Schema(Required(id)),
                (a, ct) => TaskActionAsync(a, "Moved to the trash", (c, i, t) => c.DeleteAsync(i, t), ct)),

            new("restore_task", "Restore a task", "Bring a task back from the trash.", false, false, Schema(Required(id)),
                (a, ct) => TaskActionAsync(a, "Restored", (c, i, t) => c.RestoreAsync(i, t), ct, includeTrash: true)),

            new("snooze_task", "Snooze a reminder", "Postpone a task's next reminder.", false, false,
                Schema(Required(id), Prop("minutes", "integer", "How long (default: the user's snooze setting)")),
                SnoozeAsync),

            new("undo", "Undo", "Undo the most recent change to tasks (made from any app, including this one).", false, false, Schema(), UndoAsync),

            new("notify", "Show a notification", "Show a TaskHub banner on the user's screen.", false, false,
                Schema(
                    Required(Prop("title", "string", "Banner title")),
                    Prop("body", "string", "Second line"),
                    Prop("priority", "string", "Priority", ["low", "normal", "high", "urgent"])),
                NotifyAsync),

            new("set_do_not_disturb", "Do Not Disturb", "Turn TaskHub's Do Not Disturb on or off.", false, false,
                Schema(Required(Prop("enabled", "boolean", "true = on"))),
                SetDndAsync),
        ];
    }

    private async Task<string> GetStatusAsync(Args a, CancellationToken ct)
    {
        var s = await Client.StatusAsync(ct);
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Local; }
        var local = TimeZoneInfo.ConvertTime(s.Now, zone);
        return Serialize(new
        {
            running = true,
            version = s.Version,
            today = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            weekday = local.DayOfWeek.ToString(),
            time = local.ToString("HH:mm", CultureInfo.InvariantCulture),
            timeZone = s.TimeZone,
            utcOffset = local.Offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
        });
    }

    private async Task<string> ListTasksAsync(Args a, CancellationToken ct)
    {
        var limit = Math.Clamp(a.Int("limit") ?? 50, 1, 200);
        Guid? listId = a.Str("list") is { } listName ? await ResolveListAsync(listName, create: false, ct) : null;
        var tasks = await Client.ListTasksAsync(a.Str("view") ?? "all", a.Str("search"), a.Str("tag")?.TrimStart('#'), listId,
            a.Bool("include_completed") ?? false, limit + 1, ct);
        var lists = await ListNamesAsync(ct);
        var shown = tasks.Take(limit).Select(t => Compact(t, lists, includeNotes: false)).ToList();
        return Serialize(new { count = shown.Count, more = tasks.Count > limit, tasks = shown });
    }

    private async Task<string> GetTaskAsync(Args a, CancellationToken ct)
    {
        var task = await Client.GetTaskAsync(await ResolveIdAsync(a.Required("id"), includeTrash: true, ct), ct);
        return Serialize(Compact(task, await ListNamesAsync(ct), includeNotes: true));
    }

    private async Task<string> ListListsAsync(Args a, CancellationToken ct) =>
        Serialize((await Client.ListListsAsync(ct)).Select(l => new { name = l.Name, open = l.OpenCount }));

    private async Task<string> ListTagsAsync(Args a, CancellationToken ct) =>
        Serialize((await Client.ListTagsAsync(ct)).Select(t => new { tag = t.Name, open = t.OpenCount }));

    private async Task<string> AddTaskAsync(Args a, CancellationToken ct)
    {
        Guid? listId = a.Str("list") is { } listName ? await ResolveListAsync(listName, create: true, ct) : null;
        var task = await Client.CreateTaskAsync(new CreateTaskRequest
        {
            Text = a.Required("text"),
            Notes = a.Str("notes"),
            DueDate = a.Date("due_date"),
            DueTime = a.Str("due_time"),
            Priority = a.Str("priority"),
            Tags = a.StrArray("tags"),
            ListId = listId,
            Recurrence = a.Str("repeat"),
            ReminderOffsets = a.IntArray("remind_minutes_before"),
            RepeatReminderMinutes = a.Int("nag_every_minutes") is > 0 and var n ? n : null,
        }, ct);
        return "Added: " + Serialize(Compact(task, await ListNamesAsync(ct), includeNotes: false));
    }

    private async Task<string> UpdateTaskAsync(Args a, CancellationToken ct)
    {
        var id = await ResolveIdAsync(a.Required("id"), includeTrash: false, ct);
        var changes = new Dictionary<string, object?>();
        if (a.Str("title") is { } title) changes["title"] = title;
        if (a.Str("notes") is { } notes) changes["notes"] = IsNone(notes) ? null : notes;
        if (a.Str("due_date") is { } date) changes["dueDate"] = IsNone(date) ? null : a.Date("due_date")!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (a.Str("due_time") is { } time) changes["dueTime"] = IsNone(time) ? null : time;
        if (a.Str("priority") is { } priority) changes["priority"] = priority;
        if (a.StrArray("tags") is { } tags) changes["tags"] = tags;
        if (a.Str("list") is { } list)
            changes["listId"] = string.Equals(list.Trim(), "inbox", StringComparison.OrdinalIgnoreCase) ? null : await ResolveListAsync(list, create: true, ct);
        if (a.Str("repeat") is { } repeat) changes["recurrence"] = IsNone(repeat) ? null : repeat;
        if (a.IntArray("remind_minutes_before") is { } offsets) changes["reminderOffsets"] = offsets;
        if (a.Int("nag_every_minutes") is { } nag) changes["repeatReminderMinutes"] = nag > 0 ? nag : null;
        if (changes.Count == 0) throw new ArgumentException("Nothing to change: give at least one field besides id.");
        var task = await Client.PatchTaskAsync(id, changes, ct);
        return "Updated: " + Serialize(Compact(task, await ListNamesAsync(ct), includeNotes: false));
    }

    private async Task<string> TaskActionAsync(Args a, string verb, Func<ApiClient, Guid, CancellationToken, Task<TaskDto>> action,
        CancellationToken ct, bool includeTrash = false)
    {
        var id = await ResolveIdAsync(a.Required("id"), includeTrash, ct);
        var task = await action(Client, id, ct);
        var lists = await ListNamesAsync(ct);
        return $"{verb}: " + Serialize(Compact(task, lists, includeNotes: false));
    }

    private async Task<string> SnoozeAsync(Args a, CancellationToken ct)
    {
        var id = await ResolveIdAsync(a.Required("id"), includeTrash: false, ct);
        var task = await Client.SnoozeAsync(id, a.Int("minutes"), ct);
        return $"Snoozed until {Local(task.NextReminderUtc)}: {task.Title}";
    }

    private async Task<string> UndoAsync(Args a, CancellationToken ct) =>
        await Client.UndoAsync(ct) is { } what ? $"Undid: {what}" : "There was nothing to undo.";

    private async Task<string> NotifyAsync(Args a, CancellationToken ct)
    {
        var result = await Client.NotifyAsync(new NotifyRequest { Title = a.Required("title"), Body = a.Str("body"), Priority = a.Str("priority") }, ct);
        return $"Shown (notification {result.Id}).";
    }

    private async Task<string> SetDndAsync(Args a, CancellationToken ct)
    {
        var enabled = a.Bool("enabled") ?? throw new ArgumentException("'enabled' (true or false) is required.");
        var result = await Client.SetDndAsync(enabled, ct);
        return $"Do Not Disturb is {(result.Enabled ? "on" : "off")}.";
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A compact, model-friendly view of a task.</summary>
    private static Dictionary<string, object?> Compact(TaskDto t, IReadOnlyDictionary<Guid, string> lists, bool includeNotes)
    {
        var d = new Dictionary<string, object?>
        {
            ["id"] = CliApp.ShortId(t.Id),
            ["title"] = t.Title,
        };
        if (t.Status != TaskStatusNames.Open) d["status"] = t.Status;
        if (t.DeletedUtc is not null) d["inTrash"] = true;
        if (t.DueDate is { } due) d["due"] = due.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture) + (t.DueTime is { } time ? " " + time : "");
        if (t.Priority != PriorityNames.None) d["priority"] = t.Priority;
        if (t.ListId is { } listId) d["list"] = lists.GetValueOrDefault(listId, "?");
        if (t.Tags.Count > 0) d["tags"] = t.Tags;
        if (t.RecurrenceText is { } rec) d["repeats"] = rec;
        if (t.NextReminderUtc is { } next && t.Status == TaskStatusNames.Open) d["nextReminder"] = Local(next);
        if (t.RepeatReminderMinutes is { } nag) d["nagEveryMinutes"] = nag;
        if (t.CompletedUtc is { } done) d["completed"] = Local(done);
        if (!string.IsNullOrWhiteSpace(t.Notes))
            d["notes"] = includeNotes || t.Notes.Length <= 120 ? t.Notes : t.Notes[..119] + "…";
        if (includeNotes)
        {
            d["fullId"] = t.Id;
            if (t.ReminderOffsets is { Count: > 0 } offsets) d["remindMinutesBefore"] = offsets;
            if (!string.IsNullOrWhiteSpace(t.ReminderActions)) d["reminderActions"] = t.ReminderActions;
            if (t.Attachments is { Count: > 0 } files) d["attachments"] = files;
            d["created"] = Local(t.CreatedUtc);
        }
        return d;
    }

    private static string Local(DateTimeOffset? at) =>
        at is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd ddd HH:mm", CultureInfo.InvariantCulture) : "";

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    private static bool IsNone(string s) => s.Trim().ToLowerInvariant() is "none" or "null" or "" or "-";

    private async Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(CancellationToken ct) =>
        (await Client.ListListsAsync(ct)).ToDictionary(l => l.Id, l => l.Name);

    private async Task<Guid?> ResolveListAsync(string name, bool create, CancellationToken ct)
    {
        name = name.Trim().TrimStart('@');
        var lists = await Client.ListListsAsync(ct);
        var match = lists.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match.Id;
        if (!create) throw new ArgumentException($"There's no list named '{name}'. Lists: {string.Join(", ", lists.Select(l => l.Name))}");
        return (await Client.CreateListAsync(name, ct)).Id;
    }

    /// <summary>Full GUID, or the short id (last hex digits) shown by list_tasks.</summary>
    private async Task<Guid> ResolveIdAsync(string raw, bool includeTrash, CancellationToken ct)
    {
        raw = raw.Trim();
        if (Guid.TryParse(raw, out var full)) return full;
        var key = raw.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        if (key.Length < 4 || !key.All(char.IsAsciiHexDigit))
            throw new ArgumentException($"'{raw}' isn't a task id. Use the id from list_tasks.");
        var candidates = await Client.ListTasksAsync("all", includeCompleted: true, limit: 5000, ct: ct);
        if (includeTrash) candidates.AddRange(await Client.ListTasksAsync("trash", limit: 5000, ct: ct));
        var matches = candidates.Where(t => t.Id.ToString("N").EndsWith(key, StringComparison.Ordinal)).DistinctBy(t => t.Id).ToList();
        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw new ArgumentException($"No task with id '{raw}'" + (includeTrash ? "." : " (it may be in the trash).")),
            _ => throw new ArgumentException($"'{raw}' matches {matches.Count} tasks; use the full id."),
        };
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };

    // ---- JSON Schema helpers

    private sealed record SchemaProp(string Name, JsonObject Definition, bool IsRequired = false);

    private static SchemaProp Prop(string name, string type, string description, string[]? values = null)
    {
        var def = new JsonObject { ["type"] = type, ["description"] = description };
        if (values is not null) def["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        return new SchemaProp(name, def);
    }

    private static SchemaProp ArrayProp(string name, string itemType, string description) =>
        new(name, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = itemType }, ["description"] = description });

    private static SchemaProp Required(SchemaProp p) => p with { IsRequired = true };

    private static JsonObject Schema(params SchemaProp[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props) properties[p.Name] = p.Definition.DeepClone();
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        var required = props.Where(p => p.IsRequired).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray();
        if (required.Length > 0) schema["required"] = new JsonArray(required);
        return schema;
    }

    /// <summary>Typed, forgiving access to tool arguments (assistants sometimes send numbers as strings).</summary>
    private sealed class Args(JsonObject args)
    {
        public string? Str(string key)
        {
            var node = args[key];
            if (node is null) return null;
            return node.GetValueKind() switch
            {
                JsonValueKind.String => node.GetValue<string>() is { Length: > 0 } s ? s : null,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => node.ToJsonString(),
                JsonValueKind.Null => null,
                _ => throw new ArgumentException($"'{key}' must be text."),
            };
        }

        public string Required(string key) => Str(key) ?? throw new ArgumentException($"'{key}' is required.");

        public int? Int(string key)
        {
            var node = args[key];
            if (node is null || node.GetValueKind() == JsonValueKind.Null) return null;
            var text = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new ArgumentException($"'{key}' must be a whole number.");
        }

        public bool? Bool(string key)
        {
            var node = args[key];
            if (node is null) return null;
            return node.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.String when bool.TryParse(node.GetValue<string>(), out var b) => b,
                _ => throw new ArgumentException($"'{key}' must be true or false."),
            };
        }

        public DateOnly? Date(string key)
        {
            if (Str(key) is not { } text) return null;
            return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : throw new ArgumentException($"'{key}' must be a date like 2026-11-20.");
        }

        public List<string>? StrArray(string key)
        {
            var node = args[key];
            if (node is null || node.GetValueKind() == JsonValueKind.Null) return null;
            if (node is JsonArray array) return array.Where(x => x is not null).Select(x => x!.GetValueKind() == JsonValueKind.String ? x.GetValue<string>() : x.ToJsonString()).ToList();
            if (node.GetValueKind() == JsonValueKind.String)
                return node.GetValue<string>().Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            throw new ArgumentException($"'{key}' must be a list.");
        }

        public List<int>? IntArray(string key)
        {
            var strings = StrArray(key);
            if (strings is null) return null;
            return strings.Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new ArgumentException($"'{key}' must be a list of whole numbers.")).ToList();
        }
    }
}
