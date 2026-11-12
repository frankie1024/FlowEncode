namespace FlowEncode.Domain;

public sealed record SetupGuideCacheSnapshot(
    int SchemaVersion,
    DateTimeOffset SavedAt,
    DateTimeOffset? LocalCheckedAt,
    DateTimeOffset? RemoteCheckedAt,
    SetupGuideCacheStatusReport? StatusReport,
    ToolProbeResult[]? EnvironmentTools = null,
    string EnvironmentSignature = "",
    DateTimeOffset? EnvironmentCheckedAt = null)
{
    public const int CurrentSchemaVersion = 4;
}

public sealed record SetupGuideCacheStatusReport(
    DateTimeOffset CheckedAt,
    SetupGuideCacheDependencyStatus[] Dependencies);

public sealed record SetupGuideCacheDependencyStatus(
    SetupDependencyKind Kind,
    ReadinessState State,
    string InstalledVersion,
    string LatestVersion,
    bool UpdateAvailable,
    string ExecutablePath,
    bool IsInstallSupported,
    bool IsInstallEnabled,
    string Detail);
