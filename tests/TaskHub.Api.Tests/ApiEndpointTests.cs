using System.Net;
using System.Net.Http.Json;
using System.Text;
using TaskHub.Abstractions.Contracts;
using TaskHub.Abstractions.Events;

namespace TaskHub.Api.Tests;

public class ApiEndpointTests
{
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Create_with_quick_add_text()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();

        var response = await http.PostAsync("/v1/tasks", Json("""{"text":"Pay rent tomorrow 9am #home !high @Finance"}"""));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = (await response.Content.ReadFromJsonAsync<TaskDto>())!;
        Assert.Equal("Pay rent", task.Title);
        Assert.Equal("high", task.Priority);
        Assert.Equal("09:00", task.DueTime);
        Assert.Equal(new DateOnly(2026, 11, 3), task.DueDate);
        Assert.Equal(["home"], task.Tags);
        Assert.NotNull(task.ListId);
        Assert.Equal($"/v1/tasks/{task.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Create_with_explicit_fields_and_client_source()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        http.DefaultRequestHeaders.Add("X-TaskHub-Client", "Greeter");

        var task = await (await http.PostAsync("/v1/tasks",
                Json("""{"title":"Water plants","dueDate":"2026-11-05","dueTime":"18:30","priority":"medium","tags":["Home"]}""")))
            .Content.ReadFromJsonAsync<TaskDto>();

        Assert.Equal("medium", task!.Priority);
        Assert.Equal("18:30", task.DueTime);
        var e = await api.Core.WaitForEventAsync(EventTypes.TaskCreated);
        Assert.Equal("api:greeter", e.Source);
    }

    [Fact]
    public async Task Reminder_actions_can_be_set_patched_and_validated()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();

        var task = await (await http.PostAsync("/v1/tasks",
                Json("""{"text":"Stand-up 9:30am","reminderActions":"open https://meet.example.com/standup"}""")))
            .Content.ReadFromJsonAsync<TaskDto>();
        Assert.Equal("open https://meet.example.com/standup", task!.ReminderActions);
        Assert.Empty(task.Attachments!);

        var patched = await (await http.PatchAsync($"/v1/tasks/{task.Id}", Json("""{"reminderActions":"notify Go | now"}""")))
            .Content.ReadFromJsonAsync<TaskDto>();
        Assert.Equal("notify Go | now", patched!.ReminderActions);

        var bad = await http.PatchAsync($"/v1/tasks/{task.Id}", Json("""{"reminderActions":"format c:"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Unknown action", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Attachments are read-only over the API.
        var attach = await http.PatchAsync($"/v1/tasks/{task.Id}", Json("""{"attachments":["C:\\x.exe"]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, attach.StatusCode);

        var cleared = await (await http.PatchAsync($"/v1/tasks/{task.Id}", Json("""{"reminderActions":null}""")))
            .Content.ReadFromJsonAsync<TaskDto>();
        Assert.Null(cleared!.ReminderActions);
    }

    [Fact]
    public async Task Text_plus_explicit_fields_lets_fields_win()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        var task = await (await http.PostAsync("/v1/tasks", Json("""{"text":"call bob tomorrow !low","priority":"urgent"}""")))
            .Content.ReadFromJsonAsync<TaskDto>();
        Assert.Equal("urgent", task!.Priority);
        Assert.Equal("call bob", task.Title);
    }

    [Theory]
    [InlineData("""{}""", "text")]
    [InlineData("""{"title":"   "}""", "Title")]
    [InlineData("""{"title":"x","priority":"mega"}""", "priority")]
    [InlineData("""{"title":"x","dueTime":"25:00"}""", "time")]
    [InlineData("""{"title":"x","listId":"6d1f2c1e-0000-0000-0000-000000000000"}""", "List")]
    public async Task Invalid_create_returns_400_with_a_message(string body, string mentions)
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();

