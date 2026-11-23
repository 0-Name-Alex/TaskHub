using System.Globalization;
using Microsoft.Extensions.Logging;
using TaskHub.Core.Notifications;
using TaskHub.Core.Services;

namespace TaskHub.Core.Automation;

/// <summary><c>notify Title | Body</c> or <c>{ action = "notify", title, body, priority, seconds, icon }</c>.</summary>
public sealed class NotifyAction(INotificationPresenter presenter) : IAutomationAction
{
    public string Name => ActionNames.Notify;

    public Task<string?> ExecuteAsync(ActionInvocation inv, CancellationToken ct)
    {
        var title = inv.RequireText("title");
        var priority = inv.Text("priority")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "normal" or "none" or "medium" => NotificationPriority.Normal,
            "low" => NotificationPriority.Low,
            "high" => NotificationPriority.High,
            "urgent" => NotificationPriority.Urgent,
            var p => throw new AutomationException($"notify: priority must be low, normal, high or urgent (got '{p}')."),
        };
        var seconds = inv.Spec.GetInt("seconds");
        if (seconds is < 0 or > 3600) throw new AutomationException("notify: seconds must be between 0 and 3600.");
        var taskId = inv.TaskId;
        presenter.Show(new NotificationRequest
        {
            Title = title,
            Body = inv.Text("body"),
            Icon = inv.Text("icon"),
            Priority = priority,
            Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null,
            TaskIds = taskId is { } id ? new[] { id } : Array.Empty<Guid>(),
            Buttons = taskId is null
                ? new[] { new NotificationButton(NotificationActions.Dismiss, "OK") }
                : new[] { new NotificationButton(NotificationActions.Open, "Open"), new NotificationButton(NotificationActions.Dismiss, "OK") },
            Source = inv.Source,
        });
        return Task.FromResult<string?>($"Showed “{title}”");
    }
}

/// <summary><c>add Pay {name} tomorrow #finance</c> — quick-add syntax.</summary>
public sealed class TaskAddAction(TaskService tasks) : IAutomationAction
{
    public string Name => ActionNames.TaskAdd;

    public async Task<string?> ExecuteAsync(ActionInvocation inv, CancellationToken ct)
    {
        var text = inv.RequireText("text").Trim();
        if (text.Length == 0) throw new AutomationException("add: the task text is empty after filling in placeholders.");
        try
        {
            var task = await tasks.QuickAddAsync(text, inv.Source, ct).ConfigureAwait(false);
            if (inv.Text("notes") is { Length: > 0 } notes)
                task = await tasks.UpdateAsync(task.Id, new TaskPatch { Notes = notes }, inv.Source, ct).ConfigureAwait(false);
            return $"Added “{task.Title}”";
        }
        catch (TaskValidationException ex)
        {
            throw new AutomationException($"add: {ex.Message}");
        }
    }
}

/// <summary><c>done</c> — completes the task the event is about (or <c>id = "…"</c>).</summary>
public sealed class TaskCompleteAction(TaskService tasks) : IAutomationAction
{
    public string Name => ActionNames.TaskComplete;

    public async Task<string?> ExecuteAsync(ActionInvocation inv, CancellationToken ct)
    {
        var id = TaskTarget.Resolve(inv, "done");
        try
        {
            var task = await tasks.CompleteAsync(id, inv.Source, ct).ConfigureAwait(false);
            return $"Completed “{task.Title}”";
        }
        catch (Exception ex) when (ex is TaskValidationException or TaskNotFoundException)
        {
            throw new AutomationException($"done: {ex.Message}");
        }
    }
}

/// <summary><c>snooze 15</c> — snoozes the task the event is about.</summary>
public sealed class TaskSnoozeAction(TaskService tasks) : IAutomationAction
{
    public string Name => ActionNames.TaskSnooze;

