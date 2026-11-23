using TaskHub.Abstractions.Contracts;
using TaskHub.Cli;

namespace TaskHub.Api.Tests;

/// <summary>Runs `th` commands in-process against a real API server.</summary>
public class CliTests
{
    private sealed record Run(int ExitCode, string Out, string Err);

    private static async Task<Run> Th(ApiHarness api, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new CliApp(() => ApiClient.Create(api.BaseUrl, api.Token), output, error);
        var code = await cli.RunAsync(args);
        return new Run(code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Add_list_complete_and_undo()
    {
        await using var api = await ApiHarness.CreateAsync();

        var add = await Th(api, "add", "Pay", "rent", "tomorrow", "9am", "#home", "!high");
        Assert.Equal(0, add.ExitCode);
        Assert.Contains("Added", add.Out, StringComparison.Ordinal);
        var task = (await api.Core.Query()).Single();
        var shortId = CliApp.ShortId(task.Id);
        Assert.Contains(shortId, add.Out, StringComparison.Ordinal);

        var ls = await Th(api, "ls");
        Assert.Contains("Pay rent", ls.Out, StringComparison.Ordinal);
        Assert.Contains("#home", ls.Out, StringComparison.Ordinal);
        Assert.Contains("1 task(s)", ls.Out, StringComparison.Ordinal);

        var done = await Th(api, "done", shortId);
        Assert.Equal(0, done.ExitCode);
        Assert.Contains("Completed", done.Out, StringComparison.Ordinal);
        Assert.Contains("No tasks.", (await Th(api, "ls")).Out, StringComparison.Ordinal);

        var undo = await Th(api, "undo");
        Assert.Contains("Undid: Complete", undo.Out, StringComparison.Ordinal);
        Assert.Contains("Pay rent", (await Th(api, "ls")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_clears_and_sets_fields()
    {
        await using var api = await ApiHarness.CreateAsync();
        await Th(api, "add", "Report", "tomorrow", "9am", "#work");
        var id = CliApp.ShortId((await api.Core.Query()).Single().Id);

        var edit = await Th(api, "edit", id, "--time", "none", "--priority", "urgent", "--tags", "a, b", "--notes", "hello");
        Assert.Equal(0, edit.ExitCode);

        var t = (await api.Core.Query()).Single();
        Assert.Null(t.DueTime);
        Assert.NotNull(t.DueDate);
        Assert.Equal(Core.Domain.Priority.Urgent, t.Priority);
        Assert.Equal(["a", "b"], t.Tags);
        Assert.Equal("hello", t.Notes);
    }

    [Fact]
    public async Task Json_output_is_machine_readable()
    {
        await using var api = await ApiHarness.CreateAsync();
        await Th(api, "add", "one");
        var json = (await Th(api, "ls", "--json")).Out;
        var tasks = System.Text.Json.JsonSerializer.Deserialize<List<TaskDto>>(json, ApiClient.Json);
        Assert.Equal("one", tasks!.Single().Title);
    }

    [Fact]
    public async Task Lists_tags_filters()
    {
        await using var api = await ApiHarness.CreateAsync();
        await Th(api, "lists", "add", "Side", "Project");
        await Th(api, "add", "write", "docs", "@Side_Project", "#docs");
        await Th(api, "add", "unrelated");

        Assert.Contains("Side Project", (await Th(api, "lists")).Out, StringComparison.Ordinal);
        Assert.Contains("#docs", (await Th(api, "tags")).Out, StringComparison.Ordinal);
        var filtered = await Th(api, "ls", "--list", "side project");
        Assert.Contains("write docs", filtered.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated", filtered.Out, StringComparison.Ordinal);
        Assert.Contains("unrelated", (await Th(api, "ls", "-s", "UNREL")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rm_and_restore_with_trash()
    {
        await using var api = await ApiHarness.CreateAsync();
        await Th(api, "add", "temp");
        var id = CliApp.ShortId((await api.Core.Query()).Single().Id);

        Assert.Equal(0, (await Th(api, "rm", id)).ExitCode);
        Assert.Contains("temp", (await Th(api, "ls", "-v", "trash")).Out, StringComparison.Ordinal);
        Assert.Equal(0, (await Th(api, "restore", id)).ExitCode);
        Assert.Contains("temp", (await Th(api, "ls")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_and_import_files()
    {
        await using var api = await ApiHarness.CreateAsync();
        await Th(api, "add", "keep", "me", "@Box");
        var file = Path.Combine(api.Core.Root, "export.json");

        Assert.Equal(0, (await Th(api, "export", file)).ExitCode);
        Assert.True(File.Exists(file));
        var import = await Th(api, "import", file);
        Assert.Equal(0, import.ExitCode);
        Assert.Contains("0 new / 1 updated task(s)", import.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Errors_are_friendly_and_set_exit_code()
    {
        await using var api = await ApiHarness.CreateAsync();

        var unknown = await Th(api, "done", "abcdef12");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("No task with id", unknown.Err, StringComparison.Ordinal);

        var badId = await Th(api, "done", "zz");
        Assert.Equal(1, badId.ExitCode);

        var missingFile = await Th(api, "import", Path.Combine(api.Core.Root, "nope.json"));
        Assert.Contains("File not found", missingFile.Err, StringComparison.Ordinal);

        var noList = await Th(api, "ls", "--list", "Nope");
        Assert.Contains("No list named", noList.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task App_not_running_gives_a_clear_message()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new CliApp(() => ApiClient.Create("http://127.0.0.1:1", "x"), output, error);

        var code = await cli.RunAsync(["ls"]);

        Assert.Equal(1, code);
        Assert.Contains("Is it running?", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Due_formatting()
    {
        var today = new DateOnly(2026, 11, 2);
        Assert.Equal("today 09:00", CliApp.FormatDue(today, "09:00", today));
        Assert.Equal("tomorrow", CliApp.FormatDue(today.AddDays(1), null, today));
        Assert.Equal("yesterday", CliApp.FormatDue(today.AddDays(-1), null, today));
    }

    [Fact]
    public async Task Repeat_remind_and_snooze_from_the_cli()
    {
        await using var api = await ApiHarness.CreateAsync();
        var add = await Th(api, "add", "Gym", "tomorrow", "7am", "--repeat", "every mon,wed,fri", "--remind", "0,15");
        Assert.Equal(0, add.ExitCode);
        var t = (await api.Core.Query()).Single();
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO,WE,FR", t.Recurrence);
        Assert.Equal([0, 15], t.ReminderOffsets);
        var id = CliApp.ShortId(t.Id);

        Assert.Contains("↻", (await Th(api, "ls")).Out, StringComparison.Ordinal);
        var show = await Th(api, "show", id);
        Assert.Contains("repeats", show.Out, StringComparison.Ordinal);
        Assert.Contains("15 min before", show.Out, StringComparison.Ordinal);

        Assert.Equal(0, (await Th(api, "snooze", id, "45")).ExitCode);
        Assert.Equal(api.Core.Clock.UtcNow.AddMinutes(45), (await api.Core.Tasks.GetRequiredAsync(t.Id)).NextReminderUtc);

        Assert.Equal(0, (await Th(api, "edit", id, "--repeat", "none", "--remind", "none", "--nag", "10")).ExitCode);
        var edited = await api.Core.Tasks.GetRequiredAsync(t.Id);
        Assert.Null(edited.Recurrence);
        Assert.Empty(edited.ReminderOffsets);
        Assert.Equal(10, edited.RepeatReminderMinutes);
    }

    [Fact]
    public async Task Notify_and_dnd_from_the_cli()
    {
        await using var api = await ApiHarness.CreateAsync();

        var notify = await Th(api, "notify", "Build", "finished", "--body", "All green", "-p", "urgent", "--button", "ok=Thanks");
        Assert.Equal(0, notify.ExitCode);
        var shown = Assert.Single(api.Surface.Shown);
        Assert.Equal("Build finished", shown.Title);
        Assert.Equal("Thanks", Assert.Single(shown.Buttons).Label);

        Assert.Contains("is on", (await Th(api, "dnd", "on")).Out, StringComparison.Ordinal);
        Assert.True(api.Notifications.DoNotDisturb);
        Assert.Contains("is on", (await Th(api, "dnd")).Out, StringComparison.Ordinal);
        Assert.Contains("is off", (await Th(api, "dnd", "off")).Out, StringComparison.Ordinal);

        Assert.Equal(1, (await Th(api, "notify", "x", "--button", "nolabel")).ExitCode);
    }
}
