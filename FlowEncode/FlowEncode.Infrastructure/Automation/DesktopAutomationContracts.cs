using FlowEncode.Domain;

namespace FlowEncode.Infrastructure.Automation;

/// <summary>
/// Methods the desktop app answers. Everything that runs work or changes app state goes through
/// the desktop so AI-initiated tasks share its queue, concurrency limit and completion action.
/// </summary>
public static class DesktopAutomationMethods
{
    public const string AppStatus = "app.status";

    public const string QueueList = "queue.list";
    public const string QueueGet = "queue.get";
    public const string QueueEnqueue = "queue.enqueue";
    public const string QueueStart = "queue.start";
    public const string QueueCancel = "queue.cancel";
    public const string QueueRemove = "queue.remove";
    public const string QueueRestart = "queue.restart";
    public const string QueueMove = "queue.move";

    public const string ModuleStatus = "module.status";
    public const string ModuleCancel = "module.cancel";
    public const string AudioStart = "audio.start";
    public const string BluRayStart = "bluray.start";
    public const string AutoCompressionStart = "autocompression.start";

    public const string SettingsGet = "settings.get";
    public const string SettingsUpdate = "settings.update";

    public const string TemplatesReload = "templates.reload";

    public const string DependenciesList = "dependencies.list";
    public const string DependencyOperation = "dependencies.operation";
    public const string EnvironmentRefresh = "environment.refresh";

    public const string AppUpdateCheck = "app.update.check";
    public const string AppUpdateInstall = "app.update.install";

    public const string OperationGet = "operation.get";
    public const string OperationList = "operation.list";
}

public static class DesktopAutomationModules
{
    public const string Audio = "audio";
    public const string BluRay = "bluray";
    public const string AutoCompression = "autocompression";

    public static readonly IReadOnlyList<string> All = [Audio, BluRay, AutoCompression];
}

public static class DesktopDependencyOperations
{
    public const string Install = "install";
    public const string Uninstall = "uninstall";
    public const string Import = "import";
    public const string Pin = "pin";
    public const string ClearPin = "clear_pin";

    public static readonly IReadOnlyList<string> All = [Install, Uninstall, Import, Pin, ClearPin];
}

public sealed record AutomationAppStatus(
    string Version,
    int ProcessId,
    bool IsReady,
    string StatusText,
    int MaxConcurrentEncodingJobs,
    QueueCompletionAction QueueCompletionAction,
    AutomationQueueSummary Queue,
    IReadOnlyList<AutomationModuleStatus> Modules,
    bool IsChangingWorkspaceRoot,
    string WorkspaceRootPath);

public sealed record AutomationQueueSummary(
    int Total,
    int Queued,
    int Running,
    int Completed,
    int Failed,
    int Cancelled);

public sealed record AutomationJobSnapshot(
    Guid JobId,
    int Position,
    EncodingJobState State,
    double? ProgressPercent,
    string Summary,
    string DetailLine,
    bool IsPreparingSource,
    long? CurrentFrame,
    long? TotalFrames,
    double? FramesPerSecond,
    double? BitrateKbps,
    double? EtaSeconds,
    long? EstimatedFileSizeBytes,
    string Title,
    string Encoder,
    string SourcePath,
    string OutputPath,
    bool UsesAv1anParallel,
    string? CommandLine,
    string? LogFilePath,
    bool CanStart,
    bool CanCancel,
    bool CanRemove,
    bool CanRestart,
    IReadOnlyList<string> RecentLogLines);

public sealed record AutomationModuleStatus(
    string Module,
    bool IsRunning,
    bool IsCancelling,
    Guid? ActiveJobId,
    EncodingJobState? State,
    double ProgressPercent,
    bool IsProgressIndeterminate,
    string StatusText,
    string CommandLine,
    IReadOnlyList<string> RecentLogLines);