    public async Task<string?> ExecuteAsync(ActionInvocation inv, CancellationToken ct)
    {
        var id = TaskTarget.Resolve(inv, "snooze");
        var minutes = inv.Spec.GetInt("minutes") ?? tasks.Reminders.Options.SnoozeMinutes;
        try
        {
            var task = await tasks.SnoozeAsync(id, TimeSpan.FromMinutes(minutes), inv.Source, ct).ConfigureAwait(false);
            return $"Snoozed “{task.Title}” {minutes} min";
        }
        catch (Exception ex) when (ex is TaskValidationException or TaskNotFoundException)
        {
            throw new AutomationException($"snooze: {ex.Message}");
        }
    }
}

internal static class TaskTarget
{
    public static Guid Resolve(ActionInvocation inv, string verb)
    {
        if (inv.Text("id") is { } text)
            return Guid.TryParse(text, out var explicitId) ? explicitId : throw new AutomationException($"{verb}: '{text}' is not a task id.");
        return inv.TaskId ?? throw new AutomationException($"{verb}: this event isn't about a task (use it with reminder.due or task.* events).");
    }
}

/// <summary><c>dnd on|off|toggle</c>.</summary>
public sealed class DndAction(IDoNotDisturb dnd) : IAutomationAction
{
    public string Name => ActionNames.Dnd;

    public Task<string?> ExecuteAsync(ActionInvocation inv, CancellationToken ct)
    {
        var enable = (inv.Text("state") ?? "toggle").Trim().ToLowerInvariant() switch
        {
            "on" or "true" => true,
            "off" or "false" => false,
            "toggle" => !dnd.DoNotDisturb,
            var s => throw new AutomationException($"dnd: use on, off or toggle (got '{s}')."),
        };
        dnd.SetDoNotDisturb(enable);
        return Task.FromResult<string?>(enable ? "Do Not Disturb on" : "Do Not Disturb off");
    }
}

/// <summary><c>log message</c> — writes to the TaskHub log.</summary>
public sealed class LogAction(ILogger<LogAction> log) : IAutomationAction
{
    public string Name => ActionNames.Log;

    public Task<string?> ExecuteAsync(ActionInvocation inv, CancellationToken ct)
    {
        var message = inv.Text("message") ?? "";
        log.LogInformation("[{Source}] {Message}", inv.Source, message);
        return Task.FromResult<string?>(message.Length == 0 ? null : message);
    }
}

/// <summary><c>wait 5</c> — pause between steps (max 5 minutes).</summary>
public sealed class WaitAction : IAutomationAction
{
    public string Name => ActionNames.Wait;

    public async Task<string?> ExecuteAsync(ActionInvocation inv, CancellationToken ct)
    {
        var text = inv.Spec.Require("seconds");
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds is <= 0 or > 300)
            throw new AutomationException("wait: seconds must be between 0 and 300.");
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        return null;
    }
}

/// <summary><c>copy {task.title}</c>.</summary>
public sealed class ClipboardSetAction(IClipboardService clipboard) : IAutomationAction
{
    public string Name => ActionNames.ClipboardSet;

    public Task<string?> ExecuteAsync(ActionInvocation inv, CancellationToken ct)
    {
        var text = inv.RequireText("text");
        clipboard.SetText(text);
        return Task.FromResult<string?>("Copied to the clipboard");
    }
}

/// <summary>
/// <c>open https://…</c>, <c>open "C:\file.pdf"</c>, <c>open notepad.exe "C:\notes.txt"</c> — like double-clicking it.
/// Table form: <c>{ action = "open", target = "…", args = "…", working_dir = "…" }</c>.
/// </summary>
public sealed class OpenAction(IShellLauncher shell) : IAutomationAction
{
    public string Name => ActionNames.Open;

    public Task<string?> ExecuteAsync(ActionInvocation inv, CancellationToken ct)
    {
        var target = Environment.ExpandEnvironmentVariables(inv.RequireText("target").Trim());
        if (target.StartsWith('~')) target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), target[1..].TrimStart('/', '\\'));
        try
        {
            shell.Open(target, inv.Text("args"), inv.Text("working_dir"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new AutomationException($"open: couldn't open '{target}': {ex.Message}");
        }
        return Task.FromResult<string?>($"Opened {target}");
    }
}

