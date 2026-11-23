using TaskHub.Abstractions.Events;
using TaskHub.Core.Domain;
using TaskHub.Core.Services;

namespace TaskHub.Storage.Tests;

public class TaskServiceTests
{
    [Fact]
    public async Task Create_persists_and_publishes_task_created()
    {
        await using var h = await Harness.CreateAsync();

        var task = await h.Tasks.CreateAsync(new NewTask
        {
            Title = "  Write report  ",
            Notes = "Q4 numbers",
            DueDate = Harness.Today,
            DueTime = new TimeOnly(15, 0),
            Priority = Priority.High,
            Tags = ["#Work", "work", "Reports"],
        }, "ui");

        var stored = await h.Tasks.GetRequiredAsync(task.Id);
        Assert.Equal("Write report", stored.Title);
        Assert.Equal("Q4 numbers", stored.Notes);
        Assert.Equal(["work", "reports"], stored.Tags);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero), stored.DueAtUtc); // 15:00 IST
        Assert.Equal(Harness.Start, stored.CreatedUtc);

        var e = await h.WaitForEventAsync(EventTypes.TaskCreated);
        Assert.Equal("ui", e.Source);
        Assert.Equal(task.Id, e.DataAs<TaskChangedEvent>()!.Task.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Title_is_required(string title)
    {
        await using var h = await Harness.CreateAsync();
        await Assert.ThrowsAsync<TaskValidationException>(() => h.Tasks.CreateAsync(new NewTask { Title = title }));
    }

    [Fact]
    public async Task Unknown_list_is_rejected()
    {
        await using var h = await Harness.CreateAsync();
        await Assert.ThrowsAsync<TaskValidationException>(() => h.Tasks.CreateAsync(new NewTask { Title = "x", ListId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Quick_add_creates_missing_list_and_reuses_existing_case_insensitively()
    {
        await using var h = await Harness.CreateAsync();
        var a = await h.Tasks.QuickAddAsync("one @Work");
        var b = await h.Tasks.QuickAddAsync("two @work");

        Assert.NotNull(a.ListId);
        Assert.Equal(a.ListId, b.ListId);
        var lists = await h.Tasks.GetListsAsync();
        Assert.Single(lists);
        Assert.Equal("Work", lists[0].Name);
        Assert.Equal(2, lists[0].OpenCount);
    }

    [Fact]
    public async Task Views_today_upcoming_overdue_inbox()
    {
        await using var h = await Harness.CreateAsync();
        var today = await h.Tasks.QuickAddAsync("today-task today");
        var todayPast = await h.Tasks.QuickAddAsync("earlier-today today 8am"); // 08:00 < 10:30 now
        var yesterday = await h.Tasks.CreateAsync(new NewTask { Title = "yesterday-task", DueDate = Harness.Today.AddDays(-1) });
        var tomorrow = await h.Tasks.QuickAddAsync("tomorrow-task tomorrow");
        var undated = await h.Tasks.QuickAddAsync("undated-task");
        var inList = await h.Tasks.QuickAddAsync("listed-task @Home");
        var done = await h.Tasks.QuickAddAsync("done-task today");
        await h.Tasks.CompleteAsync(done.Id);

        Titles(await h.Query(TaskView.Today)).ShouldBe("yesterday-task", "earlier-today", "today-task");
        Titles(await h.Query(TaskView.Upcoming)).ShouldBe("tomorrow-task");
        Titles(await h.Query(TaskView.Overdue)).ShouldBe("yesterday-task", "earlier-today");
        Assert.DoesNotContain("listed-task", Titles(await h.Query(TaskView.Inbox)));
        Assert.Contains("undated-task", Titles(await h.Query(TaskView.Inbox)));
        Titles(await h.Query(TaskView.Completed)).ShouldBe("done-task");
        Assert.DoesNotContain("done-task", Titles(await h.Query(TaskView.All)));
        Assert.Contains("done-task", Titles(await h.Query(TaskView.All, includeCompleted: true)));
        _ = (today, todayPast, yesterday, tomorrow, undated, inList);
    }

    [Fact]
    public async Task Overdue_follows_the_clock()
    {
        await using var h = await Harness.CreateAsync();
        await h.Tasks.QuickAddAsync("meeting today 11am");
        Assert.Empty(await h.Query(TaskView.Overdue));

        h.Clock.Advance(TimeSpan.FromHours(1)); // 11:30 IST
        Assert.Single(await h.Query(TaskView.Overdue));
    }

    [Fact]
    public async Task Search_tag_and_list_filters()
    {
        await using var h = await Harness.CreateAsync();
        await h.Tasks.QuickAddAsync("Buy milk #errands @Home");
        await h.Tasks.QuickAddAsync("Fix 100% of bugs #work");
        var notes = await h.Tasks.CreateAsync(new NewTask { Title = "Plan trip", Notes = "Remember MILK for the road", Tags = ["travel"] });
        var home = (await h.Tasks.GetListsAsync()).Single();

        Assert.Equal(["Buy milk", "Plan trip"], Titles(await h.Query(text: "milk")).Order(StringComparer.Ordinal)); // title + notes, case-insensitive
        Titles(await h.Query(text: "100%")).ShouldBe("Fix 100% of bugs");               // LIKE wildcards are escaped
        Assert.Empty(await h.Query(text: "_"));
        Titles(await h.Query(tag: "#Errands")).ShouldBe("Buy milk");
        Titles(await h.Query(list: home.Id)).ShouldBe("Buy milk");
        Titles(await h.Query(tag: "travel", text: "trip")).ShouldBe("Plan trip");
        _ = notes;

        var tags = await h.Tasks.GetTagsAsync();
        Assert.Equal(["errands", "travel", "work"], tags.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Patch_changes_only_given_fields_and_null_clears()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("Report tomorrow 9am #work !high");

        await h.Tasks.UpdateAsync(t.Id, new TaskPatch { Notes = "draft" });
        var a = await h.Tasks.GetRequiredAsync(t.Id);
        Assert.Equal("Report", a.Title);
        Assert.Equal("draft", a.Notes);
        Assert.Equal(new TimeOnly(9, 0), a.DueTime);
        Assert.Equal(Priority.High, a.Priority);

        await h.Tasks.UpdateAsync(t.Id, new TaskPatch { DueTime = (TimeOnly?)null });
        var b = await h.Tasks.GetRequiredAsync(t.Id);
        Assert.Equal(Harness.Today.AddDays(1), b.DueDate);
        Assert.Null(b.DueTime);
        Assert.Null(b.DueAtUtc);

        await h.Tasks.UpdateAsync(t.Id, new TaskPatch { DueDate = (DateOnly?)null });
        var c = await h.Tasks.GetRequiredAsync(t.Id);
        Assert.Null(c.DueDate);

        await h.Tasks.UpdateAsync(t.Id, new TaskPatch { DueTime = new TimeOnly(18, 0) });
        var d = await h.Tasks.GetRequiredAsync(t.Id);
        Assert.Equal(Harness.Today, d.DueDate); // a bare time means today
        Assert.Equal(new TimeOnly(18, 0), d.DueTime);
    }

    [Fact]
    public async Task Complete_and_reopen_are_idempotent()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("x");
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        var done = await h.Tasks.CompleteAsync(t.Id);
        Assert.Equal(TaskItemStatus.Completed, done.Status);
        Assert.Equal(h.Clock.UtcNow, done.CompletedUtc);
        var undoDepth = h.Tasks.Undo.Count;
        await h.Tasks.CompleteAsync(t.Id); // no-op: no new undo entry
        Assert.Equal(undoDepth, h.Tasks.Undo.Count);

        var reopened = await h.Tasks.ReopenAsync(t.Id);
        Assert.Equal(TaskItemStatus.Open, reopened.Status);
        Assert.Null(reopened.CompletedUtc);
    }

    [Fact]
    public async Task Delete_is_soft_and_restorable()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("trash me");

        await h.Tasks.DeleteAsync(t.Id);
        Assert.Empty(await h.Query());
        Titles(await h.Query(TaskView.Trash)).ShouldBe("trash me");
        await Assert.ThrowsAsync<TaskValidationException>(() => h.Tasks.CompleteAsync(t.Id));

        await h.Tasks.RestoreAsync(t.Id);
        Titles(await h.Query()).ShouldBe("trash me");
        Assert.Empty(await h.Query(TaskView.Trash));
    }

    [Fact]
    public async Task Undo_reverts_each_kind_of_change_in_reverse_order()
    {
        await using var h = await Harness.CreateAsync();
        var t = await h.Tasks.QuickAddAsync("Original #a");
        await h.Tasks.UpdateAsync(t.Id, new TaskPatch { Title = "Renamed", Tags = new Optional<IReadOnlyList<string>>(["b"]) });
        await h.Tasks.CompleteAsync(t.Id);
        await h.Tasks.DeleteAsync(t.Id);

        Assert.StartsWith("Delete", await h.Tasks.UndoAsync());
        Assert.False((await h.Tasks.GetRequiredAsync(t.Id)).IsDeleted);

        Assert.StartsWith("Complete", await h.Tasks.UndoAsync());
        Assert.Equal(TaskItemStatus.Open, (await h.Tasks.GetRequiredAsync(t.Id)).Status);

        Assert.StartsWith("Edit", await h.Tasks.UndoAsync());
        var reverted = await h.Tasks.GetRequiredAsync(t.Id);
        Assert.Equal("Original", reverted.Title);
        Assert.Equal(["a"], reverted.Tags);

        Assert.StartsWith("Add", await h.Tasks.UndoAsync());
        Assert.True((await h.Tasks.GetRequiredAsync(t.Id)).IsDeleted); // undoing an add trashes it

        Assert.Null(await h.Tasks.UndoAsync());
        await h.WaitForEventAsync(EventTypes.UndoApplied);
    }

    [Fact]
    public async Task Lists_create_rename_and_reject_duplicates()
    {
        await using var h = await Harness.CreateAsync();
        var work = await h.Tasks.CreateListAsync("Work");
        await Assert.ThrowsAsync<TaskValidationException>(() => h.Tasks.CreateListAsync("work"));
        await h.Tasks.CreateListAsync("Home");
        await Assert.ThrowsAsync<TaskValidationException>(() => h.Tasks.RenameListAsync(work.Id, "HOME"));

        await h.Tasks.RenameListAsync(work.Id, "Office");
        Assert.Equal(["Office", "Home"], (await h.Tasks.GetListsAsync()).Select(l => l.Name));
    }

    [Fact]
    public async Task Lists_can_be_coloured_and_cleared()
    {
        await using var h = await Harness.CreateAsync();
        var work = await h.Tasks.CreateListAsync("Work", "#4f8ef7");
        Assert.Equal("#4F8EF7", work.Color);

        await h.Tasks.SetListColorAsync(work.Id, "f00");
        Assert.Equal("#FF0000", (await h.Tasks.GetListsAsync()).Single().Color);
        var e = await h.WaitForEventAsync(EventTypes.ListUpdated);
        Assert.Equal("#FF0000", e.DataAs<ListChangedEvent>()!.List.Color);

        await h.Tasks.SetListColorAsync(work.Id, null);
        Assert.Null((await h.Tasks.GetListsAsync()).Single().Color);

        await Assert.ThrowsAsync<TaskValidationException>(() => h.Tasks.SetListColorAsync(work.Id, "blue"));
        await Assert.ThrowsAsync<TaskValidationException>(() => h.Tasks.CreateListAsync("Home", "#12345"));
    }

    [Fact]
    public async Task Lists_can_have_an_icon()
    {
        await using var h = await Harness.CreateAsync();
        var home = await h.Tasks.CreateListAsync("Home");
        Assert.Null(home.Icon);

        await h.Tasks.SetListIconAsync(home.Id, "HOME");
        Assert.Equal("home", (await h.Tasks.GetListsAsync()).Single().Icon);

        await h.Tasks.SetListIconAsync(home.Id, "folder"); // the default is stored as null
        Assert.Null((await h.Tasks.GetListsAsync()).Single().Icon);

        await Assert.ThrowsAsync<TaskValidationException>(() => h.Tasks.SetListIconAsync(home.Id, "unicorn"));
    }

    [Fact]
    public async Task Purge_removes_only_old_trash()
    {
        await using var h = await Harness.CreateAsync();
        var old = await h.Tasks.QuickAddAsync("old");
        await h.Tasks.DeleteAsync(old.Id);
        h.Clock.Advance(TimeSpan.FromDays(31));
        var recent = await h.Tasks.QuickAddAsync("recent");
        await h.Tasks.DeleteAsync(recent.Id);

        var purged = await h.Store.PurgeDeletedAsync(h.Clock.UtcNow - TimeSpan.FromDays(30));

        Assert.Equal(1, purged);
        Assert.Null(await h.Tasks.GetAsync(old.Id));
        Assert.NotNull(await h.Tasks.GetAsync(recent.Id));
    }

    [Fact]
    public async Task Concurrent_writes_are_serialised()
    {
        await using var h = await Harness.CreateAsync();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => h.Tasks.QuickAddAsync($"task {i} #bulk")));
        Assert.Equal(40, (await h.Query(tag: "bulk")).Count);
        Assert.Equal(40, h.Tasks.Undo.Count);
    }

    private static List<string> Titles(IEnumerable<TaskItem> tasks) => tasks.Select(t => t.Title).ToList();
}

internal static class AssertExtensions
{
    public static void ShouldBe(this List<string> actual, params string[] expected) => Assert.Equal(expected, actual);
}
