using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TaskHub.Abstractions.Contracts;

namespace TaskHub.Cli;

/// <summary>
/// Builds the <c>th</c> command tree. Handlers write to <paramref name="output"/> so tests can
/// capture it; the API client is created lazily so <c>th --help</c> works without the app running.
/// </summary>
public sealed class CliApp(Func<ApiClient> clientFactory, TextWriter output, TextWriter error, TextReader? input = null)
{
    private ApiClient? _client;
    private ApiClient Client => _client ??= clientFactory();

    public async Task<int> RunAsync(string[] args)
    {
        var root = Build();
        // "@List" is quick-add syntax here, not a response file.
        var config = new ParserConfiguration { ResponseFileTokenReplacer = null };
        return await root.Parse(args, config).InvokeAsync();
    }

    public RootCommand Build()
    {

        var root = new RootCommand("TaskHub from the command line. Talks to the running TaskHub app.\n\n" +
                                   "Quick-add syntax: th add Pay rent tomorrow 9am #home !high @Finance");

        // th add <text...>
        var addText = new Argument<string[]>("text") { Description = "Task text in quick-add syntax", Arity = ArgumentArity.OneOrMore };
        var addNotes = new Option<string?>("--notes", "-n") { Description = "Notes for the task" };
        var addRepeat = new Option<string?>("--repeat", "-r") { Description = "Repeat: 'every monday', daily, weekdays, 'every 2 weeks', or an RRULE" };
        var addRemind = new Option<string?>("--remind") { Description = "Minutes before due to remind (e.g. 10 or 0,60), or 'none'" };
        var addOnRemind = new Option<string[]>("--on-remind") { Description = "Action to run with the reminder, e.g. \"open https://meet.example.com/x\" (repeatable)", AllowMultipleArgumentsPerToken = false };
        var addJson = JsonOption();
        var add = new Command("add", "Add a task") { addText, addNotes, addRepeat, addRemind, addOnRemind, addJson };
        add.SetAction((pr, ct) => Run(async () =>
        {
            var task = await Client.CreateTaskAsync(new CreateTaskRequest
            {
                Text = string.Join(' ', pr.GetValue(addText) ?? []),
                Notes = pr.GetValue(addNotes),
                Recurrence = pr.GetValue(addRepeat),
                ReminderOffsets = pr.GetValue(addRemind) is { } r ? ParseOffsets(r) : null,
                ReminderActions = pr.GetValue(addOnRemind) is { Length: > 0 } actions ? string.Join('\n', actions) : null,
            }, ct);
            if (pr.GetValue(addJson)) WriteJson(task);
            else output.WriteLine($"Added {ShortId(task.Id)}  {Describe(task)}");
        }));
        root.Subcommands.Add(add);

        // th ls
        var view = new Option<string?>("--view", "-v") { Description = "inbox, today, upcoming, overdue, all, completed, trash (default: all)" };
        var tag = new Option<string?>("--tag", "-t") { Description = "Only tasks with this tag" };
        var list = new Option<string?>("--list", "-l") { Description = "Only tasks in this list (name)" };
        var search = new Option<string?>("--search", "-s") { Description = "Text to search for in title and notes" };
        var all = new Option<bool>("--all", "-a") { Description = "Include completed tasks" };
        var lsJson = JsonOption();
        var ls = new Command("ls", "List tasks") { view, tag, list, search, all, lsJson };
        ls.Aliases.Add("list");
        ls.SetAction((pr, ct) => Run(async () =>
        {
            var listId = await ResolveListAsync(pr.GetValue(list), ct);
            var tasks = await Client.ListTasksAsync(pr.GetValue(view), pr.GetValue(search), pr.GetValue(tag), listId, pr.GetValue(all), ct: ct);
            if (pr.GetValue(lsJson))
            {
                WriteJson(tasks);
                return;
            }
            if (tasks.Count == 0)
            {
                output.WriteLine("No tasks.");
                return;
            }
            var listNames = (await Client.ListListsAsync(ct)).ToDictionary(l => l.Id, l => l.Name);
            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var t in tasks) output.WriteLine(Row(t, listNames, today));
            output.WriteLine($"{tasks.Count} task(s)");
        }));
        root.Subcommands.Add(ls);

