using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskHub.Api;

public static class ApiJson
{
    /// <summary>camelCase, nulls omitted. Same settings the CLI and other .NET clients should use.</summary>
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return o;
    }
}
