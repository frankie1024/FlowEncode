namespace FlowEncode.Domain;

public sealed record AutoCompressionProgress(
    Guid JobId,
    EncodingJobState State,
    AutoCompressionExecutionStage Stage,
    double? ProgressFraction,
    string Summary,
    string DetailLine);
