using System.Text.RegularExpressions;

namespace TaskHub.Core.Events;

/// <summary>Event type names and subscription patterns.</summary>
public static partial class TopicPattern
{
    [GeneratedRegex("^[a-z0-9_-]+(\\.[a-z0-9_-]+)*$")]
    private static partial Regex TypeRegex();

    /// <summary>True for names like <c>task.created</c>: lowercase dotted segments of [a-z0-9_-].</summary>
    public static bool IsValidType(string? type) => type is not null && TypeRegex().IsMatch(type);

    /// <summary>Valid patterns: <c>*</c>, an exact type, or a type prefix followed by <c>.*</c>.</summary>
    public static bool IsValidPattern(string? pattern)
    {
        if (pattern is null) return false;
        if (pattern == "*") return true;
        if (pattern.EndsWith(".*", StringComparison.Ordinal)) return IsValidType(pattern[..^2]);
        return IsValidType(pattern);
    }

    /// <summary>
    /// <c>*</c> matches everything; <c>task.*</c> matches <c>task.created</c> and
    /// <c>task.sub.thing</c> but not <c>task</c>; anything else is an exact match.
    /// </summary>
    public static bool Matches(string pattern, string type)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = pattern.AsSpan(0, pattern.Length - 1); // keep the trailing dot
            return type.Length > prefix.Length && type.AsSpan().StartsWith(prefix, StringComparison.Ordinal);
        }
        return string.Equals(pattern, type, StringComparison.Ordinal);
    }
}