/// <summary>
/// Starts a program directly — never through a shell, so event data can't inject commands.
/// <code>
/// run python "C:\scripts\sort.py" {path}
/// { action = "run", program = "pwsh", args = ["-File", "C:\x.ps1"], wait = true, timeout = 120 }
/// { action = "run", shell = "pwsh", script = "Get-Date | Out-File $env:TEMP\x.txt" }
/// </code>
/// <c>{placeholders}</c> work in <c>args</c> (each argument stays one argument). Scripts get the event
/// as environment variables (<c>TASKHUB_TASK_TITLE</c>, <c>TASKHUB_PATH</c>, <c>TASKHUB_EVENT_JSON</c>, …);
/// placeholders are not filled into <c>script</c> text.
/// </summary>
public sealed class RunAction(IShellLauncher shell) : IAutomationAction
{
    public string Name => ActionNames.Run;

    public async Task<string?> ExecuteAsync(ActionInvocation inv, CancellationToken ct)
    {
        var spec = inv.Spec;
        string program;
        List<string> args;
        if (spec.Get("script") is { } script)
        {
            var shellName = (spec.Get("shell") ?? "pwsh").Trim().ToLowerInvariant();
            (program, args) = shellName switch
            {
                "pwsh" => ("pwsh", new List<string> { "-NoProfile", "-NonInteractive", "-Command", script }),
                "powershell" => ("powershell.exe", new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }),
                "cmd" => ("cmd.exe", new List<string> { "/d", "/c", script }),
                "python" or "py" => ("python", new List<string> { "-c", script }),
                "node" => ("node", new List<string> { "-e", script }),
                _ => throw new AutomationException($"run: unknown shell '{shellName}'. Use pwsh, powershell, cmd, python or node."),
            };
        }
        else
        {
            program = Environment.ExpandEnvironmentVariables(spec.Require("program").Trim());
            // Split first, then fill placeholders, so a value with spaces stays one argument.
            args = ActionScript.SplitArguments(spec.Get("args")).Select(a => Template.Render(a, inv.Context)).ToList();
        }

        var timeout = spec.GetInt("timeout") ?? 60;
        if (timeout is < 1 or > 3600) throw new AutomationException("run: timeout must be between 1 and 3600 seconds.");
        var wait = spec.GetBool("wait", false);
        var start = new ProcessStart
        {
            Program = program,
            Arguments = args,
            WorkingDirectory = inv.Text("working_dir") is { Length: > 0 } wd ? Environment.ExpandEnvironmentVariables(wd) : null,
            Environment = AutomationContext.ToEnvironment(inv.Context),
            Hidden = spec.GetBool("hidden", true),
            Wait = wait,
            Timeout = TimeSpan.FromSeconds(timeout),
        };

        ProcessOutcome outcome;
        try
        {
            outcome = await shell.RunAsync(start, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new AutomationException($"run: couldn't start '{program}': {ex.Message}");
        }

        if (!wait) return $"Started {Path.GetFileName(program)}";
        if (outcome.TimedOut) throw new AutomationException($"run: {Path.GetFileName(program)} didn't finish within {timeout} s and was stopped.");
        var firstLine = outcome.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (outcome.ExitCode is not 0)
            throw new AutomationException($"run: {Path.GetFileName(program)} exited with code {outcome.ExitCode}{(firstLine is null ? "" : ": " + firstLine)}");
        return firstLine is null ? $"{Path.GetFileName(program)} finished" : $"{Path.GetFileName(program)}: {firstLine}";
    }
}

/// <summary><c>clip</c> / <c>screenshot</c> — same as the capture hotkeys.</summary>
public sealed class CaptureAction(string name, CaptureService capture) : IAutomationAction
{
    public string Name => name;

    public async Task<string?> ExecuteAsync(ActionInvocation inv, CancellationToken ct)
    {
        var task = name == ActionNames.CaptureScreenshot
            ? await capture.CaptureScreenshotAsync(inv.Source, ct).ConfigureAwait(false)
            : await capture.CaptureClipboardAsync(inv.Source, ct).ConfigureAwait(false);
        return task is null ? "Nothing captured" : $"Captured “{task.Title}”";
    }
}
