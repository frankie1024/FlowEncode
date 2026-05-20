namespace FlowEncode.Domain;

public sealed record ToolProbeResult(
    RegisteredToolKind Kind,
    ReadinessState State,
    ToolDetectionSource Source,
    string SourceLabel,
    string ExecutablePath,
    string DetectedVersion,
    string FailureReason,
    string ReleaseUrl,
    ExternalToolKind? ManagedExternalToolKind = null,
    bool IsProtocolCompatible = false,
    string ProtocolVersion = "",
    string BackendCompatibilityDetail = "")
{
    public string DisplayName => Kind.ToDisplayName();

    public bool IsReady => State == ReadinessState.Ready;

    public bool SupportsManagedInstall => ManagedExternalToolKind.HasValue;
}
