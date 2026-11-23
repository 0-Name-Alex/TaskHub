using TaskHub.Core.Services;

namespace TaskHub.Core.Domain;

/// <summary>
/// The icons a list can have. Lists store the key ("home"); the UI maps it to a Segoe Fluent Icons glyph.
/// Keys are stable — don't rename them, they're saved in the database and in exports.
/// </summary>
public static class ListIcons
{
    public const string Default = "folder";

    /// <summary>Key, display name and glyph, in menu order.</summary>
    public static readonly IReadOnlyList<(string Key, string Name, string Glyph)> All =
    [
        ("folder", "Folder", ""),
        ("home", "Home", ""),
        ("work", "Work", ""),
        ("school", "School", ""),
        ("shopping", "Shopping", ""),
        ("money", "Money", ""),
        ("health", "Health", ""),
        ("star", "Star", ""),
        ("flag", "Flag", ""),
        ("calendar", "Calendar", ""),
        ("people", "People", ""),
        ("travel", "Travel", ""),
        ("car", "Car", ""),
        ("code", "Code", ""),
        ("music", "Music", ""),
        ("idea", "Idea", ""),
        ("game", "Games", ""),
        ("camera", "Camera", ""),
        ("mail", "Mail", ""),
        ("phone", "Phone", ""),
        ("globe", "Web", ""),
        ("pin", "Pin", ""),
    ];

    public static bool IsKnown(string? key) =>
        key is not null && All.Any(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The glyph for a key; unknown or empty keys get the folder.</summary>
    public static string GlyphFor(string? key) =>
        All.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)).Glyph ?? All[0].Glyph;

    /// <summary>Null/empty/"folder" → null (the default); a known key → its canonical lower-case form.</summary>
    public static string? Normalize(string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        var match = All.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
        if (match.Key is null)
            throw new TaskValidationException($"Unknown list icon '{key}'. Use one of: {string.Join(", ", All.Select(i => i.Key))}.");
        return match.Key == Default ? null : match.Key;
    }
}
