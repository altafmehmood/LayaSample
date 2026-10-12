using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocIntake.Contracts;

/// <summary>How the services write and read contract types between them: web defaults and enums by name.</summary>
public static class IntakeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
