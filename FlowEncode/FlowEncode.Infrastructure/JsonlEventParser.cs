using System.Text.Json;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal sealed record StructuredAv1anEvent(
    string Type,
    DateTimeOffset Timestamp,
    JsonElement Payload);

internal static class JsonlEventParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryParse(string line, out StructuredAv1anEvent? parsedEvent)
    {
        parsedEvent = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty)
                || !root.TryGetProperty("ts", out var timestampProperty))
            {
                return false;
            }

            var type = typeProperty.GetString();
            var timestampText = timestampProperty.GetString();
            if (string.IsNullOrWhiteSpace(type)
                || string.IsNullOrWhiteSpace(timestampText)
                || !DateTimeOffset.TryParse(timestampText, out var timestamp))
            {
                return false;
            }

            parsedEvent = new StructuredAv1anEvent(
                type,
                timestamp,
                JsonSerializer.Deserialize<JsonElement>(root.GetRawText(), JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static AutoCompressionExecutionStage MapStage(string eventType)
    {
        return eventType switch
        {
            "run_started" => AutoCompressionExecutionStage.Preparing,
            "input_probed" => AutoCompressionExecutionStage.InputProbing,
            "scene_detect" => AutoCompressionExecutionStage.SceneDetection,
            "chunk_plan" => AutoCompressionExecutionStage.ChunkPlanning,
            "probe" => AutoCompressionExecutionStage.Probing,
            "encode" or "encode_progress" => AutoCompressionExecutionStage.Encoding,
            "concat" => AutoCompressionExecutionStage.Concatenating,
            "run_completed" => AutoCompressionExecutionStage.Completed,
            "run_failed" => AutoCompressionExecutionStage.Failed,
            "run_cancelled" => AutoCompressionExecutionStage.Cancelled,
            _ => AutoCompressionExecutionStage.Preparing
        };
    }
}
