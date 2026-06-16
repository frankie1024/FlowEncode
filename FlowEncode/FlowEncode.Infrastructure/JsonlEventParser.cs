using System.Text.Json;
using System.Globalization;
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
            "encode" or "encode_progress" or "encoder_log" => AutoCompressionExecutionStage.Encoding,
            "concat" => AutoCompressionExecutionStage.Concatenating,
            "run_completed" => AutoCompressionExecutionStage.Completed,
            "run_failed" => AutoCompressionExecutionStage.Failed,
            "run_cancelled" => AutoCompressionExecutionStage.Cancelled,
            _ => AutoCompressionExecutionStage.Preparing
        };
    }

    public static double? TryGetProgressFraction(StructuredAv1anEvent parsedEvent)
    {
        if (parsedEvent.Type == "run_completed")
        {
            return 1.0;
        }

        if (!TryGetInt32(parsedEvent.Payload, "fraction_done", out var done)
            || !TryGetInt32(parsedEvent.Payload, "fraction_total", out var total)
            || total <= 0
            || done < 0)
        {
            return null;
        }

        return Math.Clamp((double)done / total, 0.0, 1.0);
    }

    public static string? TryGetFailureMessage(StructuredAv1anEvent parsedEvent)
    {
        return parsedEvent.Payload.TryGetProperty("message", out var messageProperty)
            ? messageProperty.GetString()
            : null;
    }

    public static string BuildDetailLine(StructuredAv1anEvent parsedEvent)
    {
        return parsedEvent.Type switch
        {
            "run_started" => BuildRunStartedLine(parsedEvent.Payload),
            "input_probed" => BuildInputProbedLine(parsedEvent.Payload),
            "chunk_plan" => BuildChunkPlanLine(parsedEvent.Payload),
            "encode_progress" => BuildEncodeProgressLine(parsedEvent.Payload),
            "encoder_log" => BuildEncoderLogDetailLine(parsedEvent.Payload),
            "run_completed" => "run completed",
            "run_failed" => string.IsNullOrWhiteSpace(TryGetFailureMessage(parsedEvent))
                ? "run failed"
                : $"run failed: {TryGetFailureMessage(parsedEvent)}",
            _ => parsedEvent.Type
        };
    }

    public static IReadOnlyList<string> BuildEncoderLogLines(StructuredAv1anEvent parsedEvent)
    {
        if (parsedEvent.Type != "encoder_log")
        {
            return Array.Empty<string>();
        }

        var encoder = TryGetString(parsedEvent.Payload, "encoder");
        var chunkIndex = TryGetInt32(parsedEvent.Payload, "chunk_index", out var parsedChunkIndex)
            ? parsedChunkIndex.ToString(CultureInfo.InvariantCulture)
            : "?";
        var frames = TryGetInt32(parsedEvent.Payload, "frames", out var parsedFrames)
            ? parsedFrames.ToString(CultureInfo.InvariantCulture)
            : "?";
        var stderr = TryGetString(parsedEvent.Payload, "stderr");
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>
        {
            $"--- ENCODER LOG chunk {chunkIndex} ({encoder}, {frames} frames) ---"
        };
        lines.AddRange(stderr
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static line => line.TrimEnd())
            .Where(static line => !string.IsNullOrWhiteSpace(line)));
        return lines;
    }

    private static string BuildRunStartedLine(JsonElement payload)
    {
        var encoder = TryGetString(payload, "encoder");
        var metric = TryGetString(payload, "target_metric");
        var quality = TryGetQualityRange(payload);
        return string.IsNullOrWhiteSpace(quality)
            ? $"run started: {encoder} / {metric}".TrimEnd(' ', '/')
            : $"run started: {encoder} / {metric} / target {quality}";
    }

    private static string BuildInputProbedLine(JsonElement payload)
    {
        var inputKind = TryGetString(payload, "input_kind");
        var width = TryGetInt32(payload, "width", out var parsedWidth) ? parsedWidth : 0;
        var height = TryGetInt32(payload, "height", out var parsedHeight) ? parsedHeight : 0;
        var frames = TryGetInt32(payload, "total_frames", out var parsedFrames) ? parsedFrames : 0;
        var fps = TryGetString(payload, "fps");
        return $"input probed: {inputKind} {width}x{height}, {frames} frames @ {fps} fps";
    }

    private static string BuildChunkPlanLine(JsonElement payload)
    {
        return TryGetInt32(payload, "chunk_count", out var chunkCount)
            ? $"chunk plan: {chunkCount} chunks"
            : "chunk plan";
    }

    private static string BuildEncodeProgressLine(JsonElement payload)
    {
        var done = TryGetInt32(payload, "fraction_done", out var parsedDone) ? parsedDone : 0;
        var total = TryGetInt32(payload, "fraction_total", out var parsedTotal) ? parsedTotal : 0;
        var chunkSuffix = TryGetInt32(payload, "chunk_index", out var chunkIndex)
            ? $" (chunk {chunkIndex})"
            : string.Empty;
        return total > 0
            ? $"encode progress: {done}/{total} chunks{chunkSuffix}"
            : $"encode progress{chunkSuffix}";
    }

    private static string BuildEncoderLogDetailLine(JsonElement payload)
    {
        var encoder = TryGetString(payload, "encoder");
        var chunkSuffix = TryGetInt32(payload, "chunk_index", out var chunkIndex)
            ? $"chunk {chunkIndex}"
            : "chunk";
        return string.IsNullOrWhiteSpace(encoder)
            ? $"encoder log captured: {chunkSuffix}"
            : $"encoder log captured: {chunkSuffix} ({encoder})";
    }

    private static bool TryGetInt32(JsonElement payload, string propertyName, out int value)
    {
        value = 0;
        if (!payload.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), out value),
            _ => false
        };
    }

    private static string TryGetString(JsonElement payload, string propertyName)
    {
        return payload.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string TryGetQualityRange(JsonElement payload)
    {
        if (!payload.TryGetProperty("target_quality", out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var values = property
            .EnumerateArray()
            .Where(static element => element.ValueKind == JsonValueKind.Number)
            .Select(static element => element.GetDouble().ToString("0.###", CultureInfo.InvariantCulture))
            .ToArray();

        return values.Length == 2
            ? $"{values[0]}-{values[1]}"
            : string.Empty;
    }
}
