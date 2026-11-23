using System.Text.Json;
using System.Text.Json.Serialization;
using TaskHub.Abstractions.Contracts;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Domain;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;
using TaskHub.Core.Scheduling;

namespace TaskHub.Core.Services;

/// <summary>
/// JSON export/import (<see cref="ExportDocument"/>). Import merges by id: existing lists and
/// tasks are replaced, new ones added, nothing is deleted. Importing your own export is a no-op.
/// </summary>
public sealed class ImportExportService(ITaskStore store, IEventBus bus, IClock clock, ReminderPlanner reminders)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<ExportDocument> ExportAsync(CancellationToken ct = default)
    {
        var lists = await store.GetListsAsync(ct);
        var tasks = await store.GetAllAsync(ct);
        return new ExportDocument
        {
            ExportedUtc = clock.UtcNow,
            Lists = lists.Select(l => l.ToExport()).ToList(),
            Tasks = tasks.Select(t => t.ToDto()).ToList(),
        };
    }

    public async Task<string> ExportJsonAsync(CancellationToken ct = default) =>
        JsonSerializer.Serialize(await ExportAsync(ct), Json);

    public Task<ImportResult> ImportJsonAsync(string json, string source = EventSources.Core, CancellationToken ct = default)
    {
        ExportDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ExportDocument>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new TaskValidationException($"Not a valid TaskHub export: {ex.Message}");
        }
        return ImportAsync(doc ?? throw new TaskValidationException("The file is empty."), source, ct);
    }

    public async Task<ImportResult> ImportAsync(ExportDocument doc, string source = EventSources.Core, CancellationToken ct = default)
    {
        if (!string.Equals(doc.Format, ExportDocument.FormatName, StringComparison.OrdinalIgnoreCase))
            throw new TaskValidationException($"Unknown format '{doc.Format}'; expected '{ExportDocument.FormatName}'.");
        if (doc.Version > ExportDocument.CurrentVersion)
            throw new TaskValidationException($"This file is format version {doc.Version}; this TaskHub understands up to {ExportDocument.CurrentVersion}. Update TaskHub.");

        var lists = doc.Lists.Select(l => new TaskList
        {
            Id = l.Id == Guid.Empty ? Guid.CreateVersion7() : l.Id,
            Name = string.IsNullOrWhiteSpace(l.Name) ? "Imported" : l.Name.Trim(),
            Color = l.Color,
            Icon = ListIcons.IsKnown(l.Icon) ? l.Icon!.ToLowerInvariant() : null,
            SortOrder = l.SortOrder,
            CreatedUtc = l.CreatedUtc,
        }).ToList();
        var listIds = lists.Select(l => l.Id).ToHashSet();
        var existingListIds = (await store.GetListsAsync(ct)).Select(l => l.Id).ToHashSet();

        var tasks = new List<TaskItem>(doc.Tasks.Count);
        foreach (var dto in doc.Tasks)
        {
            if (string.IsNullOrWhiteSpace(dto.Title)) continue;
            var time = TaskMapper.ParseTime(dto.DueTime);
            var task = new TaskItem
            {
                Id = dto.Id == Guid.Empty ? Guid.CreateVersion7() : dto.Id,
                Title = dto.Title.Trim().Length > TaskItem.MaxTitleLength ? dto.Title.Trim()[..TaskItem.MaxTitleLength] : dto.Title.Trim(),
                Notes = dto.Notes,
                Status = dto.Status == TaskStatusNames.Completed ? TaskItemStatus.Completed : TaskItemStatus.Open,
                Priority = TaskMapper.ParsePriority(dto.Priority),
                DueDate = dto.DueDate,
                DueTime = dto.DueDate is null ? null : time,
                // Recompute from local date/time so imported tasks follow the current zone rules.
                DueAtUtc = dto.DueDate is { } d && time is { } t ? clock.ToUtc(d, t) : null,
                ListId = dto.ListId is { } lid && (listIds.Contains(lid) || existingListIds.Contains(lid)) ? lid : null,
                Tags = Domain.Tags.Normalize(dto.Tags),
                CreatedUtc = dto.CreatedUtc == default ? clock.UtcNow : dto.CreatedUtc,
                UpdatedUtc = dto.UpdatedUtc == default ? clock.UtcNow : dto.UpdatedUtc,
                CompletedUtc = dto.CompletedUtc,
                DeletedUtc = dto.DeletedUtc,
                Recurrence = dto.Recurrence is { } r && Recurrence.TryParse(r, out var rrule, out _) ? rrule : null,
                ReminderOffsets = (dto.ReminderOffsets ?? []).Where(m => m is >= 0 and <= 525_600).Distinct().Order().ToList(),
                RepeatReminderMinutes = dto.RepeatReminderMinutes is > 0 and <= 1440 ? dto.RepeatReminderMinutes : null,
                ReminderActions = dto.ReminderActions is { Length: > 0 and <= TaskItem.MaxReminderActionsLength } ra
                                  && Automation.ActionScript.TryParse(ra, out _, out _) ? ra : null,
                Attachments = (dto.Attachments ?? []).Where(a => !string.IsNullOrWhiteSpace(a) && a.Length <= 1000)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(TaskItem.MaxAttachments).ToList(),
            };
            reminders.Replan(task, clock.UtcNow);
            tasks.Add(task);
        }

        var (la, lu, ta, tu) = await store.UpsertAsync(lists, tasks, ct);
        var result = new ImportResult(la, lu, ta, tu);
        bus.Publish(EventTypes.DataImported, new DataImportedEvent(result), source);
        return result;
    }
}