public sealed record AutomationOperationSnapshot(
    Guid OperationId,
    string Kind,
    string Description,
    AutomationOperationState State,
    string? Result,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

public enum AutomationOperationState
{
    Running,
    Succeeded,
    Failed
}

public sealed record AutomationDependencyStatus(
    SetupDependencyKind Kind,
    string DisplayName,
    string State,
    string InstalledVersion,
    string LatestVersion,
    bool UpdateAvailable,
    string ExecutablePath,
    bool IsManualPinned,
    bool CanInstall,
    bool CanUninstall,
    bool RequiresManualImport,
    bool IsBusy,
    string Detail);

public sealed record AutomationSettings(
    bool PreferSystemEncoders,
    bool AutoCheckUpdatesOnStartup,
    AppThemePreference Theme,
    AppLanguage Language,
    string WorkspaceRootPath,
    int MaxConcurrentEncodingJobs,
    QueueCompletionAction QueueCompletionAction,
    IReadOnlyDictionary<string, string> ManualToolPaths)
{
    public static AutomationSettings From(AppSettings settings) => new(
        settings.PreferSystemEncoders,
        settings.AutoCheckUpdatesOnStartup,
        settings.Theme,
        settings.Language,
        settings.WorkspaceRootPath,
        settings.MaxConcurrentEncodingJobs,
        settings.QueueCompletionAction,
        settings.EffectiveManualToolPaths);
}

/// <summary>
/// Settings an automation client may change. Null fields are left as they are.
/// </summary>
public sealed record AutomationSettingsPatch(
    bool? PreferSystemEncoders = null,
    bool? AutoCheckUpdatesOnStartup = null,
    AppThemePreference? Theme = null,
    AppLanguage? Language = null,
    string? WorkspaceRootPath = null,
    int? MaxConcurrentEncodingJobs = null,
    QueueCompletionAction? QueueCompletionAction = null)
{
    public bool IsEmpty => PreferSystemEncoders is null
        && AutoCheckUpdatesOnStartup is null
        && Theme is null
        && Language is null
        && WorkspaceRootPath is null
        && MaxConcurrentEncodingJobs is null
        && QueueCompletionAction is null;

    /// <summary>
    /// Applies the patch to a settings snapshot (used when the desktop app is not running).
    /// </summary>
    public AppSettings ApplyTo(AppSettings settings) => RequestValidation.NormalizeAppSettings(settings with
    {
        PreferSystemEncoders = PreferSystemEncoders ?? settings.PreferSystemEncoders,
        AutoCheckUpdatesOnStartup = AutoCheckUpdatesOnStartup ?? settings.AutoCheckUpdatesOnStartup,
        Theme = Theme ?? settings.Theme,
        Language = Language ?? settings.Language,
        WorkspaceRootPath = WorkspaceRootPath ?? settings.WorkspaceRootPath,
        MaxConcurrentEncodingJobs = MaxConcurrentEncodingJobs ?? settings.MaxConcurrentEncodingJobs,
        QueueCompletionAction = QueueCompletionAction ?? settings.QueueCompletionAction
    });
}

public sealed record EnqueueEncodingJobParams(EncodingJobRequest Request, bool StartImmediately);

public sealed record JobIdParams(Guid JobId);

public sealed record GetJobParams(Guid JobId, int LogLines);

public sealed record MoveJobParams(Guid JobId, int Position);

public sealed record ModuleParams(string Module, int LogLines = 20);

public sealed record StartAudioParams(AudioProcessingRequest Request);

public sealed record StartBluRayParams(BluRayDemuxRequest Request);

public sealed record StartAutoCompressionParams(AutoCompressionRequest Request);

public sealed record DependencyOperationParams(SetupDependencyKind Kind, string Operation, string? SourcePath = null);

public sealed record OperationIdParams(Guid OperationId);

public sealed record CheckUpdatesParams(bool CheckForUpdates);

public sealed record AppUpdateCheckResultView(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? ReleaseUrl,
    string? Error);
