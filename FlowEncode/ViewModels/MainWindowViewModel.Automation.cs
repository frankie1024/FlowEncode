using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FlowEncode.Domain;
using FlowEncode.Infrastructure.Automation;

namespace FlowEncode.ViewModels;

/// <summary>
/// Entry points for the local automation channel (MCP server). Everything here runs on the UI
/// thread and goes through the same queue, module state and settings code as the window, so
/// AI-initiated work is indistinguishable from work the user started.
/// </summary>
public partial class MainWindowViewModel
{
    private const int AutomationMaxLogLines = 200;

    internal AutomationAppStatus GetAutomationAppStatus(string version, bool isReady)
    {
        return new AutomationAppStatus(
            version,
            Environment.ProcessId,
            isReady,
            StatusText,
            GetMaxConcurrentEncodingJobCount(),
            QueueCompletionAction,
            new AutomationQueueSummary(
                Jobs.Count,
                Jobs.Count(static job => job.State == EncodingJobState.Queued),
                Jobs.Count(static job => job.State == EncodingJobState.Running),
                Jobs.Count(static job => job.State == EncodingJobState.Completed),
                Jobs.Count(static job => job.State == EncodingJobState.Failed),
                Jobs.Count(static job => job.State == EncodingJobState.Cancelled)),
            DesktopAutomationModules.All.Select(module => GetAutomationModuleStatus(module, 0)).ToList(),
            _isChangingWorkspaceRoot,
            _appPaths.RootPath);
    }

    internal IReadOnlyList<AutomationJobSnapshot> GetAutomationJobs()
    {
        return Jobs.Select((job, index) => CreateAutomationJobSnapshot(job, index, 0)).ToList();
    }

    internal AutomationJobSnapshot GetAutomationJob(Guid jobId, int logLines)
    {
        var job = FindAutomationJob(jobId);
        return CreateAutomationJobSnapshot(job, Jobs.IndexOf(job), logLines);
    }

