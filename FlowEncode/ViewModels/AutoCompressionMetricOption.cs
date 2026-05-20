using FlowEncode.Domain;

namespace FlowEncode.ViewModels;

public sealed record AutoCompressionMetricOption(
    AutoCompressionMetric Value,
    string Label);
