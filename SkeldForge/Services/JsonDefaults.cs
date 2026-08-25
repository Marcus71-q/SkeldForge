using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkeldForge.Services;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    public static JsonSerializerOptions SerializerOptions => Options;

    private static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };
    }
}
