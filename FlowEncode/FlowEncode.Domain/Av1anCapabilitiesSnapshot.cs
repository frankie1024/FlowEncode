namespace FlowEncode.Domain;

public sealed record Av1anCapabilitiesSnapshot(
    int Protocol,
    string BackendVersion,
    IReadOnlyList<AutoCompressionMetric> SupportedMetrics,
    IReadOnlyList<EncoderKind> SupportedEncoders,
    IReadOnlyList<string> InterpolationMethods,
    IReadOnlyList<string> ProbingStatistics);
