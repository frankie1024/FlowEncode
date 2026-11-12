namespace FlowEncode.Domain;

public sealed record AudioProcessingTelemetry(
    double? SpeedMultiplier,
    double? BitrateKbps,
    TimeSpan? Remaining,
    long? EstimatedOutputBytes,
    double? DeewDb = null,
    DeewDbSource? DeewDbSource = null);

public enum DeewDbSource { Unknown, Measured, SpecifiedDialnorm }
