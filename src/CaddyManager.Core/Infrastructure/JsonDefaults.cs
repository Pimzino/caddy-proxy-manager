using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaddyManager.Core;

public static class JsonDefaults
{
    /// <summary>API wire format: camelCase properties, camelCase string enums, nulls omitted.</summary>
    public static readonly JsonSerializerOptions Api = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Settings persistence (same as API, but keeps nulls).</summary>
    public static readonly JsonSerializerOptions Storage = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        if (!o.Converters.OfType<JsonStringEnumConverter>().Any())
            o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }
}
