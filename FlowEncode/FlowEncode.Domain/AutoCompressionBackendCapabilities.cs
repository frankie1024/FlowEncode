namespace FlowEncode.Domain;

public sealed record AutoCompressionMetricCapability(
    AutoCompressionMetric Metric,
    MetricAvailability Availability,
    string? Reason = null);

public sealed record AutoCompressionProtocolRange(
    int Min,
    int Max);

public sealed record AutoCompressionAppCompatibility(
    string? MinBackendConsumer,
    string? MaxBackendConsumer);

public sealed record AutoCompressionBackendCapabilities(
    int Protocol,
    string BackendVersion,
    IReadOnlyList<AutoCompressionMetricCapability> Metrics,
    IReadOnlyList<EncoderKind> Encoders,
    IReadOnlyList<string> InterpolationMethods,
    IReadOnlyList<string> ProbingStatistics,
    AutoCompressionProtocolRange? ProtocolRange = null,
    AutoCompressionAppCompatibility? AppCompatibility = null);
