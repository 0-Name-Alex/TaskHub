using TaskHub.Abstractions.Contracts;
using TaskHub.Core.Services;

namespace TaskHub.Storage.Tests;

public class AutomationStorageTests
{
    [Fact]
    public async Task Reminder_actions_and_attachments_are_stored_validated_and_undoable()
    {
        await using var h = await Harness.CreateAsync();
        var file = Path.Combine(h.Root, "shot.png");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);

        var task = await h.Tasks.CreateAsync(new NewTask
        {
            Title = "Stand-up",
            ReminderActions = "open https://meet.example.com/standup\n  notify Starting  ",
            Attachments = [file, file.ToUpperInvariant()],
        });

        var stored = await h.Tasks.GetRequiredAsync(task.Id);
        Assert.Equal("open https://meet.example.com/standup\n  notify Starting", stored.ReminderActions);
        Assert.Equal([file], stored.Attachments); // duplicates (ignoring case) removed

        var dto = stored.ToDto();
        Assert.Equal(stored.ReminderActions, dto.ReminderActions);
        Assert.Equal([file], dto.Attachments);

        await Assert.ThrowsAsync<TaskValidationException>(() =>
            h.Tasks.UpdateAsync(task.Id, new TaskPatch { ReminderActions = "explode now" }));
        await Assert.ThrowsAsync<TaskValidationException>(() =>
            h.Tasks.UpdateAsync(task.Id, new TaskPatch { Attachments = new Optional<IReadOnlyList<string>>(["relative.png"]) }));

        await h.Tasks.UpdateAsync(task.Id, new TaskPatch
        {
            ReminderActions = (string?)null,
            Attachments = new Optional<IReadOnlyList<string>>([]),
        });
        var cleared = await h.Tasks.GetRequiredAsync(task.Id);
        Assert.Null(cleared.ReminderActions);
        Assert.Empty(cleared.Attachments);

        await h.Tasks.UndoAsync();
        var restored = await h.Tasks.GetRequiredAsync(task.Id);
        Assert.Equal(stored.ReminderActions, restored.ReminderActions);
        Assert.Equal([file], restored.Attachments);
    }

    [Fact]
    public async Task Export_and_import_keep_reminder_actions_and_attachments()
    {
        await using var h = await Harness.CreateAsync();
        var file = Path.Combine(h.Root, "doc.pdf");
        var task = await h.Tasks.CreateAsync(new NewTask
        {
            Title = "Read",
            ReminderActions = "open https://example.com",
            Attachments = [file],
        });

        var json = await h.ImportExport.ExportJsonAsync();
        Assert.Contains("reminderActions", json, StringComparison.Ordinal);

        await using var other = await Harness.CreateAsync();
        await other.ImportExport.ImportJsonAsync(json);
        var imported = await other.Tasks.GetRequiredAsync(task.Id);
        Assert.Equal("open https://example.com", imported.ReminderActions);
        Assert.Equal([file], imported.Attachments);

        // Broken action text in a file is dropped rather than failing the whole import.
        var doc = await h.ImportExport.ExportAsync();
        var bad = doc with { Tasks = [doc.Tasks[0] with { ReminderActions = "explode" }] };
        await other.ImportExport.ImportAsync(bad);
        Assert.Null((await other.Tasks.GetRequiredAsync(task.Id)).ReminderActions);
    }
}