        // th show <id>
        var showId = IdArgument();
        var showJson = JsonOption();
        var show = new Command("show", "Show one task") { showId, showJson };
        show.SetAction((pr, ct) => Run(async () =>
        {
            var task = await Client.GetTaskAsync(await ResolveIdAsync(pr.GetValue(showId)!, includeTrash: true, ct), ct);
            if (pr.GetValue(showJson)) WriteJson(task);
            else WriteDetails(task, (await Client.ListListsAsync(ct)).ToDictionary(l => l.Id, l => l.Name));
        }));
        root.Subcommands.Add(show);

        // th done / reopen / rm / restore <id...>
        root.Subcommands.Add(IdAction("done", "Complete task(s)", (id, ct) => Client.CompleteAsync(id, ct), "Completed", false));
        root.Subcommands.Add(IdAction("reopen", "Reopen completed task(s)", (id, ct) => Client.ReopenAsync(id, ct), "Reopened", false));
        root.Subcommands.Add(IdAction("rm", "Move task(s) to the trash", (id, ct) => Client.DeleteAsync(id, ct), "Deleted", false));
        root.Subcommands.Add(IdAction("restore", "Restore task(s) from the trash", (id, ct) => Client.RestoreAsync(id, ct), "Restored", true));

        // th snooze <id> [minutes]
        var snoozeId = IdArgument();
        var snoozeMinutes = new Argument<int?>("minutes") { Description = "How long (default: your snooze setting)", Arity = ArgumentArity.ZeroOrOne };
        var snooze = new Command("snooze", "Postpone a task's next reminder") { snoozeId, snoozeMinutes };
        snooze.SetAction((pr, ct) => Run(async () =>
        {
            var id = await ResolveIdAsync(pr.GetValue(snoozeId)!, includeTrash: false, ct);
            var task = await Client.SnoozeAsync(id, pr.GetValue(snoozeMinutes), ct);
            output.WriteLine($"Snoozed {ShortId(task.Id)}  {task.Title} until {task.NextReminderUtc?.ToLocalTime():t}");
        }));
        root.Subcommands.Add(snooze);

