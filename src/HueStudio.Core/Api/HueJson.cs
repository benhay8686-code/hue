using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HueStudio.Core.Api;

public static class HueJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static T? Deserialize<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Deep-merges <paramref name="patch"/> into <paramref name="target"/>. Objects are merged
    /// recursively; arrays and scalars are replaced. Used to apply partial event-stream updates.
    /// </summary>
    public static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject patchObj && target[key] is JsonObject targetObj)
                Merge(targetObj, patchObj);
            else
                target[key] = value?.DeepClone();
        }
    }
}

/// <summary>Standard CLIP v2 response envelope.</summary>
public sealed class HueResponse<T>
{
    public List<HueError> Errors { get; set; } = [];
    public List<T> Data { get; set; } = [];
}

public sealed class HueError
{
    public string Description { get; set; } = "";
}

public class HueApiException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>Thrown while pairing when the bridge's link button hasn't been pressed (error 101).</summary>
public sealed class LinkButtonNotPressedException()
    : HueApiException("Press the round link button on the Hue Bridge, then try again.", 101);
