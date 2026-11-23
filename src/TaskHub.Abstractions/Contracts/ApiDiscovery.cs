using System.Text.Json;

namespace TaskHub.Abstractions.Contracts;

/// <summary>
/// Written by the running app to <c>%LOCALAPPDATA%\TaskHub\api.json</c> so other apps (CLI,
/// greeter, scripts) can find the local API. Read the token from <see cref="TokenFile"/> and
/// send it as <c>Authorization: Bearer &lt;token&gt;</c>.
/// </summary>
public sealed record ApiDiscovery(int Port, string TokenFile, int ProcessId, string Version)
{
    public const string FileName = "api.json";
    public const string HomeEnvironmentVariable = "TASKHUB_HOME";

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The data folder of the default (or TASKHUB_HOME) instance.</summary>
    public static string DefaultDataRoot()
    {
        var custom = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskHub");
    }

    public static string DefaultPath() => Path.Combine(DefaultDataRoot(), FileName);

    public static ApiDiscovery? TryRead(string? path = null)
    {
        path ??= DefaultPath();
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ApiDiscovery>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public string ReadToken() => File.ReadAllText(TokenFile).Trim();
}
