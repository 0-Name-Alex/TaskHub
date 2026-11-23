using System.Globalization;
using System.Text;

namespace TaskHub.Core.Automation;

/// <summary>
/// One step of a rule or of a task's reminder actions: an action name (<c>open</c>, <c>notify</c>, …)
/// plus its arguments. Written in config.toml either as a line of text (<c>"open https://example.com"</c>)
/// or as an inline table (<c>{ action = "open", target = "https://example.com" }</c>).
/// </summary>
public sealed record ActionSpec(string Name, IReadOnlyDictionary<string, string> Args)
{
    /// <summary>The original line, when it was written as text (shown in the UI).</summary>
    public string? Text { get; init; }

    public string? Get(string key) => Args.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public string Require(string key) =>
        Get(key) ?? throw new AutomationException($"'{Name}' needs a value for '{key}'.");

    public int? GetInt(string key)
    {
        if (Get(key) is not { } s) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new AutomationException($"'{Name}': {key} must be a whole number (got '{s}').");
    }

    public bool GetBool(string key, bool fallback)
    {
        if (Get(key) is not { } s) return fallback;
        return s.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new AutomationException($"'{Name}': {key} must be true or false (got '{s}')."),
        };
    }

    /// <summary>Short human description, e.g. <c>open https://meet.example.com</c>.</summary>
    public string Describe()
    {
        if (Text is not null) return Text;
        var sb = new StringBuilder(Name);
        foreach (var (k, v) in Args)
        {
            sb.Append(' ').Append(k).Append('=');
            sb.Append(v.Contains(' ', StringComparison.Ordinal) ? $"\"{v}\"" : v);
        }
        return sb.ToString();
    }

    public static ActionSpec Create(string name, params (string Key, string Value)[] args) =>
        new(name, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));
}

/// <summary>A rule, action or condition that can't be parsed or run. The message is shown to the user.</summary>
public sealed class AutomationException(string message) : Exception(message);
