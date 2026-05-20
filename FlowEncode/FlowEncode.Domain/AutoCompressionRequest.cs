namespace FlowEncode.Domain;

public sealed record AutoCompressionRequest(
    Guid JobId,
    string SourcePath,
    string OutputPath,
    EncoderKind EncoderKind,
    AutoCompressionMetric Metric,
    double TargetQuality,
    int Probes,
    string VideoParameters,
    int? Workers,
    AutoCompressionSearchProfile? SearchProfile = null);