        var response = await http.PostAsync("/v1/tasks", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Contains(mentions, error!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_json_is_400()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/v1/tasks", Json("{not json"))).StatusCode);
    }

    [Fact]
    public async Task Unknown_task_is_404()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/v1/tasks/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/v1/tasks/{Guid.NewGuid()}/complete", null)).StatusCode);
    }

    [Fact]
    public async Task Patch_uses_merge_semantics()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        var created = await (await http.PostAsync("/v1/tasks", Json("""{"text":"Report tomorrow 9am #work !high"}"""))).Content.ReadFromJsonAsync<TaskDto>();

        var patched = await (await http.PatchAsync($"/v1/tasks/{created!.Id}", Json("""{"notes":"draft","dueTime":null}""")))
            .Content.ReadFromJsonAsync<TaskDto>();

        Assert.Equal("Report", patched!.Title);   // untouched
        Assert.Equal("high", patched.Priority);   // untouched
        Assert.Equal("draft", patched.Notes);     // set
        Assert.Null(patched.DueTime);             // cleared
        Assert.NotNull(patched.DueDate);          // untouched

        var bad = await http.PatchAsync($"/v1/tasks/{created.Id}", Json("""{"createdUtc":"2020-01-01T00:00:00Z"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Complete_delete_restore_undo_and_views()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        var t = (await (await http.PostAsync("/v1/tasks", Json("""{"text":"Lifecycle today"}"""))).Content.ReadFromJsonAsync<TaskDto>())!;

        Assert.Single((await http.GetFromJsonAsync<List<TaskDto>>("/v1/tasks?view=today"))!);
        Assert.Equal("completed", (await (await http.PostAsync($"/v1/tasks/{t.Id}/complete", null)).Content.ReadFromJsonAsync<TaskDto>())!.Status);
        Assert.Empty((await http.GetFromJsonAsync<List<TaskDto>>("/v1/tasks?view=today"))!);
        Assert.Single((await http.GetFromJsonAsync<List<TaskDto>>("/v1/tasks?view=completed"))!);

        var undo = await (await http.PostAsync("/v1/undo", null)).Content.ReadFromJsonAsync<UndoResultDto>();
        Assert.StartsWith("Complete", undo!.Description, StringComparison.Ordinal);
        Assert.Single((await http.GetFromJsonAsync<List<TaskDto>>("/v1/tasks?view=today"))!);

        Assert.NotNull((await (await http.DeleteAsync($"/v1/tasks/{t.Id}")).Content.ReadFromJsonAsync<TaskDto>())!.DeletedUtc);
        Assert.Single((await http.GetFromJsonAsync<List<TaskDto>>("/v1/tasks?view=trash"))!);
        await http.PostAsync($"/v1/tasks/{t.Id}/restore", null);
        Assert.Empty((await http.GetFromJsonAsync<List<TaskDto>>("/v1/tasks?view=trash"))!);

        // drain the undo stack, then nothing left → 409
        while ((await http.PostAsync("/v1/undo", null)).StatusCode == HttpStatusCode.OK) { }
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync("/v1/undo", null)).StatusCode);
    }

    [Fact]
    public async Task Query_filters_and_bad_view()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        await http.PostAsync("/v1/tasks", Json("""{"text":"Buy milk #errands"}"""));
        await http.PostAsync("/v1/tasks", Json("""{"text":"Write code #work"}"""));

        Assert.Single((await http.GetFromJsonAsync<List<TaskDto>>("/v1/tasks?q=MILK"))!);
        Assert.Single((await http.GetFromJsonAsync<List<TaskDto>>("/v1/tasks?tag=work"))!);
        Assert.Equal(2, (await http.GetFromJsonAsync<List<TagDto>>("/v1/tags"))!.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/v1/tasks?view=someday")).StatusCode);
    }

    [Fact]
    public async Task Lists_and_export_import()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        Assert.Equal(HttpStatusCode.Created, (await http.PostAsync("/v1/lists", Json("""{"name":"Garden"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/v1/lists", Json("""{"name":"garden"}"""))).StatusCode);
        await http.PostAsync("/v1/tasks", Json("""{"text":"Mow lawn @Garden"}"""));

        var lists = await http.GetFromJsonAsync<List<TaskListDto>>("/v1/lists");
        Assert.Equal(1, lists!.Single().OpenCount);

        var id = lists.Single().Id;
        var patched = await (await http.PatchAsync($"/v1/lists/{id}", Json("""{"color":"#2e9e44"}"""))).Content.ReadFromJsonAsync<TaskListDto>();
        Assert.Equal("#2E9E44", patched!.Color);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsync($"/v1/lists/{id}", Json("""{"color":"green"}"""))).StatusCode);
        var withIcon = await (await http.PatchAsync($"/v1/lists/{id}", Json("""{"icon":"home"}"""))).Content.ReadFromJsonAsync<TaskListDto>();
        Assert.Equal("home", withIcon!.Icon);
        Assert.Equal("#2E9E44", withIcon.Color);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PatchAsync($"/v1/lists/{Guid.NewGuid()}", Json("""{"color":""}"""))).StatusCode);

        var export = await http.GetFromJsonAsync<ExportDocument>("/v1/export");
        Assert.Single(export!.Tasks);
        var import = await (await http.PostAsJsonAsync("/v1/import", export)).Content.ReadFromJsonAsync<ImportResult>();
        Assert.Equal(new ImportResult(0, 1, 0, 1), import);
    }

    [Fact]
    public async Task Recurrence_reminders_and_snooze_over_the_api()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();

        var created = await (await http.PostAsync("/v1/tasks",
                Json("""{"title":"Standup","dueDate":"2026-11-03","dueTime":"09:30","recurrence":"every weekday","reminderOffsets":[0,10],"repeatReminderMinutes":5}""")))
            .Content.ReadFromJsonAsync<TaskDto>();
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", created!.Recurrence);
        Assert.Equal("Every weekday", created.RecurrenceText);
        Assert.Equal([0, 10], created.ReminderOffsets);
        Assert.Equal(5, created.RepeatReminderMinutes);
        Assert.NotNull(created.NextReminderUtc);

        var snoozed = await (await http.PostAsync($"/v1/tasks/{created.Id}/snooze", Json("""{"minutes":30}"""))).Content.ReadFromJsonAsync<TaskDto>();
        Assert.Equal(api.Core.Clock.UtcNow.AddMinutes(30), snoozed!.NextReminderUtc);

        var defaultSnooze = await http.PostAsync($"/v1/tasks/{created.Id}/snooze", null);
        Assert.Equal(HttpStatusCode.OK, defaultSnooze.StatusCode); // uses reminders.snooze_minutes

        var patched = await (await http.PatchAsync($"/v1/tasks/{created.Id}", Json("""{"recurrence":null,"reminderOffsets":[],"repeatReminderMinutes":null}""")))
            .Content.ReadFromJsonAsync<TaskDto>();
        Assert.Null(patched!.Recurrence);
        Assert.Empty(patched.ReminderOffsets!);
        Assert.Null(patched.NextReminderUtc);

        var bad = await http.PatchAsync($"/v1/tasks/{created.Id}", Json("""{"repeatReminderMinutes":"lots"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var badRule = await http.PatchAsync($"/v1/tasks/{created.Id}", Json("""{"recurrence":"whenever"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, badRule.StatusCode);
    }

    [Fact]
    public async Task Show_a_banner_from_another_app()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        http.DefaultRequestHeaders.Add("X-TaskHub-Client", "greeter");

        var response = await http.PostAsync("/v1/notifications",
            Json("""{"title":"Good morning!","body":"3 tasks today","priority":"high","seconds":5,"buttons":[{"action":"show-today","label":"Show"}]}"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<NotifyResult>())!.Id;
        var shown = Assert.Single(api.Surface.Shown);
        Assert.Equal(id, shown.Id);
        Assert.Equal("Good morning!", shown.Title);
        Assert.Equal(TaskHub.Core.Notifications.NotificationPriority.High, shown.Priority);
        Assert.Equal(TimeSpan.FromSeconds(5), shown.Duration);
        Assert.Equal("api:greeter", shown.Source);

        // a click comes back as an event the app can subscribe to
        api.Surface.Click(id, "show-today");
        var e = await api.Core.WaitForEventAsync(EventTypes.NotificationAction);
        Assert.Equal("show-today", e.DataAs<NotificationActionEvent>()!.Action);

        var history = await http.GetFromJsonAsync<List<NotificationHistoryDto>>("/v1/notifications/history");
        Assert.Equal("Good morning!", history!.Single().Title);
    }

    [Theory]
    [InlineData("""{"title":""}""", "title")]
    [InlineData("""{"title":"x","priority":"meh"}""", "priority")]
    [InlineData("""{"title":"x","seconds":-1}""", "seconds")]
    [InlineData("""{"title":"x","buttons":[{"action":"Bad Action!","label":"x"}]}""", "action")]
    public async Task Bad_notifications_are_400(string body, string mentions)
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();
        var response = await http.PostAsync("/v1/notifications", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(mentions, (await response.Content.ReadFromJsonAsync<ApiError>())!.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(api.Surface.Shown);
    }

    [Fact]
    public async Task Do_not_disturb_over_the_api()
    {
        await using var api = await ApiHarness.CreateAsync();
        using var http = api.Client();

        Assert.False((await http.GetFromJsonAsync<DndDto>("/v1/notifications/dnd"))!.Enabled);
        var on = await (await http.PutAsJsonAsync("/v1/notifications/dnd", new DndDto(true))).Content.ReadFromJsonAsync<DndDto>();
        Assert.True(on!.Enabled);

        await http.PostAsync("/v1/notifications", Json("""{"title":"held"}"""));
        Assert.Empty(api.Surface.Shown);
        await http.PutAsJsonAsync("/v1/notifications/dnd", new DndDto(false));
        Assert.Equal("held", Assert.Single(api.Surface.Shown).Title);
    }
}
