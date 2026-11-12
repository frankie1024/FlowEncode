namespace FlowEncode.Domain;

public sealed record AudioProcessingProgress(
    Guid JobId,
    EncodingJobState State,
    double? ProgressFraction,
    string Summary,
    string DetailLine,
    AudioProcessingTelemetry? Telemetry = null,
    string? PhaseLabel = null,
    string? LogSnapshot = null);