        // th edit <id> [...]
        var editId = IdArgument();
        var title = new Option<string?>("--title") { Description = "New title" };
        var due = new Option<string?>("--due") { Description = "Due date yyyy-MM-dd, or 'none'" };
        var time = new Option<string?>("--time") { Description = "Due time HH:mm, or 'none'" };
        var priority = new Option<string?>("--priority", "-p") { Description = "none, low, medium, high, urgent" };
        var tags = new Option<string?>("--tags") { Description = "Comma-separated tags (replaces existing), or 'none'" };
        var notes = new Option<string?>("--notes", "-n") { Description = "Notes, or 'none'" };
        var moveTo = new Option<string?>("--list", "-l") { Description = "Move to list (name), or 'inbox'" };
        var repeat = new Option<string?>("--repeat", "-r") { Description = "Repeat rule ('every monday', daily, RRULE), or 'none'" };
        var remind = new Option<string?>("--remind") { Description = "Minutes before due to remind (e.g. 10 or 0,60), or 'none'" };
        var nag = new Option<string?>("--nag") { Description = "After due, remind every N minutes until done, or 'none'" };
        var onRemind = new Option<string[]>("--on-remind") { Description = "Action(s) to run with the reminder (replaces existing; repeatable), or 'none'" };
        var editJson = JsonOption();
        var edit = new Command("edit", "Change a task") { editId, title, due, time, priority, tags, notes, moveTo, repeat, remind, nag, onRemind, editJson };
        edit.SetAction((pr, ct) => Run(async () =>
        {
            var id = await ResolveIdAsync(pr.GetValue(editId)!, includeTrash: false, ct);
            var changes = new Dictionary<string, object?>();
            if (pr.GetValue(title) is { } t) changes["title"] = t;
            if (pr.GetValue(due) is { } d) changes["dueDate"] = IsNone(d) ? null : d;
            if (pr.GetValue(time) is { } tm) changes["dueTime"] = IsNone(tm) ? null : tm;
            if (pr.GetValue(priority) is { } p) changes["priority"] = p;
            if (pr.GetValue(tags) is { } tg) changes["tags"] = IsNone(tg) ? Array.Empty<string>() : tg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pr.GetValue(notes) is { } n) changes["notes"] = IsNone(n) ? null : n;
            if (pr.GetValue(moveTo) is { } l) changes["listId"] = string.Equals(l, "inbox", StringComparison.OrdinalIgnoreCase) ? null : await ResolveListAsync(l, ct);
            if (pr.GetValue(repeat) is { } rp) changes["recurrence"] = IsNone(rp) ? null : rp;
            if (pr.GetValue(remind) is { } rm) changes["reminderOffsets"] = ParseOffsets(rm);
            if (pr.GetValue(nag) is { } ng) changes["repeatReminderMinutes"] = IsNone(ng) ? null : ParseMinutes(ng);
            if (pr.GetValue(onRemind) is { Length: > 0 } onr) changes["reminderActions"] = onr.Length == 1 && IsNone(onr[0]) ? null : string.Join('\n', onr);
            if (changes.Count == 0) throw new CliException("Nothing to change. See: th edit --help");
            var task = await Client.PatchTaskAsync(id, changes, ct);
            if (pr.GetValue(editJson)) WriteJson(task);
            else output.WriteLine($"Updated {ShortId(task.Id)}  {Describe(task)}");
        }));
        root.Subcommands.Add(edit);

        // th undo
        var undo = new Command("undo", "Undo the last change (from any client)");
        undo.SetAction((_, ct) => Run(async () =>
            output.WriteLine(await Client.UndoAsync(ct) is { } what ? $"Undid: {what}" : "Nothing to undo.")));
        root.Subcommands.Add(undo);

        // th lists [add <name>]
        var listsJson = JsonOption();
        var lists = new Command("lists", "Show lists") { listsJson };
        lists.SetAction((pr, ct) => Run(async () =>
        {
            var items = await Client.ListListsAsync(ct);
            if (pr.GetValue(listsJson)) WriteJson(items);
            else if (items.Count == 0) output.WriteLine("No lists yet. Create one with: th lists add <name>");
            else foreach (var l in items) output.WriteLine($"{l.Name,-30} {l.OpenCount,4} open");
        }));
        var listName = new Argument<string[]>("name") { Arity = ArgumentArity.OneOrMore };
        var listsAdd = new Command("add", "Create a list") { listName };
        listsAdd.SetAction((pr, ct) => Run(async () =>
        {
            var created = await Client.CreateListAsync(string.Join(' ', pr.GetValue(listName) ?? []), ct);
            output.WriteLine($"Created list '{created.Name}'");
        }));
        lists.Subcommands.Add(listsAdd);
        root.Subcommands.Add(lists);

        // th tags
        var tagsJson = JsonOption();
        var tagsCmd = new Command("tags", "Show tags in use") { tagsJson };
        tagsCmd.SetAction((pr, ct) => Run(async () =>
        {
            var items = await Client.ListTagsAsync(ct);
            if (pr.GetValue(tagsJson)) WriteJson(items);
            else if (items.Count == 0) output.WriteLine("No tags in use.");
            else foreach (var t in items) output.WriteLine($"#{t.Name,-29} {t.OpenCount,4} open");
        }));
        root.Subcommands.Add(tagsCmd);

        // th export [file] / import <file>
        var exportFile = new Argument<string?>("file") { Description = "Output file (default: print to console)", Arity = ArgumentArity.ZeroOrOne };
        var export = new Command("export", "Export all tasks and lists as JSON") { exportFile };
        export.SetAction((pr, ct) => Run(async () =>
        {
            var doc = await Client.ExportAsync(ct);
            var text = JsonSerializer.Serialize(doc, ApiClient.Json);
            if (pr.GetValue(exportFile) is { } path)
            {
                await File.WriteAllTextAsync(path, text, ct);
                output.WriteLine($"Exported {doc.Tasks.Count} task(s) and {doc.Lists.Count} list(s) to {Path.GetFullPath(path)}");
            }
            else
            {
                output.WriteLine(text);
            }
        }));
        root.Subcommands.Add(export);

        var importFile = new Argument<string>("file") { Description = "A file created by 'th export' or the app" };
        var import = new Command("import", "Import tasks and lists from JSON (merges by id)") { importFile };
        import.SetAction((pr, ct) => Run(async () =>
        {
            var path = pr.GetValue(importFile)!;
            if (!File.Exists(path)) throw new CliException($"File not found: {path}");
            ExportDocument doc;
            try
            {
                doc = JsonSerializer.Deserialize<ExportDocument>(await File.ReadAllTextAsync(path, ct), ApiClient.Json)
                      ?? throw new CliException("The file is empty.");
            }
            catch (JsonException ex)
            {
                throw new CliException($"Not a valid TaskHub export: {ex.Message}");
            }
            var r = await Client.ImportAsync(doc, ct);
            output.WriteLine($"Imported: {r.TasksAdded} new / {r.TasksUpdated} updated task(s), {r.ListsAdded} new / {r.ListsUpdated} updated list(s).");
        }));
        root.Subcommands.Add(import);

        // th notify <title> [--body] [--priority] [--seconds] [--button action=Label]
        var notifyTitle = new Argument<string[]>("title") { Description = "Banner title", Arity = ArgumentArity.OneOrMore };
        var notifyBody = new Option<string?>("--body", "-b") { Description = "Second line of text" };
        var notifyPriority = new Option<string?>("--priority", "-p") { Description = "low, normal, high, urgent" };
        var notifySeconds = new Option<int?>("--seconds", "-s") { Description = "Seconds on screen (0 = until dismissed)" };
        var notifyButtons = new Option<string[]>("--button") { Description = "A button as action=Label (repeatable), e.g. --button ok=Thanks", AllowMultipleArgumentsPerToken = false };
        var notifyIcon = new Option<string?>("--icon") { Description = "An emoji or Segoe Fluent Icons glyph" };
        var notify = new Command("notify", "Show a TaskHub banner (for scripts)") { notifyTitle, notifyBody, notifyPriority, notifySeconds, notifyButtons, notifyIcon };
        notify.SetAction((pr, ct) => Run(async () =>
        {
            var buttons = (pr.GetValue(notifyButtons) ?? []).Select(b =>
            {
                var parts = b.Split('=', 2);
                return parts.Length == 2 ? new NotifyButtonDto(parts[0].Trim(), parts[1].Trim())
                    : throw new CliException($"'{b}' should look like action=Label.");
            }).ToList();
            var result = await Client.NotifyAsync(new NotifyRequest
            {
                Title = string.Join(' ', pr.GetValue(notifyTitle) ?? []),
                Body = pr.GetValue(notifyBody),
                Priority = pr.GetValue(notifyPriority),
                Seconds = pr.GetValue(notifySeconds),
                Buttons = buttons.Count > 0 ? buttons : null,
                Icon = pr.GetValue(notifyIcon),
            }, ct);
            output.WriteLine($"Shown (id {result.Id})");
        }));
        root.Subcommands.Add(notify);

        // th dnd [on|off]
        var dndState = new Argument<string?>("state") { Description = "on or off (omit to show the current state)", Arity = ArgumentArity.ZeroOrOne };
        var dnd = new Command("dnd", "Do Not Disturb: show or set") { dndState };
        dnd.SetAction((pr, ct) => Run(async () =>
        {
            var state = pr.GetValue(dndState)?.Trim().ToLowerInvariant();
            var result = state switch
            {
                null or "" => await Client.GetDndAsync(ct),
                "on" or "true" or "1" => await Client.SetDndAsync(true, ct),
                "off" or "false" or "0" => await Client.SetDndAsync(false, ct),
                _ => throw new CliException("Use: th dnd on | off"),
            };
            output.WriteLine($"Do Not Disturb is {(result.Enabled ? "on" : "off")}.");
        }));
        root.Subcommands.Add(dnd);

        // th mcp [--read-only]
        var mcpReadOnly = new Option<bool>("--read-only") { Description = "Only allow reading tasks (no adding, changing or deleting)" };
        var mcp = new Command("mcp", "Run as an MCP server on stdin/stdout, for AI assistants such as Claude Desktop") { mcpReadOnly };
        mcp.SetAction(async (pr, ct) =>
        {
            // Real stdio: UTF-8 both ways, no BOM, and nothing else may be written to stdout.
            var reader = input ?? new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var writer = input is null ? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true } : output;
            var version = typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var server = new Mcp.McpServer(clientFactory, reader, writer, error, pr.GetValue(mcpReadOnly), version);
            await server.RunAsync(ct);
            return 0;
        });
        root.Subcommands.Add(mcp);

        // th status
        var status = new Command("status", "Check that TaskHub is running");
        status.SetAction((_, ct) => Run(async () =>
        {
            var s = await Client.StatusAsync(ct);
            output.WriteLine($"TaskHub {s.Version} is running (pid {s.ProcessId}, time zone {s.TimeZone}).");
        }));
        root.Subcommands.Add(status);

        return root;
    }

    // ------------------------------------------------------------------ helpers

    private static Option<bool> JsonOption() => new("--json") { Description = "Print raw JSON instead of a table" };

    private static Argument<string> IdArgument() =>
        new("id") { Description = "Task id: the 8-character short id shown by 'th ls', or the full id" };

    private Command IdAction(string name, string description, Func<Guid, CancellationToken, Task<TaskDto>> action, string verb, bool trash)
    {
        var ids = new Argument<string[]>("id") { Description = "One or more task ids (short or full)", Arity = ArgumentArity.OneOrMore };
        var cmd = new Command(name, description) { ids };
        cmd.SetAction((pr, ct) => Run(async () =>
        {
            foreach (var raw in pr.GetValue(ids) ?? [])
            {
                var task = await action(await ResolveIdAsync(raw, trash, ct), ct);
                output.WriteLine($"{verb} {ShortId(task.Id)}  {task.Title}");
            }
        }));
        return cmd;
    }

    private async Task<int> Run(Func<Task> body)
    {
        try
        {
            await body();
            return 0;
        }
        catch (CliException ex)
        {
            await error.WriteLineAsync("error: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Accepts a full GUID, or the short id (last 8 hex digits, which are random in v7 GUIDs).</summary>
    internal async Task<Guid> ResolveIdAsync(string raw, bool includeTrash, CancellationToken ct)
    {
        raw = raw.Trim();
        if (Guid.TryParse(raw, out var full)) return full;
        var key = raw.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        if (key.Length < 4 || !key.All(char.IsAsciiHexDigit))
            throw new CliException($"'{raw}' isn't a task id. Use the id shown by 'th ls'.");

        var candidates = await Client.ListTasksAsync("all", includeCompleted: true, limit: 5000, ct: ct);
        if (includeTrash) candidates.AddRange(await Client.ListTasksAsync("trash", limit: 5000, ct: ct));
        var matches = candidates.Where(t => t.Id.ToString("N").EndsWith(key, StringComparison.Ordinal)).DistinctBy(t => t.Id).ToList();
        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw new CliException($"No task with id '{raw}'."),
            _ => throw new CliException($"'{raw}' matches {matches.Count} tasks; use more characters."),
        };
    }

    private async Task<Guid?> ResolveListAsync(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var lists = await Client.ListListsAsync(ct);
        return lists.FirstOrDefault(l => string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
               ?? throw new CliException($"No list named '{name}'. See: th lists");
    }

    private static int[] ParseOffsets(string text)
    {
        if (IsNone(text)) return [];
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseMinutes).ToArray();
    }

    private static int ParseMinutes(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            ? m
            : throw new CliException($"'{text}' isn't a number of minutes.");

    internal static string ShortId(Guid id) => id.ToString("N")[^8..];

    private static bool IsNone(string s) => s is "none" or "null" or "-" or "";

    internal static string Row(TaskDto t, IReadOnlyDictionary<Guid, string> lists, DateOnly today)
    {
        var check = t.Status == TaskStatusNames.Completed ? "[x]" : "[ ]";
        var prio = t.Priority switch
        {
            PriorityNames.Urgent => "!!!",
            PriorityNames.High => "!! ",
            PriorityNames.Medium => "!  ",
            PriorityNames.Low => ".  ",
            _ => "   ",
        };
        var title = t.Title.Length > 48 ? t.Title[..47] + "…" : t.Title;
        var extras = new List<string>();
        if (t.DueDate is { } d)
        {
            var dueText = FormatDue(d, t.DueTime, today);
            if (t.Status == TaskStatusNames.Open && d < today) dueText += " (overdue)";
            extras.Add(dueText);
        }
        if (t.RecurrenceText is { } rec) extras.Add("↻ " + rec.ToLowerInvariant());
        if (t.ListId is { } lid && lists.TryGetValue(lid, out var listName)) extras.Add("@" + listName);
        extras.AddRange(t.Tags.Select(tag => "#" + tag));
        return $"{ShortId(t.Id)}  {check} {prio} {title,-48}  {string.Join("  ", extras)}".TrimEnd();
    }

    internal static string FormatDue(DateOnly date, string? time, DateOnly today)
    {
        var day = (date.DayNumber - today.DayNumber) switch
        {
            0 => "today",
            1 => "tomorrow",
            -1 => "yesterday",
            > 1 and < 7 => date.ToString("ddd", CultureInfo.CurrentCulture),
            _ => date.ToString(date.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture),
        };
        return time is null ? day : $"{day} {time}";
    }

    private static string Describe(TaskDto t)
    {
        var parts = new List<string> { t.Title };
        if (t.DueDate is { } d) parts.Add("due " + FormatDue(d, t.DueTime, DateOnly.FromDateTime(DateTime.Now)));
        if (t.Priority != PriorityNames.None) parts.Add(t.Priority);
        if (t.Tags.Count > 0) parts.Add(string.Join(' ', t.Tags.Select(x => "#" + x)));
        return string.Join("  ·  ", parts);
    }

    private void WriteDetails(TaskDto t, IReadOnlyDictionary<Guid, string> lists)
    {
        output.WriteLine(t.Title);
        output.WriteLine($"  id        {t.Id}  ({ShortId(t.Id)})");
        output.WriteLine($"  status    {t.Status}{(t.DeletedUtc is not null ? " (in trash)" : "")}");
        output.WriteLine($"  priority  {t.Priority}");
        if (t.DueDate is { } d) output.WriteLine($"  due       {d:yyyy-MM-dd}{(t.DueTime is null ? "" : " " + t.DueTime)}");
        if (t.RecurrenceText is { } rec) output.WriteLine($"  repeats   {rec}  ({t.Recurrence})");
        if (t.ReminderOffsets is { Count: > 0 } offs) output.WriteLine($"  reminders {string.Join(", ", offs.Select(o => o == 0 ? "at due" : $"{o} min before"))}");
        if (t.RepeatReminderMinutes is { } every) output.WriteLine($"  nag       every {every} min until done");
        if (t.NextReminderUtc is { } next) output.WriteLine($"  next      {next.ToLocalTime():g}");
        if (t.ReminderActions is { Length: > 0 } ra)
            foreach (var line in ra.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                output.WriteLine($"  on remind {line}");
        if (t.Attachments is { Count: > 0 } files)
            foreach (var f in files) output.WriteLine($"  attached  {f}");
        if (t.ListId is { } lid) output.WriteLine($"  list      {lists.GetValueOrDefault(lid, lid.ToString())}");
        if (t.Tags.Count > 0) output.WriteLine($"  tags      {string.Join(' ', t.Tags.Select(x => "#" + x))}");
        output.WriteLine($"  created   {t.CreatedUtc.ToLocalTime():g}");
        if (t.CompletedUtc is { } c) output.WriteLine($"  completed {c.ToLocalTime():g}");
        if (!string.IsNullOrWhiteSpace(t.Notes))
        {
            output.WriteLine();
            output.WriteLine(t.Notes);
        }
    }

    private void WriteJson(object value) => output.WriteLine(JsonSerializer.Serialize(value, ApiClient.Json));
}
