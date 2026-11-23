using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TaskHub.Abstractions.Contracts;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Domain;
using TaskHub.Core.Hosting;
using TaskHub.Core.Services;

namespace TaskHub.Api.Endpoints;

/// <summary>
/// /v1 endpoints. Clients may send <c>X-TaskHub-Client: greeter</c> so events show
/// <c>source = "api:greeter"</c> (lets a client ignore its own echoes).
/// </summary>
internal static class TaskEndpoints
{
    public const string ClientHeader = "X-TaskHub-Client";

    public static void Map(IEndpointRouteBuilder app, string version)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapGet("/status", (IClock clock) =>
            Results.Ok(new StatusDto(version, Environment.ProcessId, clock.UtcNow, clock.LocalZone.Id)));

        // ---- tasks
        v1.MapGet("/tasks", async (TaskService svc, string? view, string? q, string? tag, Guid? list, bool? includeCompleted, int? limit, CancellationToken ct) =>
        {
            var query = new TaskQuery
            {
                View = TaskQuery.ParseView(view),
                Text = q,
                Tag = tag,
                ListId = list,
                IncludeCompleted = includeCompleted ?? false,
                Limit = Math.Clamp(limit ?? 1000, 1, 5000),
            };
            var tasks = await svc.QueryAsync(query, ct);
            return Results.Ok(tasks.Select(t => t.ToDto()));
        });

        v1.MapGet("/tasks/{id:guid}", async (TaskService svc, Guid id, CancellationToken ct) =>
            Results.Ok((await svc.GetRequiredAsync(id, ct)).ToDto()));

        v1.MapPost("/tasks", async (TaskService svc, HttpContext http, CreateTaskRequest req, CancellationToken ct) =>
        {
            var source = Source(http);
            TaskItem task;
            if (!string.IsNullOrWhiteSpace(req.Text))
            {
                task = await svc.QuickAddAsync(req.Text, source, ct);
                var overrides = OverridesFrom(req);
                if (overrides is not null) task = await svc.UpdateAsync(task.Id, overrides, source, ct);
            }
            else
            {
                task = await svc.CreateAsync(new NewTask
                {
                    Title = req.Title ?? throw new TaskValidationException("Provide 'text' (quick-add) or 'title'."),
                    Notes = req.Notes,
                    DueDate = req.DueDate,
                    DueTime = TaskMapper.ParseTime(req.DueTime),
                    Priority = TaskMapper.ParsePriority(req.Priority),
                    Tags = req.Tags,
                    ListId = req.ListId,
                    Recurrence = req.Recurrence,
                    ReminderOffsets = req.ReminderOffsets,
                    RepeatReminderMinutes = req.RepeatReminderMinutes,
                    ReminderActions = req.ReminderActions,
                }, source, ct);
            }
            return Results.Created($"/v1/tasks/{task.Id}", task.ToDto());
        });

        v1.MapPatch("/tasks/{id:guid}", async (TaskService svc, HttpContext http, Guid id, JsonElement body, CancellationToken ct) =>
            Results.Ok((await svc.UpdateAsync(id, ParsePatch(body), Source(http), ct)).ToDto()));

        v1.MapPost("/tasks/{id:guid}/complete", async (TaskService svc, HttpContext http, Guid id, CancellationToken ct) =>
            Results.Ok((await svc.CompleteAsync(id, Source(http), ct)).ToDto()));
        v1.MapPost("/tasks/{id:guid}/reopen", async (TaskService svc, HttpContext http, Guid id, CancellationToken ct) =>
            Results.Ok((await svc.ReopenAsync(id, Source(http), ct)).ToDto()));
        v1.MapPost("/tasks/{id:guid}/restore", async (TaskService svc, HttpContext http, Guid id, CancellationToken ct) =>
            Results.Ok((await svc.RestoreAsync(id, Source(http), ct)).ToDto()));
        v1.MapPost("/tasks/{id:guid}/snooze", async (TaskService svc, HttpContext http, Guid id, SnoozeRequest? req, CancellationToken ct) =>
        {
            var minutes = req?.Minutes ?? svc.Reminders.Options.SnoozeMinutes;
            return Results.Ok((await svc.SnoozeAsync(id, TimeSpan.FromMinutes(minutes), Source(http), ct)).ToDto());
        });
        v1.MapDelete("/tasks/{id:guid}", async (TaskService svc, HttpContext http, Guid id, CancellationToken ct) =>
            Results.Ok((await svc.DeleteAsync(id, Source(http), ct)).ToDto()));

        v1.MapPost("/undo", async (TaskService svc, HttpContext http, CancellationToken ct) =>
            await svc.UndoAsync(Source(http), ct) is { } description
                ? Results.Ok(new UndoResultDto(description))
                : Results.Conflict(new ApiError("Nothing to undo.")));

        // ---- lists & tags
        v1.MapGet("/lists", async (TaskService svc, CancellationToken ct) => Results.Ok(await svc.GetListsAsync(ct)));
        v1.MapPost("/lists", async (TaskService svc, HttpContext http, CreateListRequest req, CancellationToken ct) =>
        {
            var list = await svc.CreateListAsync(req.Name, req.Color, Source(http), ct);
            return Results.Created($"/v1/lists/{list.Id}", list.ToDto());
        });
        v1.MapPatch("/lists/{id:guid}", async (TaskService svc, HttpContext http, Guid id, UpdateListRequest req, CancellationToken ct) =>
        {
            var source = Source(http);
            if (req.Name is { } name) await svc.RenameListAsync(id, name, source, ct);
            if (req.Color is not null) await svc.SetListColorAsync(id, req.Color, source, ct);
            if (req.Icon is not null) await svc.SetListIconAsync(id, req.Icon, source, ct);
            var list = (await svc.GetListsAsync(ct)).FirstOrDefault(l => l.Id == id);
            return list is null ? Results.NotFound(new ApiError($"List {id} not found.")) : Results.Ok(list);
        });
        v1.MapGet("/tags", async (TaskService svc, CancellationToken ct) => Results.Ok(await svc.GetTagsAsync(ct)));

