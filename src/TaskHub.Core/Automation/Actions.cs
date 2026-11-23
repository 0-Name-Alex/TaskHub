using System.Text.Json.Nodes;

namespace TaskHub.Core.Automation;

/// <summary>Where an action came from; decides what it is allowed to do.</summary>
public enum ActionOrigin
{
    /// <summary>A rule in config.toml (fully trusted: only the user edits that file).</summary>
    Rule,
    /// <summary>A task's "when reminded" actions. Tasks can also be written by the API, imports and
    /// plugins, so starting programs from them needs <c>[automation] allow_task_programs = true</c>.</summary>
    Task,
    /// <summary>Run by hand from the UI or launcher.</summary>
    Manual,
}

/// <summary>Everything an action needs to run once.</summary>
public sealed record ActionInvocation(ActionSpec Spec, JsonObject Context, ActionOrigin Origin, string Source)
{
    /// <summary>The task the triggering event is about (<c>task.id</c>), if any.</summary>
    public Guid? TaskId => Condition.Resolve(Context, "task.id") is string s && Guid.TryParse(s, out var id) ? id : null;

    /// <summary>An argument with its <c>{placeholders}</c> filled in, or null when absent.</summary>
    public string? Text(string key) => Spec.Get(key) is { } raw ? Template.Render(raw, Context) : null;

    public string RequireText(string key) => Template.Render(Spec.Require(key), Context);
}

/// <summary>A thing a rule can do. Implementations must be thread-safe.</summary>
public interface IAutomationAction
{
    /// <summary>Canonical name (see <see cref="ActionNames"/>).</summary>
    string Name { get; }

    /// <summary>Runs the action. Returns a short result for the activity log, or null.
    /// Throws <see cref="AutomationException"/> for expected failures (shown without a stack trace).</summary>
    Task<string?> ExecuteAsync(ActionInvocation invocation, CancellationToken ct);
}

// ---------------------------------------------------------------- platform services (implemented on Windows)

/// <summary>How to start a program for the <c>run</c> action.</summary>
public sealed record ProcessStart
{
    /// <summary>Program to start: a path, or a name on PATH (python, pwsh, node …).</summary>
    public required string Program { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    /// <summary>Hide the console window.</summary>
    public bool Hidden { get; init; } = true;
    /// <summary>Wait for it to finish (and capture output) instead of fire-and-forget.</summary>
    public bool Wait { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(1);
}

public sealed record ProcessOutcome(int? ExitCode, string Output, bool TimedOut);

/// <summary>Opens things and starts programs. The Windows implementation uses ShellExecute / Process.</summary>
public interface IShellLauncher
{
    /// <summary>Opens a URL, document, folder or program the way Explorer would.</summary>
    void Open(string target, string? arguments = null, string? workingDirectory = null);

    /// <summary>Starts a program directly (no shell): arguments are passed exactly as given.</summary>
    Task<ProcessOutcome> RunAsync(ProcessStart start, CancellationToken ct);
}

/// <summary>What was on the clipboard.</summary>
public sealed record ClipboardContent(string? Text, byte[]? PngImage, IReadOnlyList<string> Files)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && PngImage is null && Files.Count == 0;
}

public interface IClipboardService
{
    ClipboardContent Read();
    void SetText(string text);
}

/// <summary>Lets the user drag a rectangle on screen; returns the selected area as a PNG, or null if cancelled.</summary>
public interface IScreenCapture
{
    Task<byte[]?> SelectRegionAsync(CancellationToken ct);
}
