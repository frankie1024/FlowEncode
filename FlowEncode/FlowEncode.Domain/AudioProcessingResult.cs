namespace FlowEncode.Domain;

public sealed record AudioProcessingResult(
    Guid JobId,
    EncodingJobState State,
    int ExitCode,
    string Summary,
    string Log,
    string DisplayCommand,
    IReadOnlyList<string>? OutputFiles = null,
    IReadOnlyList<string>? DiagnosticFiles = null,
    string? RetainedTempPath = null,
    double? LoudnessDb = null,
    double? DialnormDb = null);