        // ---- import / export
        v1.MapGet("/export", async (ImportExportService io, CancellationToken ct) => Results.Ok(await io.ExportAsync(ct)));
        v1.MapPost("/import", async (ImportExportService io, HttpContext http, ExportDocument doc, CancellationToken ct) =>
            Results.Ok(await io.ImportAsync(doc, Source(http), ct)));
    }

    internal static string SourceOf(HttpContext http) => Source(http);

    private static string Source(HttpContext http)
    {
        var client = http.Request.Headers[ClientHeader].ToString().Trim().ToLowerInvariant();
        if (client.Length == 0 || client.Length > 40 || !client.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return EventSources.Api;
        return $"{EventSources.Api}:{client}";
    }

    private static TaskPatch? OverridesFrom(CreateTaskRequest req)
    {
        if (req.Title is null && req.Notes is null && req.DueDate is null && req.DueTime is null
            && req.Priority is null && req.Tags is null && req.ListId is null
            && req.Recurrence is null && req.ReminderOffsets is null && req.RepeatReminderMinutes is null
            && req.ReminderActions is null)
            return null;
        // Note: `default(Optional<T>)` = "leave unchanged"; a plain value = "set".
        return new TaskPatch
        {
            Title = req.Title is null ? default(Optional<string>) : req.Title,
            Notes = req.Notes is null ? default(Optional<string?>) : req.Notes,
            DueDate = req.DueDate is null ? default(Optional<DateOnly?>) : req.DueDate,
            DueTime = req.DueTime is null ? default(Optional<TimeOnly?>) : TaskMapper.ParseTime(req.DueTime),
            Priority = req.Priority is null ? default(Optional<Priority>) : TaskMapper.ParsePriority(req.Priority),
            Tags = req.Tags is null ? default(Optional<IReadOnlyList<string>>) : new Optional<IReadOnlyList<string>>(req.Tags),
            ListId = req.ListId is null ? default(Optional<Guid?>) : req.ListId,
            Recurrence = req.Recurrence is null ? default(Optional<string?>) : req.Recurrence,
            ReminderOffsets = req.ReminderOffsets is null ? default(Optional<IReadOnlyList<int>>) : new Optional<IReadOnlyList<int>>(req.ReminderOffsets),
            RepeatReminderMinutes = req.RepeatReminderMinutes is null ? default(Optional<int?>) : req.RepeatReminderMinutes,
            ReminderActions = req.ReminderActions is null ? default(Optional<string?>) : req.ReminderActions,
        };
    }

    /// <summary>
    /// JSON merge-patch semantics: a property that is present sets the value (null clears it);
    /// an absent property is left unchanged.
    /// </summary>
    internal static TaskPatch ParsePatch(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new TaskValidationException("Body must be a JSON object.");
        var patch = new TaskPatch();
        foreach (var p in body.EnumerateObject())
        {
            try
            {
                patch = ApplyProperty(patch, p);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                throw new TaskValidationException($"Invalid value for '{p.Name}': {ex.Message}");
            }
        }
        return patch;
    }

    private static TaskPatch ApplyProperty(TaskPatch patch, JsonProperty p)
    {
        {
            var v = p.Value;
            var isNull = v.ValueKind == JsonValueKind.Null;
            return p.Name.ToLowerInvariant() switch
            {
                "title" => patch with { Title = isNull ? throw new TaskValidationException("title can't be null.") : v.GetString()! },
                "notes" => patch with { Notes = isNull ? null : v.GetString() },
                "duedate" => patch with { DueDate = isNull ? (DateOnly?)null : ParseDate(v.GetString()) },
                "duetime" => patch with { DueTime = isNull ? null : TaskMapper.ParseTime(v.GetString()) },
                "priority" => patch with { Priority = TaskMapper.ParsePriority(isNull ? null : v.GetString()) },
                "tags" => patch with
                {
                    Tags = new Optional<IReadOnlyList<string>>(isNull ? [] : v.EnumerateArray().Select(x => x.GetString() ?? "").ToList()),
                },
                "listid" => patch with { ListId = isNull ? (Guid?)null : v.GetGuid() },
                "recurrence" => patch with { Recurrence = isNull ? null : v.GetString() },
                "reminderoffsets" => patch with
                {
                    ReminderOffsets = new Optional<IReadOnlyList<int>>(isNull ? [] : v.EnumerateArray().Select(x => x.GetInt32()).ToList()),
                },
                "repeatreminderminutes" => patch with { RepeatReminderMinutes = isNull ? (int?)null : v.GetInt32() },
                "reminderactions" => patch with { ReminderActions = isNull ? null : v.GetString() },
                _ => throw new TaskValidationException($"Unknown or read-only property '{p.Name}'."),
            };
        }
    }

    private static DateOnly ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", out var d)
            ? d
            : throw new TaskValidationException($"Invalid date '{text}'. Use yyyy-MM-dd.");
}
