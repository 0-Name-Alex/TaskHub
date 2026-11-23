using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Hosting;

namespace TaskHub.Core.Automation;

/// <summary>
/// What conditions and <c>{placeholders}</c> can see: the event's payload at the top level
/// (e.g. <c>task.title</c>, <c>path</c>, <c>percent</c>), plus <c>event.type</c>, <c>event.source</c>,
/// <c>now</c> ("2026-11-03 09:30"), <c>today</c>, <c>time</c>, <c>weekday</c> and <c>hour</c> (local).
/// </summary>
public static class AutomationContext
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JsonObject FromEvent(EventEnvelope e, IClock clock)
    {
        JsonObject root;
        JsonNode? data = null;
        if (e.Data is not null)
        {
            try
            {
                data = JsonSerializer.SerializeToNode(e.Data, e.Data.GetType(), Json);
            }
            catch (NotSupportedException)
            {
                data = JsonValue.Create(e.Data.ToString());
            }
        }
        root = data as JsonObject ?? new JsonObject();
        if (data is not null and not JsonObject) root["data"] = data;

        root["event"] = new JsonObject
        {
            ["type"] = e.Type,
            ["source"] = e.Source,
            ["id"] = e.Id.ToString(),
            ["time"] = e.Time.ToString("O", CultureInfo.InvariantCulture),
        };
        AddClock(root, clock);
        return root;
    }

    /// <summary>A context with no event (rule run by hand, launcher).</summary>
    public static JsonObject Manual(IClock clock, string type = "automation.manual", string source = EventSources.Ui)
    {
        var root = new JsonObject
        {
            ["event"] = new JsonObject { ["type"] = type, ["source"] = source },
        };
        AddClock(root, clock);
        return root;
    }

    private static void AddClock(JsonObject root, IClock clock)
    {
        var local = clock.LocalNow();
        TryAdd(root, "now", local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        TryAdd(root, "today", local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        TryAdd(root, "time", local.ToString("HH:mm", CultureInfo.InvariantCulture));
        TryAdd(root, "weekday", local.DayOfWeek.ToString());
        if (!root.ContainsKey("hour")) root["hour"] = local.Hour;
    }

    private static void TryAdd(JsonObject root, string key, string value)
    {
        if (!root.ContainsKey(key)) root[key] = value;
    }

    /// <summary>
    /// Flattens the context into environment variables for scripts: <c>TASKHUB_TASK_TITLE</c>,
    /// <c>TASKHUB_PATH</c>, … plus <c>TASKHUB_EVENT_JSON</c> with everything. Scripts get event data
    /// this way instead of having it pasted into a command line, so odd file names can't inject commands.
    /// </summary>
    public static Dictionary<string, string> ToEnvironment(JsonObject context)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TASKHUB_EVENT_JSON"] = context.ToJsonString(),
        };
        Flatten(context, "TASKHUB", env, depth: 0);
        return env;
    }

    private static void Flatten(JsonNode? node, string prefix, Dictionary<string, string> env, int depth)
    {
        if (depth > 4 || env.Count > 200) return;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                    Flatten(value, prefix + "_" + EnvName(key), env, depth + 1);
                break;
            default:
                var text = Condition.Format(Condition.ToValue(node));
                if (text.Length <= 8000) env[prefix] = text;
                break;
        }
    }

    private static string EnvName(string key)
    {
        // camelCase → CAMEL_CASE
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (char.IsUpper(c) && i > 0 && char.IsLower(key[i - 1])) sb.Append('_');
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Fills <c>{task.title}</c>-style placeholders from an <see cref="AutomationContext"/>.
/// <c>{{</c> and <c>}}</c> give literal braces; unknown fields become empty; anything that isn't a
/// field name (e.g. JSON) is left as written.
/// </summary>
public static class Template
{
    public static string Render(string template, JsonObject context)
    {
        if (template.IndexOf('{', StringComparison.Ordinal) < 0 && template.IndexOf('}', StringComparison.Ordinal) < 0) return template;
        var sb = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                sb.Append('{');
                i += 2;
                continue;
            }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                sb.Append('}');
                i += 2;
                continue;
            }
            if (c == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close > i + 1)
                {
                    var path = template[(i + 1)..close].Trim();
                    if (IsPath(path))
                    {
                        sb.Append(Condition.Format(Condition.Resolve(context, path)));
                        i = close + 1;
                        continue;
                    }
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsPath(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-');
}
