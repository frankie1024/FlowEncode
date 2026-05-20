using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal sealed record VideoEncodingOutputLine(
    string NormalizedLine,
    bool IsTransient,
    bool ShouldShowInLog,
    bool ShouldSurfaceDuringSourcePreparation,
    EncodingProgressParseResult? ParseResult);

internal sealed record SourcePreparationOutputLine(
    string NormalizedLine,
    string DisplayLine,
    int? ProgressPercent,
    bool ShouldShowInLog);

internal static class VideoEncodingOutputBridge
{
    public static VideoEncodingOutputLine? ParseEncodingLine(
        EncoderKind kind,
        long? totalFrames,
        double? sourceFramesPerSecond,
        string line)
    {
        var normalizedLine = EncoderConsoleLineNormalizer.Normalize(line);
        if (string.IsNullOrWhiteSpace(normalizedLine))
        {
            return null;
        }

        var parseResult = EncodingProgressParser.ParseSnapshot(kind, totalFrames, sourceFramesPerSecond, normalizedLine);
        var isTransient = EncodingLogLineClassifier.IsTransientProgressLine(kind, normalizedLine);

        return new VideoEncodingOutputLine(
            normalizedLine,
            IsTransient: isTransient,
            ShouldShowInLog: !isTransient,
            ShouldSurfaceDuringSourcePreparation: ShouldSurfaceLineDuringSourcePreparation(normalizedLine),
            parseResult);
    }

    public static SourcePreparationOutputLine? ParseSourcePreparationLine(string line)
    {
        var normalizedLine = EncoderConsoleLineNormalizer.Normalize(line);
        if (string.IsNullOrWhiteSpace(normalizedLine))
        {
            return null;
        }

        return new SourcePreparationOutputLine(
            normalizedLine,
            $"[source] {normalizedLine}",
            EncodingProgressParser.ParseSourcePreparationProgressPercent(normalizedLine),
            ShouldShowInLog: ShouldSurfaceLineDuringSourcePreparation(normalizedLine));
    }

    public static bool ShouldSurfaceLineDuringSourcePreparation(string line)
    {
        return line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || line.Contains("traceback", StringComparison.OrdinalIgnoreCase);
    }
}
