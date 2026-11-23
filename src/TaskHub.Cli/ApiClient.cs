using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskHub.Abstractions.Contracts;

namespace TaskHub.Cli;

public sealed class CliException(string message) : Exception(message);

/// <summary>Typed client for the TaskHub local API. Usable from any .NET app, not just the CLI.</summary>
public sealed class ApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly HttpClient _http;

    public ApiClient(HttpClient http) => _http = http;

    public static ApiClient Create(string baseUrl, string token, string clientName = "cli")
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Add("X-TaskHub-Client", clientName);
        return new ApiClient(http);
    }

    /// <summary>Finds the running app via api.json (honours TASKHUB_HOME).</summary>
    public static ApiClient FromDiscovery(string clientName = "cli")
    {
        var discovery = ApiDiscovery.TryRead()
            ?? throw new CliException($"TaskHub doesn't seem to be running (no {ApiDiscovery.DefaultPath()}). Start TaskHub and try again.");
        string token;
        try
        {
            token = discovery.ReadToken();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"Can't read the API token at {discovery.TokenFile}: {ex.Message}");
        }
        return Create(discovery.BaseUrl, token, clientName);
    }

    public Task<StatusDto> StatusAsync(CancellationToken ct = default) => GetAsync<StatusDto>("/v1/status", ct);

    public Task<List<TaskDto>> ListTasksAsync(string? view = null, string? search = null, string? tag = null, Guid? listId = null,
        bool includeCompleted = false, int? limit = null, CancellationToken ct = default)
    {
        var q = new List<string>();
        if (view is not null) q.Add("view=" + Uri.EscapeDataString(view));
        if (search is not null) q.Add("q=" + Uri.EscapeDataString(search));
        if (tag is not null) q.Add("tag=" + Uri.EscapeDataString(tag));
        if (listId is not null) q.Add("list=" + listId);
        if (includeCompleted) q.Add("includeCompleted=true");
        if (limit is not null) q.Add("limit=" + limit);
        return GetAsync<List<TaskDto>>("/v1/tasks" + (q.Count > 0 ? "?" + string.Join('&', q) : ""), ct);
    }

    public Task<TaskDto> GetTaskAsync(Guid id, CancellationToken ct = default) => GetAsync<TaskDto>($"/v1/tasks/{id}", ct);

    public Task<TaskDto> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Post, "/v1/tasks", request, ct);

    /// <summary>Only the given properties change; pass null values to clear (e.g. dueDate).</summary>
    public Task<TaskDto> PatchTaskAsync(Guid id, IReadOnlyDictionary<string, object?> changes, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Patch, $"/v1/tasks/{id}", changes, ct);

    public Task<TaskDto> CompleteAsync(Guid id, CancellationToken ct = default) => SendAsync<TaskDto>(HttpMethod.Post, $"/v1/tasks/{id}/complete", null, ct);
    public Task<TaskDto> ReopenAsync(Guid id, CancellationToken ct = default) => SendAsync<TaskDto>(HttpMethod.Post, $"/v1/tasks/{id}/reopen", null, ct);
    public Task<TaskDto> RestoreAsync(Guid id, CancellationToken ct = default) => SendAsync<TaskDto>(HttpMethod.Post, $"/v1/tasks/{id}/restore", null, ct);
    public Task<TaskDto> SnoozeAsync(Guid id, int? minutes = null, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Post, $"/v1/tasks/{id}/snooze", new SnoozeRequest(minutes), ct);
    public Task<TaskDto> DeleteAsync(Guid id, CancellationToken ct = default) => SendAsync<TaskDto>(HttpMethod.Delete, $"/v1/tasks/{id}", null, ct);

    public async Task<string?> UndoAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("/v1/undo", null, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<UndoResultDto>(Json, ct))!.Description;
    }

    public Task<List<TaskListDto>> ListListsAsync(CancellationToken ct = default) => GetAsync<List<TaskListDto>>("/v1/lists", ct);
    public Task<TaskListDto> CreateListAsync(string name, CancellationToken ct = default) =>
        SendAsync<TaskListDto>(HttpMethod.Post, "/v1/lists", new CreateListRequest(name), ct);
    public Task<List<TagDto>> ListTagsAsync(CancellationToken ct = default) => GetAsync<List<TagDto>>("/v1/tags", ct);

    public Task<NotifyResult> NotifyAsync(NotifyRequest request, CancellationToken ct = default) =>
        SendAsync<NotifyResult>(HttpMethod.Post, "/v1/notifications", request, ct);
    public Task<DndDto> GetDndAsync(CancellationToken ct = default) => GetAsync<DndDto>("/v1/notifications/dnd", ct);
    public Task<DndDto> SetDndAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync<DndDto>(HttpMethod.Put, "/v1/notifications/dnd", new DndDto(enabled), ct);

    public Task<ExportDocument> ExportAsync(CancellationToken ct = default) => GetAsync<ExportDocument>("/v1/export", ct);
    public Task<ImportResult> ImportAsync(ExportDocument doc, CancellationToken ct = default) =>
        SendAsync<ImportResult>(HttpMethod.Post, "/v1/import", doc, ct);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await Call(() => _http.GetAsync(path, ct));
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var response = await Call(() => _http.SendAsync(request, ct));
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static async Task<HttpResponseMessage> Call(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException ex)
        {
            throw new CliException($"Can't reach TaskHub ({ex.Message}). Is it running?");
        }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try { message = (await response.Content.ReadFromJsonAsync<ApiError>(Json, ct))?.Error; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        throw new CliException(message ?? $"TaskHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    public void Dispose() => _http.Dispose();
}