    internal AutomationJobSnapshot EnqueueAutomationJob(EncodingJobRequest request, bool startImmediately)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_isShuttingDown)
        {
            throw new DesktopAutomationException("FlowEncode is shutting down and does not accept new jobs.");
        }

        if (_isChangingWorkspaceRoot)
        {
            throw new DesktopAutomationException(Texts.WorkspaceDirectoryChangeInProgressMessage);
        }

        AutomationQueuePolicy.ValidateEnqueue(
            request,
            Jobs.Where(IsPendingQueueWork).Select(static job => job.Request),
            static path => File.Exists(path) || Directory.Exists(path));
        RunAutomationValidation(() => EnsureRequestConstraintsSatisfied(request));

        var hasRunningJob = GetRunningEncodingJobCount() >= GetMaxConcurrentEncodingJobCount();
        var job = new EncodingJobItemViewModel(
            request,
            Texts.Pick("正在生成实际执行命令...", "Resolving the actual command..."),
            CurrentLanguagePreference);

        Jobs.Add(job);
        SelectedJob ??= job;
        RefreshJobPositionFlags();
        RaiseJobSummaryPropertyChanges();
        StatusText = Texts.JobQueuedStatus(Path.GetFileName(request.SourcePath), startImmediately, hasRunningJob);
        _ = ResolveJobDisplayCommandAsync(job, request);

        if (startImmediately)
        {
            _ = ProcessQueueAfterUiRefreshAsync();
        }

        return CreateAutomationJobSnapshot(job, Jobs.IndexOf(job), 0);
    }

    internal async Task<AutomationJobSnapshot> CancelAutomationJobAsync(Guid jobId)
    {
        var job = FindAutomationJob(jobId);
        if (!job.CanCancel)
        {
            throw new DesktopAutomationException($"Job {jobId} is {job.State} and cannot be cancelled.");
        }

        await CancelJobAsync(job);
        return CreateAutomationJobSnapshot(job, Jobs.IndexOf(job), 5);
    }

    internal AutomationJobSnapshot StartAutomationJobNow(Guid jobId)
    {
        var job = FindAutomationJob(jobId);
        if (!job.CanStart)
        {
            throw new DesktopAutomationException($"Job {jobId} is {job.State}; only queued jobs can be started.");
        }

        if (GetRunningEncodingJobCount() < GetMaxConcurrentEncodingJobCount())
        {
            ThrowIfAutomationError(StartJobNow(job));
        }
        else
        {
            // Every slot is busy: make it the next job to run, like the window's batch start does.
            MoveQueuedJobsToFront([job]);
            RefreshJobPositionFlags();
            RaiseJobStatePropertyChanges();
            StatusText = Texts.BatchJobsPrioritizedStatus(1, GetMaxConcurrentEncodingJobCount());
        }

        return CreateAutomationJobSnapshot(job, Jobs.IndexOf(job), 0);
    }

    internal async Task<AutomationJobSnapshot> RestartAutomationJobAsync(Guid jobId)
    {
        var job = FindAutomationJob(jobId);
        var countBefore = Jobs.Count;
        ThrowIfAutomationError(await RestartJobAsync(job));
        var restarted = Jobs.Count > countBefore ? Jobs[^1] : job;
        return CreateAutomationJobSnapshot(restarted, Jobs.IndexOf(restarted), 0);
    }

    internal IReadOnlyList<AutomationJobSnapshot> RemoveAutomationJob(Guid jobId)
    {
        ThrowIfAutomationError(RemoveJob(FindAutomationJob(jobId)));
        RefreshJobPositionFlags();
        return GetAutomationJobs();
    }

    internal IReadOnlyList<AutomationJobSnapshot> MoveAutomationJob(Guid jobId, int position)
    {
        var job = FindAutomationJob(jobId);
        if (job.State != EncodingJobState.Queued)
        {
            throw new DesktopAutomationException($"Job {jobId} is {job.State}; only queued jobs can be reordered.");
        }

        var currentIndex = Jobs.IndexOf(job);
        var lastIndex = Jobs.Count - 1;
        var targetIndex = position < 0 ? lastIndex : Math.Min(position, lastIndex);
        targetIndex = Math.Max(targetIndex, Math.Min(GetQueuedMoveFloorIndex(), lastIndex));
        if (targetIndex != currentIndex)
        {
            Jobs.Move(currentIndex, targetIndex);
        }

        RefreshJobPositionFlags();
        RaiseJobSummaryPropertyChanges();
        return GetAutomationJobs();
    }

    internal AutomationModuleStatus GetAutomationModuleStatus(string module, int logLines)
    {
        return module switch
        {
            DesktopAutomationModules.Audio => new AutomationModuleStatus(
                module,
                _isAudioProcessingRunning,
                _isAudioProcessingRunning && _isAudioProcessingCancellationRequested,
                _activeAudioProcessingJobId,
                _audioProcessingDisplayState,
                AudioProcessingProgressPercent,
                AudioProcessingProgressIsIndeterminate,
                AudioProcessingStatusText,
                AudioProcessingCommandLine,
                TailAutomationLogLines(AudioProcessingLog, logLines)),
            DesktopAutomationModules.BluRay => new AutomationModuleStatus(
                module,
                _isBluRayDemuxRunning,
                _isBluRayDemuxRunning && _isBluRayDemuxCancellationRequested,
                _activeBluRayDemuxJobId,
                _bluRayDemuxDisplayState,
                BluRayDemuxProgressPercent,
                BluRayDemuxProgressIsIndeterminate,
                BluRayDemuxStatusText,
                BluRayDemuxCommandLine,
                TailAutomationLogLines(BluRayDemuxLog, logLines)),
            DesktopAutomationModules.AutoCompression => new AutomationModuleStatus(
                module,
                _isAutoCompressionRunning,
                _isAutoCompressionRunning && _isAutoCompressionCancellationRequested,
                _activeAutoCompressionJobId,
                _autoCompressionDisplayState,
                AutoCompressionProgressPercent,
                AutoCompressionProgressIsIndeterminate,
                AutoCompressionStatusText,
                AutoCompressionCommandLine,
                TailAutomationLogLines(AutoCompressionLog, logLines)),
            _ => throw new DesktopAutomationException($"Unknown module '{module}'.")
        };
    }

    internal AutomationModuleStatus CancelAutomationModule(string module)
    {
        switch (module)
        {
            case DesktopAutomationModules.Audio:
                CancelAudioProcessing();
                break;
            case DesktopAutomationModules.BluRay:
                CancelBluRayDemux();
                break;
            case DesktopAutomationModules.AutoCompression:
                CancelAutoCompression();
                break;
            default:
                throw new DesktopAutomationException($"Unknown module '{module}'.");
        }

        return GetAutomationModuleStatus(module, 5);
    }

    internal AutomationModuleStatus StartAutomationAudio(AudioProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_isAudioProcessingRunning)
        {
            throw new DesktopAutomationException(Texts.AudioProcessingAlreadyRunningError);
        }

        RunAutomationValidation(() => RequestValidation.ValidateAudioProcessingRequest(request));
        ObserveAutomationModuleStart(StartAudioProcessingAsync(request));
        return GetAutomationModuleStatus(DesktopAutomationModules.Audio, 5);
    }

    internal AutomationModuleStatus StartAutomationBluRay(BluRayDemuxRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_isBluRayDemuxRunning)
        {
            throw new DesktopAutomationException(Texts.BluRayDemuxAlreadyRunningError);
        }

        if (request.Selections.Count == 0)
        {
            throw new DesktopAutomationException(Texts.BluRayTrackSelectionMissingError);
        }

        ObserveAutomationModuleStart(StartBluRayDemuxAsync(request));
        return GetAutomationModuleStatus(DesktopAutomationModules.BluRay, 5);
    }

    internal AutomationModuleStatus StartAutomationAutoCompression(AutoCompressionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_isAutoCompressionRunning)
        {
            throw new DesktopAutomationException(Texts.AutoCompressionAlreadyRunningError);
        }

        RunAutomationValidation(() => RequestValidation.ValidateAutoCompressionRequest(request));
        ObserveAutomationModuleStart(StartAutoCompressionAsync(request));
        return GetAutomationModuleStatus(DesktopAutomationModules.AutoCompression, 5);
    }

    internal AutomationSettings GetAutomationSettings()
    {
        return new AutomationSettings(
            PreferSystemEncoders,
            AutoCheckUpdatesOnStartup,
            CurrentThemePreference,
            CurrentLanguagePreference,
            _appPaths.RootPath,
            GetMaxConcurrentEncodingJobCount(),
            QueueCompletionAction,
            new Dictionary<string, string>(_manualToolPaths, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies the in-memory part of a settings change. The window then persists it (and applies
    /// theme / workspace side effects) exactly as when the user edits the settings page.
    /// </summary>
    internal async Task<bool> ApplyAutomationSettingsPatchAsync(AutomationSettingsPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (patch.PreferSystemEncoders is { } preferSystemEncoders)
        {
            PreferSystemEncoders = preferSystemEncoders;
        }

        if (patch.AutoCheckUpdatesOnStartup is { } autoCheckUpdates)
        {
            AutoCheckUpdatesOnStartup = autoCheckUpdates;
        }

        if (patch.Theme is { } theme)
        {
            SelectedTheme = ThemeOptions.FirstOrDefault(option => option.Value == theme) ?? SelectedTheme;
        }

        if (patch.Language is { } language)
        {
            SelectedLanguage = LanguageOptions.FirstOrDefault(option => option.Value == language) ?? SelectedLanguage;
        }

        if (patch.MaxConcurrentEncodingJobs is { } maxConcurrentJobs)
        {
            MaxConcurrentEncodingJobs = maxConcurrentJobs;
        }

        if (patch.QueueCompletionAction is { } completionAction)
        {
            QueueCompletionAction = completionAction;
        }

        var preparedWorkspaceChange = false;
        if (!string.IsNullOrWhiteSpace(patch.WorkspaceRootPath))
        {
            ThrowIfAutomationError(await PrepareWorkspaceRootChangeAsync(patch.WorkspaceRootPath));
            preparedWorkspaceChange = _isChangingWorkspaceRoot;
        }

        return preparedWorkspaceChange;
    }

    /// <summary>
    /// Re-reads the template library after an automation client changed template files.
    /// </summary>
    internal async Task ReloadTemplatesFromDiskAsync()
    {
        var templates = await _profileLibraryService.GetUserTemplatesAsync();
        TemplatesModule.Library.ApplyLoadedTemplates(templates);
    }

    private EncodingJobItemViewModel FindAutomationJob(Guid jobId)
    {
        return Jobs.FirstOrDefault(job => job.JobId == jobId)
            ?? throw new DesktopAutomationException($"No job with id {jobId} is in the queue.");
    }

    private void ObserveAutomationModuleStart(Task<string?> start)
    {
        // The module methods return synchronously with an error when they refuse to start;
        // otherwise they keep running until the task ends and report through module state.
        if (start.IsCompleted && start.Result is { Length: > 0 } error)
        {
            throw new DesktopAutomationException(error);
        }

        _ = start.ContinueWith(
            completed => WriteDiagnostic($"Automation-started module task failed. {completed.Exception?.GetBaseException().Message}"),
            System.Threading.CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void ThrowIfAutomationError(string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new DesktopAutomationException(error);
        }
    }

    private static void RunAutomationValidation(Action validate)
    {
        try
        {
            validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new DesktopAutomationException(ex.Message);
        }
    }

    private static AutomationJobSnapshot CreateAutomationJobSnapshot(EncodingJobItemViewModel job, int index, int logLines)
    {
        return new AutomationJobSnapshot(
            job.JobId,
            index,
            job.State,
            job.ProgressFraction is { } fraction ? Math.Round(fraction * 100, 2) : null,
            job.Summary,
            job.DetailLine,
            job.IsSourcePreparation,
            job.CurrentFrame,
            job.TotalFrames,
            job.FramesPerSecond,
            job.BitrateKbps,
            job.Eta is { } eta ? Math.Round(eta.TotalSeconds) : null,
            job.EstimatedFileSizeBytes,
            job.Title,
            job.EncoderLabel,
            job.SourcePath,
            job.OutputPath,
            job.Request.UseAv1anParallelVideoEncoding,
            job.IsDisplayCommandResolved ? job.DisplayCommand : null,
            string.IsNullOrWhiteSpace(job.LogFilePath) ? null : job.LogFilePath,
            job.CanStart,
            job.CanCancel,
            job.CanRemove,
            job.CanRestart,
            TailAutomationLogLines(job.Log, logLines));
    }

    private static IReadOnlyList<string> TailAutomationLogLines(string? log, int count)
    {
        if (count <= 0 || string.IsNullOrEmpty(log))
        {
            return [];
        }

        var lines = log
            .Split('\n')
            .Select(static line => line.TrimEnd('\r'))
            .Where(static line => line.Length > 0)
            .ToList();
        var take = Math.Min(Math.Min(count, AutomationMaxLogLines), lines.Count);
        return lines.GetRange(lines.Count - take, take);
    }
}
