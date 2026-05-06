using System.Text.Json.Serialization;

namespace FlowEncode.Domain;

public sealed record AppSettings(
    bool PreferSystemEncoders,
    bool AutoCheckUpdatesOnStartup,
    AppThemePreference Theme,
    AppLanguage Language,
    bool HasSeenSetupGuide = false,
    string WorkspaceRootPath = "",
    IReadOnlyDictionary<string, string>? ManualToolPaths = null,
    bool HasRunInitialVsPluginDependencyUpdate = false,
    int MaxConcurrentEncodingJobs = 1,
    QueueCompletionAction QueueCompletionAction = QueueCompletionAction.None,
    string PreviewScalingAlgorithm = "nearest",
    string PreviewSnapshotDialogDirectory = "",
    string PreviewChapterDialogDirectory = "")
{
    public static AppSettings Default { get; } = new(
        PreferSystemEncoders: true,
        AutoCheckUpdatesOnStartup: true,
        Theme: AppThemePreference.Default,
        Language: AppLanguage.Chinese,
        HasSeenSetupGuide: false,
        WorkspaceRootPath: string.Empty,
        ManualToolPaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        HasRunInitialVsPluginDependencyUpdate: false,
        MaxConcurrentEncodingJobs: 1,
        QueueCompletionAction: QueueCompletionAction.None,
        PreviewScalingAlgorithm: "nearest",
        PreviewSnapshotDialogDirectory: string.Empty,
        PreviewChapterDialogDirectory: string.Empty);

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> EffectiveManualToolPaths =>
        ManualToolPaths ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
