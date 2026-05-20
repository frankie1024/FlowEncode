namespace FlowEncode.Domain;

public sealed record AutoCompressionResult(
    Guid JobId,
    EncodingJobState State,
    int ExitCode,
    string Summary,
    string Log,
    string DisplayCommand,
    AutoCompressionBackendCapabilities? BackendCapabilities = null);
