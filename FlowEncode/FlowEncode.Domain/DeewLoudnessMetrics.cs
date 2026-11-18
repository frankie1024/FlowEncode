namespace FlowEncode.Domain;

// Optional source measurement fields from deew's JSONL progress events. Units are not interchangeable.
public sealed record DeewLoudnessMetrics(
    string? Gating = null,
    double? SpeechPercent = null,
    double? ProgramLoudnessLkfs = null,
    double? LoudnessRangeLu = null,
    double? TruePeakDbtp = null);
