using System.IO;
using Microsoft.Extensions.Logging;
using TaskHub.Core.Launcher;

namespace TaskHub.Automation;

/// <summary>
/// Apps for the quick launcher: the shortcuts in the Start menu (yours and all users'), cached for a few
/// minutes. Store apps without a Start-menu shortcut file aren't included.
/// </summary>
public sealed class StartMenuCatalog(ILogger<StartMenuCatalog> log) : IAppCatalog
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly string[] Extensions = [".lnk", ".url", ".appref-ms"];
    private static readonly string[] Skip = ["uninstall", "remove ", "readme", "release notes", "license"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AppEntry> _cache = [];
    private DateTime _loadedAt = DateTime.MinValue;

    public async Task<IReadOnlyList<AppEntry>> GetAppsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _loadedAt < CacheFor) return _cache;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - _loadedAt < CacheFor) return _cache;
            _cache = await Task.Run(Scan, ct).ConfigureAwait(false);
            _loadedAt = DateTime.UtcNow;
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<AppEntry> Scan()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        };
        var byName = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", options))
                {
                    if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (Skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                    var folder = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
                    byName.TryAdd(name, new AppEntry(name, file, folder == "." ? null : folder));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Could not scan {Root}", root);
            }
        }
        log.LogDebug("Found {Count} Start menu apps", byName.Count);
        return byName.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
