namespace FlowEncode.Domain;

public sealed record AutoCompressionSearchProfile(
    string? InterpolationMethod,
    string? ProbingStatistic,
    string? ProbeResolution,
    int ProbingRate,
    bool ScoutEnabled);
