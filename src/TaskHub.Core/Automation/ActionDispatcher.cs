using Microsoft.Extensions.Logging;
using TaskHub.Core.Configuration;

namespace TaskHub.Core.Automation;

/// <summary>
/// Finds the implementation for an action and runs it — after checking what the action's origin
/// is allowed to do. Rules (config.toml) may do anything; a task's reminder actions may only open
/// web/mail links unless <c>[automation] allow_task_programs = true</c>.
/// </summary>
public sealed class ActionDispatcher
{
    private static readonly string[] SafeSchemes = ["http", "https", "mailto", "msteams", "zoommtg", "zoomus", "tel"];

    private readonly Dictionary<string, IAutomationAction> _actions;
    private readonly Func<AutomationOptions> _options;
    private readonly ILogger<ActionDispatcher> _log;

    public ActionDispatcher(IEnumerable<IAutomationAction> actions, Func<AutomationOptions> options, ILogger<ActionDispatcher> log)
    {
        _actions = new Dictionary<string, IAutomationAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in actions) _actions[a.Name] = a;
        _options = options;
        _log = log;
    }

    public bool Has(string name) => _actions.ContainsKey(name);

    public async Task<string?> ExecuteAsync(ActionInvocation invocation, CancellationToken ct)
    {
        var spec = invocation.Spec;
        if (!_actions.TryGetValue(spec.Name, out var action))
            throw new AutomationException($"'{spec.Name}' isn't available here.");

        if (invocation.Origin == ActionOrigin.Task && !_options().AllowTaskPrograms)
        {
            if (spec.Name == ActionNames.Run)
                throw new AutomationException("Starting programs from a task is turned off. Set allow_task_programs = true under [automation] in the config file to allow it.");
            if (spec.Name == ActionNames.Open && !IsSafeLink(invocation.Text("target")))
                throw new AutomationException("A task can only open web and mail links. Set allow_task_programs = true under [automation] to open files and programs too.");
        }

        _log.LogDebug("Running action {Action} ({Origin}, {Source})", spec.Describe(), invocation.Origin, invocation.Source);
        return await action.ExecuteAsync(invocation, ct).ConfigureAwait(false);
    }

    /// <summary>http(s), mailto and a few meeting-app links.</summary>
    public static bool IsSafeLink(string? target) =>
        target is not null
        && Uri.TryCreate(target.Trim(), UriKind.Absolute, out var uri)
        && !uri.IsFile && !uri.IsUnc
        && SafeSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);
}
