using TaskHub.Abstractions.Contracts;
using TaskHub.Core.Domain;
using TaskHub.Core.Services;

namespace TaskHub.Storage.Tests;

public class ImportExportTests
{
    [Fact]
    public async Task Export_then_import_into_a_fresh_database_round_trips()
    {
        string json;
        await using (var source = await Harness.CreateAsync())
        {
            await source.Tasks.QuickAddAsync("Pay rent tomorrow 9am #home !high @Finance");
            var done = await source.Tasks.QuickAddAsync("Old thing");
            await source.Tasks.CompleteAsync(done.Id);
            var trashed = await source.Tasks.QuickAddAsync("Trashed");
            await source.Tasks.DeleteAsync(trashed.Id);
            json = await source.ImportExport.ExportJsonAsync();
        }

        Assert.Contains("\"format\": \"taskhub\"", json, StringComparison.Ordinal);

        await using var target = await Harness.CreateAsync();
        var result = await target.ImportExport.ImportJsonAsync(json);

        Assert.Equal(new ImportResult(1, 0, 2, 0), result); // trashed tasks aren't exported
        var all = await target.Query(includeCompleted: true);
        var rent = all.Single(t => t.Title == "Pay rent");
        Assert.Equal(Priority.High, rent.Priority);
        Assert.Equal(["home"], rent.Tags);
        Assert.Equal(new TimeOnly(9, 0), rent.DueTime);
        Assert.Equal("Finance", (await target.Tasks.GetListsAsync()).Single(l => l.Id == rent.ListId).Name);
        Assert.Equal(TaskItemStatus.Completed, all.Single(t => t.Title == "Old thing").Status);
    }

    [Fact]
    public async Task Reimporting_updates_instead_of_duplicating()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("Same task @L");
        var json = await h.ImportExport.ExportJsonAsync();
        await h.Tasks.UpdateAsync(t.Id, new TaskPatch { Title = "Changed locally" });

        var result = await h.ImportExport.ImportJsonAsync(json);

        Assert.Equal(new ImportResult(0, 1, 0, 1), result);
        Assert.Equal("Same task", (await h.Tasks.GetRequiredAsync(t.Id)).Title);
        Assert.Single(await h.Query());
    }

    [Fact]
    public async Task List_name_clash_with_different_id_gets_a_suffix()
    {
        await using var h = await Harness.CreateAsync();
        await h.Tasks.CreateListAsync("Work");
        var doc = new ExportDocument { Lists = [new ExportList(Guid.NewGuid(), "Work", null, 0, Harness.Start)] };

        await h.ImportExport.ImportAsync(doc);

        Assert.Equal(["Work", "Work (2)"], (await h.Tasks.GetListsAsync()).Select(l => l.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"format\":\"todoist\",\"version\":1}")]
    [InlineData("{\"format\":\"taskhub\",\"version\":99}")]
    public async Task Bad_files_are_rejected_with_a_message(string json)
    {
        await using var h = await Harness.CreateAsync();
        var ex = await Assert.ThrowsAsync<TaskValidationException>(() => h.ImportExport.ImportJsonAsync(json));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public async Task Tasks_pointing_at_unknown_lists_go_to_the_inbox()
    {
        await using var h = await Harness.CreateAsync();
        var doc = new ExportDocument
        {
            Tasks = [new TaskDto(Guid.NewGuid(), "Orphan", null, "open", "none", null, null, null, Guid.NewGuid(), [], Harness.Start, Harness.Start, null, null)],
        };

        await h.ImportExport.ImportAsync(doc);

        Assert.Null((await h.Query()).Single().ListId);
    }
}
