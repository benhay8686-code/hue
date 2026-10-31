using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace HueStudio.Core.Api;

/// <summary>One event from the bridge event stream: type is "add", "update", "delete" or "error".</summary>
public sealed record HueEvent(string Type, string? CreationTime, IReadOnlyList<JsonObject> Data);

/// <summary>Parses the Server-Sent Events wire format used by /eventstream/clip/v2.</summary>
public static class EventStreamParser
{
    public static async IAsyncEnumerable<HueEvent> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break; // stream closed

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    foreach (var evt in ParsePayload(data.ToString())) yield return evt;
                    data.Clear();
                }
                continue;
            }

            if (line.StartsWith(':')) continue; // comment / keep-alive
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
            // "id:" / "event:" / "retry:" are ignored – the payload carries everything we need.
        }

        if (data.Length > 0)
            foreach (var evt in ParsePayload(data.ToString())) yield return evt;
    }

    public static IEnumerable<HueEvent> ParsePayload(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { yield break; }

        if (root is not JsonArray events) yield break;

        foreach (var e in events.OfType<JsonObject>())
        {
            var type = e["type"]?.GetValue<string>() ?? "unknown";
            var created = e["creationtime"]?.GetValue<string>();
            var items = (e["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            yield return new HueEvent(type, created, items);
        }
    }
}
