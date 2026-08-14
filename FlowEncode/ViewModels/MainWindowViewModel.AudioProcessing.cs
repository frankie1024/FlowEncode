using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Controls.Shared;
using FlowEncode.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FlowEncode.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly HashSet<string> UnsupportedDirectAudioContainerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv",
        ".mp4",
        ".m2ts",
        ".ts",
        ".avi",
        ".flv",
        ".mov"
    };

    private string _audioProcessingSourcePath = string.Empty;
    private string _audioProcessingOutputPath = string.Empty;
    private AudioWorkflowOption? _selectedAudioWorkflow;
    private AudioEac3ToOutputFormatOption? _selectedAudioEac3ToOutputFormat;
    private AudioOpusBitrateOption? _selectedAudioOpusBitrate;
    private bool _audioOpusUseMappingFamily1;
    private string _audioProcessingAdditionalArguments = string.Empty;
    private string _audioProcessingStatusText = string.Empty;
    private string _audioProcessingCommandLine = string.Empty;
    private string _audioProcessingLog = string.Empty;
    private string _audioProcessingPhaseLabel = string.Empty;
    private AudioProcessingTelemetry? _audioProcessingTelemetry;
    private double _audioProcessingProgressPercent;
    private bool _audioProcessingProgressIsIndeterminate;
    private bool _isAudioProcessingRunning;
    private string? _lastAudioProcessingOutputPath;
    private bool _isUpdatingAudioProcessingOutputPath;
    private CancellationTokenSource? _audioProcessingCancellationTokenSource;
    private Guid? _activeAudioProcessingJobId;
    private EncodingJobState? _audioProcessingDisplayState;
    private AudioProcessingMode? _activeAudioProcessingMode;
    private AudioSourceInfo? _audioSourceInfo;
    private bool _isAudioSourceInfoLoading;
    private string? _audioSourceInfoError;
    private CancellationTokenSource? _audioSourceProbeCancellationTokenSource;
    private CancellationTokenSource? _audioProcessingInputRefreshCancellationTokenSource;
    private int _audioSourceProbeVersion;
    private int _audioProcessingInputRefreshVersion;
    private bool _isApplyingDeferredAudioProcessingInputRefresh;
    private bool _isAudioProcessingInputRefreshPending;
    private bool _isApplyingAudioProcessingLanguageState;
    private readonly List<string> _audioProcessingLogStageLines = [];
    private string _audioProcessingLiveLogLine = string.Empty;
    private string _audioProcessingLogPhaseMarker = string.Empty;
    private const int AudioProcessingStageLogLimit = 48;
    private static readonly TimeSpan AudioSourceProbeDebounceInterval = TimeSpan.FromMilliseconds(200);

    internal ObservableCollection<AudioWorkflowOption> AudioWorkflowOptions { get; } = [];

    internal ObservableCollection<AudioEac3ToOutputFormatOption> AudioEac3ToOutputFormatOptions { get; } = [];

    internal ObservableCollection<AudioOpusBitrateOption> AudioOpusBitrateOptions { get; } = [];

    internal string AudioProcessingSourcePath
    {
        get => _audioProcessingSourcePath;
        set
        {
            if (SetProperty(ref _audioProcessingSourcePath, value))
            {
                CancelAudioSourceProbeForPendingInputRefresh();
                ScheduleAudioProcessingInputRefresh(probeSource: true);
            }
        }
    }

    internal string AudioProcessingOutputPath
    {
        get => _audioProcessingOutputPath;
        set
        {
            if (SetProperty(ref _audioProcessingOutputPath, value))
            {
                if (!_isUpdatingAudioProcessingOutputPath)
                {
                    _lastAudioProcessingOutputPath = null;
                }

                if (_isApplyingDeferredAudioProcessingInputRefresh)
                {
                    return;
                }

                ScheduleAudioProcessingInputRefresh(probeSource: false);
            }
        }
    }

    internal AudioWorkflowOption? SelectedAudioWorkflow
    {
        get => _selectedAudioWorkflow;
        set
        {
            if (SetProperty(ref _selectedAudioWorkflow, value))
            {
                TryPopulateAudioProcessingOutputPathIfEmpty();
                if (_isApplyingAudioProcessingLanguageState)
                {
                    return;
                }

                RaiseAudioProcessingInputPropertyChanges();
                OnPropertyChanged(nameof(AudioEac3ToOptionsVisibility));
                OnPropertyChanged(nameof(AudioOpusOptionsVisibility));
                OnPropertyChanged(nameof(AudioProcessingProgressPrimaryText));
                RefreshAudioProcessingCommandPreview();
            }
        }
    }

    internal AudioEac3ToOutputFormatOption? SelectedAudioEac3ToOutputFormat
    {
        get => _selectedAudioEac3ToOutputFormat;
        set
        {
            if (SetProperty(ref _selectedAudioEac3ToOutputFormat, value))
            {
                TryPopulateAudioProcessingOutputPathIfEmpty();
                if (_isApplyingAudioProcessingLanguageState)
                {
                    return;
                }

                RaiseAudioProcessingInputPropertyChanges();
                RefreshAudioProcessingCommandPreview();
            }
        }
    }

    internal AudioOpusBitrateOption? SelectedAudioOpusBitrate
    {
        get => _selectedAudioOpusBitrate;
        set
        {
            if (SetProperty(ref _selectedAudioOpusBitrate, value))
            {
                if (_isApplyingAudioProcessingLanguageState)
                {
                    return;
                }

                RaiseAudioProcessingInputPropertyChanges();
                RefreshAudioProcessingCommandPreview();
            }
        }
    }

    internal bool AudioOpusUseMappingFamily1
    {
        get => _audioOpusUseMappingFamily1;
        set
        {
            if (SetProperty(ref _audioOpusUseMappingFamily1, value))
            {
                if (_isApplyingAudioProcessingLanguageState)
                {
                    return;
                }

                RaiseAudioProcessingInputPropertyChanges();
                RefreshAudioProcessingCommandPreview();
            }
        }
    }

    internal string AudioProcessingAdditionalArguments
    {
        get => _audioProcessingAdditionalArguments;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _audioProcessingAdditionalArguments, normalized))
            {
                if (_isApplyingAudioProcessingLanguageState)
                {
                    return;
                }

                RaiseAudioProcessingInputPropertyChanges();
                RefreshAudioProcessingCommandPreview();
            }
        }
    }

    internal string AudioProcessingStatusText
    {
        get => _audioProcessingStatusText;
        set
        {
            if (SetProperty(ref _audioProcessingStatusText, value))
            {
                OnPropertyChanged(nameof(CanClearAudioProcessingTask));
                OnPropertyChanged(nameof(AudioProcessingProgressPrimaryText));
                OnPropertyChanged(nameof(AudioProcessingProgressSecondaryText));
            }
        }
    }

    internal string AudioProcessingCommandLine
    {
        get => _audioProcessingCommandLine;
        set
        {
            if (SetProperty(ref _audioProcessingCommandLine, value))
            {
                OnPropertyChanged(nameof(CanClearAudioProcessingTask));
            }
        }
    }

    internal string AudioProcessingLog
    {
        get => _audioProcessingLog;
        set
        {
            if (SetProperty(ref _audioProcessingLog, value))
            {
                OnPropertyChanged(nameof(CanClearAudioProcessingTask));
            }
        }
    }

    internal double AudioProcessingProgressPercent
    {
        get => _audioProcessingProgressPercent;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _audioProcessingProgressPercent, normalized))
            {
                OnPropertyChanged(nameof(AudioProcessingProgressValue));
                OnPropertyChanged(nameof(AudioProcessingProgressPercentText));
                OnPropertyChanged(nameof(AudioProcessingProgressLabel));
            }
        }
    }

    internal bool AudioProcessingProgressIsIndeterminate
    {
        get => _audioProcessingProgressIsIndeterminate;
        set
        {
            if (SetProperty(ref _audioProcessingProgressIsIndeterminate, value))
            {
                OnPropertyChanged(nameof(AudioProcessingProgressPercentText));
                OnPropertyChanged(nameof(AudioProcessingProgressLabel));
                OnPropertyChanged(nameof(AudioProcessingProgressSecondaryText));
                OnPropertyChanged(nameof(AudioProcessingProgressHintVisibility));
            }
        }
    }

    internal bool IsAudioProcessingRunning => _isAudioProcessingRunning;

    internal bool CanStartAudioProcessing =>
        !_isChangingWorkspaceRoot
        && !_isAudioProcessingRunning
        && SelectedAudioWorkflow is not null
        && !string.IsNullOrWhiteSpace(AudioProcessingSourcePath)
        && !string.IsNullOrWhiteSpace(AudioProcessingOutputPath)
        && GetSelectedAudioCapabilityState() == ReadinessState.Ready
        && string.IsNullOrWhiteSpace(ValidateAudioProcessingConfiguration(requireSourceExists: false, out _));

    internal bool CanCancelAudioProcessing => _isAudioProcessingRunning;

    internal bool CanClearAudioProcessingTask =>
        !_isAudioProcessingRunning
        && (!string.IsNullOrWhiteSpace(AudioProcessingSourcePath)
            || !string.IsNullOrWhiteSpace(AudioProcessingOutputPath)
            || !string.IsNullOrWhiteSpace(AudioProcessingCommandLine)
            || !string.IsNullOrWhiteSpace(AudioProcessingLog)
            || !string.Equals(AudioProcessingStatusText, Texts.AudioProcessingIdleStatus, StringComparison.Ordinal));

    internal string AudioProcessingProgressLabel =>
        AudioProcessingProgressIsIndeterminate && _isAudioProcessingRunning
            ? Texts.AudioProcessingProgressActiveLabel
            : FormatAudioProcessingPercent(AudioProcessingProgressPercent);

    internal double AudioProcessingProgressValue => AudioProcessingProgressPercent / 100.0;

    internal string AudioProcessingProgressPercentText =>
        AudioProcessingProgressIsIndeterminate && _isAudioProcessingRunning && AudioProcessingProgressPercent <= 0
            ? "--"
            : FormatAudioProcessingPercent(AudioProcessingProgressPercent);

    internal string AudioProcessingProgressPrimaryText =>
        AudioProcessingProgressPercentText;

    internal string AudioProcessingProgressSecondaryText =>
        _audioProcessingTelemetry is not null
            ? Texts.AudioProcessingTelemetrySummary(_audioProcessingTelemetry)
            : !string.IsNullOrWhiteSpace(_audioProcessingPhaseLabel)
                ? _audioProcessingPhaseLabel
            : _isAudioProcessingRunning
                && AudioProcessingProgressIsIndeterminate
                && (_activeAudioProcessingMode ?? SelectedAudioWorkflow?.Value) == AudioProcessingMode.Ddp
                    && string.Equals(AudioProcessingStatusText, Texts.AudioProcessingDdpWarmupHint, StringComparison.Ordinal)
                        ? Texts.AudioProcessingDdpWarmupHint
                    : string.Empty;

    internal Visibility AudioProcessingProgressSecondaryVisibility =>
        string.IsNullOrWhiteSpace(AudioProcessingProgressSecondaryText)
            ? Visibility.Collapsed
            : Visibility.Visible;

    internal Visibility AudioProcessingProgressHintVisibility =>
        _isAudioProcessingRunning && AudioProcessingProgressIsIndeterminate
            ? Visibility.Visible
            : Visibility.Collapsed;

    internal string AudioProcessingProgressHint => Texts.AudioProcessingProgressIndeterminateHint;

    internal Brush AudioProcessingStatusPanelBorderBrush => ResolveTaskStatusPanelBorderBrush(_audioProcessingDisplayState);

    internal Brush AudioProcessingProgressTrackBrush => ResolveAudioProcessingProgressTrackBrush(_audioProcessingDisplayState);

    internal Brush AudioProcessingProgressBorderBrush => ResolveAudioProcessingProgressBorderBrush(_audioProcessingDisplayState);

    internal Brush AudioProcessingProgressFillBrush => ResolveAudioProcessingProgressFillBrush(_audioProcessingDisplayState);

    internal TaskPresentationState AudioProcessingPresentationState => _audioProcessingDisplayState switch
    {
        EncodingJobState.Running => TaskPresentationState.Running,
        EncodingJobState.Completed => TaskPresentationState.Completed,
        EncodingJobState.Failed => TaskPresentationState.Failed,
        EncodingJobState.Cancelled => TaskPresentationState.Cancelled,
        _ => TaskPresentationState.Idle
    };

    internal string AudioProcessingSuggestedOutputExtension => GetAudioProcessingSuggestedExtension();

    internal string AudioProcessingSuggestedOutputFileName
    {
        get
        {
            var outputPath = TryResolveAudioProcessingOutputPreviewPath();
            return string.IsNullOrWhiteSpace(outputPath)
                ? Texts.SuggestedOutputName
                : Path.GetFileNameWithoutExtension(outputPath);
        }
    }

    internal string AudioProcessingOutputPreviewText => _isAudioProcessingInputRefreshPending
        ? Texts.OutputPreviewUpdating
        : BuildOutputPreviewText(TryResolveAudioProcessingOutputPreviewPath());

    internal string AudioProcessingOutputHeader => Texts.OutputDirectoryHeader;

    internal string AudioProcessingOutputBrowseButtonText => Texts.ChooseFolderButton;

    internal Visibility AudioEac3ToOptionsVisibility =>
        SelectedAudioWorkflow?.Value == AudioProcessingMode.Eac3To
            ? Visibility.Visible
            : Visibility.Collapsed;

    internal Visibility AudioOpusOptionsVisibility =>
        SelectedAudioWorkflow?.Value == AudioProcessingMode.Opus
            ? Visibility.Visible
            : Visibility.Collapsed;

    internal string AudioSourceInfoText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AudioProcessingSourcePath))
            {
                return Texts.AudioSourceInfoPlaceholder;
            }

            if (_isAudioSourceInfoLoading)
            {
                return Texts.AudioSourceInspectingStatus;
            }

            if (_audioSourceInfo is not null)
            {
                return Texts.AudioSourceInfoSummary(
                    string.IsNullOrWhiteSpace(_audioSourceInfo.CodecName) ? "unknown" : _audioSourceInfo.CodecName,
                    Texts.AudioChannelProfileLabel(_audioSourceInfo.InferProfile() ?? AudioChannelProfile.Auto),
                    _audioSourceInfo.BitDepth,
                    _audioSourceInfo.SampleRate,
                    _audioSourceInfo.IsLossless());
            }

            return string.IsNullOrWhiteSpace(_audioSourceInfoError)
                ? Texts.AudioSourceInfoPlaceholder
                : Texts.AudioSourceProbeFailed(_audioSourceInfoError);
        }
    }

    internal string AudioWorkflowRecommendation => Texts.AudioSourceRecommendation(_audioSourceInfo);

    internal string AudioCapabilitySummary
    {
        get
        {
            var workflow = SelectedAudioWorkflow;
            if (workflow is null)
            {
                return Texts.AudioCapabilityPreparing;
            }

            var workflowLabel = Texts.AudioWorkflowLabel(workflow.Value);
            var capability = GetSelectedAudioCapabilityReadiness();
            if (capability is null)
            {
                return Texts.AudioCapabilityPreparing;
            }

            var missingRequirements = capability.Requirements
                .Where(static requirement => !requirement.IsSatisfied)
                .Select(BuildRequirementLabel)
                .ToList();

            if (capability.State == ReadinessState.Ready)
            {
                return Texts.AudioCapabilityReadySummary(workflowLabel);
            }

            var detail = string.Join(" / ", missingRequirements);
            return Texts.AudioCapabilityUnavailableSummary(workflowLabel, string.IsNullOrWhiteSpace(detail) ? capability.State.ToString() : detail);
        }
    }

    internal string? ValidateAudioProcessingForStart(out string? existingOutputPath)
    {
        return ValidateAudioProcessingConfiguration(requireSourceExists: true, out existingOutputPath);
    }

    internal async Task<string?> StartAudioProcessingAsync()
    {
        if (_isChangingWorkspaceRoot)
        {
            return Texts.WorkspaceDirectoryChangeInProgressMessage;
        }

        if (_isAudioProcessingRunning)
        {
            return Texts.AudioProcessingAlreadyRunningError;
        }

        AudioProcessingResult result;
        var workflowLabel = Texts.AudioWorkflowLabel(SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp);

        try
        {
            var request = CreateAudioProcessingRequest(requireSourceExists: true);
            _activeAudioProcessingMode = request.Mode;
            workflowLabel = Texts.AudioWorkflowLabel(request.Mode);

            AudioProcessingLog = string.Empty;
            ResetAudioProcessingLogState();
            SetAudioProcessingPhaseLabel(string.Empty);
            SetAudioProcessingTelemetry(null);
            AudioProcessingProgressPercent = 0;
            AudioProcessingProgressIsIndeterminate = true;
            SetAudioProcessingDisplayState(EncodingJobState.Running);
            AudioProcessingCommandLine = _audioProcessingRunner.BuildDisplayCommand(request);
            AudioProcessingStatusText = Texts.AudioProcessingStartingStatus(Path.GetFileName(request.SourcePath), workflowLabel);
            StatusText = AudioProcessingStatusText;

            SetAudioProcessingRunningState(true, request.JobId);
            _audioProcessingCancellationTokenSource = new CancellationTokenSource();

            var progress = new Progress<AudioProcessingProgress>(ApplyAudioProcessingProgress);
            result = await _audioProcessingRunner.RunAsync(
                request,
                progress,
                _audioProcessingCancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (_audioProcessingCancellationTokenSource?.IsCancellationRequested == true)
        {
            SetAudioProcessingRunningState(false, null);
            DisposeAudioProcessingCancellation();
            ClampAudioProcessingProgressForTerminalState(EncodingJobState.Cancelled);
            SetAudioProcessingDisplayState(EncodingJobState.Cancelled);
            SetAudioProcessingPhaseLabel(string.Empty);
            SetAudioProcessingTelemetry(null);
            AudioProcessingStatusText = Texts.AudioProcessingCancelledStatus(workflowLabel);
            StatusText = AudioProcessingStatusText;
            return null;
        }
        catch (Exception ex)
        {
            SetAudioProcessingRunningState(false, null);
            DisposeAudioProcessingCancellation();
            ClampAudioProcessingProgressForTerminalState(EncodingJobState.Failed);
            SetAudioProcessingDisplayState(EncodingJobState.Failed);
            SetAudioProcessingPhaseLabel(string.Empty);
            SetAudioProcessingTelemetry(null);
            ApplyAudioProcessingFailureLog(ex.Message, ex.Message);
            AudioProcessingStatusText = Texts.AudioProcessingFailedStatus(ex.Message);
            StatusText = AudioProcessingStatusText;
            return ex.Message;
        }

        DisposeAudioProcessingCancellation();
        SetAudioProcessingRunningState(false, null);

        if (string.IsNullOrWhiteSpace(AudioProcessingLog))
        {
            AudioProcessingLog = LastMeaningfulAudioProcessingLine(result.Log, result.Summary);
        }

        switch (result.State)
        {
            case EncodingJobState.Completed:
                SetAudioProcessingDisplayState(EncodingJobState.Completed);
                ClampAudioProcessingProgressForTerminalState(EncodingJobState.Completed);
                SetAudioProcessingPhaseLabel(string.Empty);
                ApplyFinalAudioProcessingLogLine(
                    UsesCompactAudioProcessingLog(_activeAudioProcessingMode)
                        ? LastMeaningfulAudioProcessingStageLine(result.Log, result.Summary)
                        : LastMeaningfulAudioProcessingLine(AudioProcessingLog, result.Summary));
                AudioProcessingStatusText = Texts.AudioProcessingCompletedStatus(Texts.AudioWorkflowLabel(_activeAudioProcessingMode ?? SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp));
                StatusText = AudioProcessingStatusText;
                return null;

            case EncodingJobState.Cancelled:
                SetAudioProcessingDisplayState(EncodingJobState.Cancelled);
                ClampAudioProcessingProgressForTerminalState(EncodingJobState.Cancelled);
                SetAudioProcessingPhaseLabel(string.Empty);
                ApplyFinalAudioProcessingLogLine(
                    UsesCompactAudioProcessingLog(_activeAudioProcessingMode)
                        ? LastMeaningfulAudioProcessingStageLine(result.Log, result.Summary)
                        : LastMeaningfulAudioProcessingLine(AudioProcessingLog, result.Summary));
                AudioProcessingStatusText = Texts.AudioProcessingCancelledStatus(Texts.AudioWorkflowLabel(_activeAudioProcessingMode ?? SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp));
                StatusText = AudioProcessingStatusText;
                return null;

            default:
                SetAudioProcessingDisplayState(EncodingJobState.Failed);
                ClampAudioProcessingProgressForTerminalState(EncodingJobState.Failed);
                SetAudioProcessingPhaseLabel(string.Empty);
                ApplyAudioProcessingFailureLog(result.Log, result.Summary);
                AudioProcessingStatusText = Texts.AudioProcessingFailedStatus(result.Summary);
                StatusText = AudioProcessingStatusText;
                return result.Summary;
        }
    }

    internal void CancelAudioProcessing()
    {
        if (!_isAudioProcessingRunning)
        {
            return;
        }

        var workflowLabel = Texts.AudioWorkflowLabel(_activeAudioProcessingMode ?? SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp);
        AudioProcessingStatusText = Texts.AudioProcessingCancellingStatus(workflowLabel);
        StatusText = AudioProcessingStatusText;

        _audioProcessingCancellationTokenSource?.Cancel();
        if (_activeAudioProcessingJobId is { } jobId)
        {
            _audioProcessingRunner.Abort(jobId);
        }
    }

    internal void ClearAudioProcessingTask()
    {
        if (_isAudioProcessingRunning)
        {
            return;
        }

        DisposeAudioProcessingCancellation();
        DisposeAudioSourceProbeCancellation();
        _audioSourceProbeVersion++;
        _audioSourceInfo = null;
        _audioSourceInfoError = null;
        _isAudioSourceInfoLoading = false;
        SetAudioProcessingDisplayState(null);
        _activeAudioProcessingMode = null;
        _lastAudioProcessingOutputPath = null;
        SetAudioProcessingRunningState(false, null);

        AudioProcessingSourcePath = string.Empty;
        AudioProcessingOutputPath = string.Empty;
        AudioProcessingAdditionalArguments = string.Empty;
        AudioOpusUseMappingFamily1 = false;
        AudioProcessingCommandLine = string.Empty;
        AudioProcessingLog = string.Empty;
        ResetAudioProcessingLogState();
        SetAudioProcessingPhaseLabel(string.Empty);
        SetAudioProcessingTelemetry(null);
        AudioProcessingProgressPercent = 0;
        AudioProcessingProgressIsIndeterminate = false;
        AudioProcessingStatusText = Texts.AudioProcessingIdleStatus;
        StatusText = AudioProcessingStatusText;

        OnPropertyChanged(nameof(AudioSourceInfoText));
        OnPropertyChanged(nameof(AudioWorkflowRecommendation));
        RaiseAudioProcessingInputPropertyChanges();
    }

    partial void InitializeAudioProcessingState()
    {
        AudioProcessingModule.InitializeState();
    }

    partial void DisposeAudioProcessingState()
    {
        CancelAudioProcessing();
        CancelPendingAudioProcessingInputRefresh();
        DisposeAudioProcessingCancellation();
        DisposeAudioSourceProbeCancellation();
    }

    partial void HandleAudioEnvironmentReadinessApplied()
    {
        AudioProcessingModule.HandleEnvironmentReadinessApplied();
    }

    partial void ApplyAudioProcessingLanguageState()
    {
        AudioProcessingModule.ApplyLanguageState();
    }

    private string? ValidateAudioProcessingConfiguration(bool requireSourceExists, out string? existingOutputPath)
    {
        existingOutputPath = null;

        if (TryCreateAudioProcessingRequest(requireSourceExists, out var request, out var error))
        {
            existingOutputPath = ResolveExistingAudioProcessingOutputPath(request!);
            return null;
        }

        return error;
    }

    private bool TryCreateAudioProcessingRequest(
        bool requireSourceExists,
        out AudioProcessingRequest? request,
        out string? error)
    {
        try
        {
            request = CreateAudioProcessingRequest(requireSourceExists);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            request = null;
            error = ex.Message;
            return false;
        }
    }

    private AudioProcessingRequest CreateAudioProcessingRequest(bool requireSourceExists)
    {
        if (SelectedAudioWorkflow is null)
        {
            throw new InvalidOperationException(Texts.AudioCapabilityPreparing);
        }

        if (string.IsNullOrWhiteSpace(AudioProcessingSourcePath))
        {
            throw new InvalidOperationException(Texts.AudioSourceMissingError);
        }

        if (string.IsNullOrWhiteSpace(AudioProcessingOutputPath))
        {
            throw new InvalidOperationException(Texts.AudioOutputMissingError);
        }

        var normalizedSource = Path.GetFullPath(AudioProcessingSourcePath.Trim());
        var normalizedOutputDirectory = Path.GetFullPath(AudioProcessingOutputPath.Trim());

        if (requireSourceExists && !File.Exists(normalizedSource))
        {
            throw new FileNotFoundException(Texts.AudioSourceMissingError, normalizedSource);
        }

        if (requireSourceExists && File.Exists(normalizedOutputDirectory))
        {
            throw new InvalidOperationException(Texts.AudioOutputDirectoryInvalidError);
        }

        var capabilityState = GetSelectedAudioCapabilityState();
        var workflowLabel = Texts.AudioWorkflowLabel(SelectedAudioWorkflow.Value);
        if (capabilityState != ReadinessState.Ready)
        {
            throw new InvalidOperationException(Texts.AudioWorkflowCapabilityMissingError(workflowLabel));
        }

        var eac3ToOutputFormat = ResolveSelectedEac3ToOutputFormat();
        var eac3ToAdditionalArguments = ParseEac3ToAdditionalArguments();
        var opusBitrateKbps = ResolveOpusBitrateKbps();
        switch (SelectedAudioWorkflow.Value)
        {
            case AudioProcessingMode.Eac3To:
                if (!eac3ToOutputFormat.HasValue)
                {
                    throw new InvalidOperationException(Texts.AudioEac3ToOutputFormatMissingError);
                }

                EnsureDirectAudioSourceSupported(normalizedSource, workflowLabel);
                break;

            case AudioProcessingMode.Ddp:
                break;

            case AudioProcessingMode.Opus:
                break;
        }

        var normalizedOutput = ResolveAudioProcessingOutputPath(
            normalizedSource,
            normalizedOutputDirectory,
            SelectedAudioWorkflow.Value,
            eac3ToOutputFormat);

        if (string.Equals(normalizedSource, normalizedOutput, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Texts.AudioSourceOutputPathConflictError);
        }

        var request = new AudioProcessingRequest(
            Guid.NewGuid(),
            normalizedSource,
            normalizedOutput,
            SelectedAudioWorkflow.Value,
            eac3ToOutputFormat,
            eac3ToAdditionalArguments,
            _audioSourceInfo?.DurationSeconds,
            _audioSourceInfo?.Channels,
            _audioSourceInfo?.ChannelLayout,
            opusBitrateKbps,
            AudioOpusUseMappingFamily1);
        RequestValidation.ValidateAudioProcessingRequest(request);
        return request;
    }

    private void EnsureDirectAudioSourceSupported(string sourcePath, string workflowLabel)
    {
        var extension = Path.GetExtension(sourcePath);
        if (!string.IsNullOrWhiteSpace(extension) && UnsupportedDirectAudioContainerExtensions.Contains(extension))
        {
            throw new InvalidOperationException(Texts.AudioDirectSourceUnsupportedError(workflowLabel));
        }
    }

    private AudioEac3ToOutputFormat? ResolveSelectedEac3ToOutputFormat()
    {
        return SelectedAudioEac3ToOutputFormat?.Value;
    }

    private IReadOnlyList<string> ParseEac3ToAdditionalArguments()
    {
        if (SelectedAudioWorkflow?.Value != AudioProcessingMode.Eac3To)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(AudioProcessingAdditionalArguments))
        {
            return [];
        }

        return SplitCommandLineArguments(AudioProcessingAdditionalArguments);
    }

    private static IReadOnlyList<string> SplitCommandLineArguments(string value)
    {
        try
        {
            return CommandArgumentTokenizer.Tokenize(value, throwOnUnclosedQuote: true);
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("eac3to 额外参数中的引号未闭合。");
        }
    }

    private int? ResolveOpusBitrateKbps()
    {
        if (SelectedAudioWorkflow?.Value != AudioProcessingMode.Opus)
        {
            return null;
        }

        return SelectedAudioOpusBitrate?.Value
            ?? throw new InvalidOperationException(Texts.AudioOpusBitrateMissingError);
    }

    private CapabilityReadiness? GetSelectedAudioCapabilityReadiness()
    {
        if (SelectedAudioWorkflow is null)
        {
            return null;
        }

        var capabilityKind = SelectedAudioWorkflow.Value switch
        {
            AudioProcessingMode.Eac3To => EnvironmentCapabilityKind.AudioFlac,
            AudioProcessingMode.Ddp => EnvironmentCapabilityKind.AudioDdp,
            AudioProcessingMode.Opus => EnvironmentCapabilityKind.AudioOpus,
            _ => EnvironmentCapabilityKind.AudioFlac
        };

        var capability = _environmentReadinessReport?.Capabilities.FirstOrDefault(readiness => readiness.Kind == capabilityKind)
            ?? BuildCachedAudioCapabilityReadiness(capabilityKind);
        if (capabilityKind == EnvironmentCapabilityKind.AudioOpus
            && AudioOpusUseMappingFamily1
            && CanUseFfmpegOpusMappingFamily1(_audioSourceInfo?.Channels, _audioSourceInfo?.ChannelLayout))
        {
            return OverrideOpusCapabilityForFfmpegMappingFamily(capability);
        }

        return capability;
    }

    private CapabilityReadiness? BuildCachedAudioCapabilityReadiness(EnvironmentCapabilityKind capabilityKind)
    {
        if (!SetupGuideModule.HasSetupGuideStatusReport)
        {
            return null;
        }

        CapabilityRequirementReadiness[]? requirements = capabilityKind switch
        {
            EnvironmentCapabilityKind.AudioFlac =>
                [BuildCachedAudioRequirement(RegisteredToolKind.Eac3To)],
            EnvironmentCapabilityKind.AudioDdp =>
            [
                BuildCachedAudioRequirement(RegisteredToolKind.Deew),
                BuildCachedAudioRequirement(RegisteredToolKind.Dee),
                BuildCachedAudioRequirement(RegisteredToolKind.Ffmpeg),
                BuildCachedAudioRequirement(RegisteredToolKind.Ffprobe)
            ],
            EnvironmentCapabilityKind.AudioOpus =>
            [
                BuildCachedAudioRequirement(RegisteredToolKind.Ffmpeg),
                BuildCachedAudioRequirement(RegisteredToolKind.OpusExt)
            ],
            _ => null
        };

        return requirements is null
            ? null
            : new CapabilityReadiness(
                capabilityKind,
                ReadinessStateResolver.ResolveFromRequirements(requirements),
                requirements);
    }

    private CapabilityRequirementReadiness BuildCachedAudioRequirement(params RegisteredToolKind[] candidateTools)
    {
        var candidateResults = candidateTools
            .Select(BuildCachedAudioToolProbeResult)
            .ToArray();

        return new CapabilityRequirementReadiness(
            new CapabilityToolRequirement(candidateTools),
            candidateResults);
    }

    private ToolProbeResult BuildCachedAudioToolProbeResult(RegisteredToolKind kind)
    {
        var status = ResolveCachedAudioToolStatus(kind);
        return new ToolProbeResult(
            kind,
            status.State,
            string.IsNullOrWhiteSpace(status.ExecutablePath) ? ToolDetectionSource.None : ToolDetectionSource.SpecialLocation,
            string.Empty,
            status.ExecutablePath,
            status.InstalledVersion,
            status.Detail,
            status.ReleaseUrl);
    }

    private SetupDependencyStatus ResolveCachedAudioToolStatus(RegisteredToolKind kind)
    {
        var dependencyKind = kind switch
        {
            RegisteredToolKind.Ffmpeg or RegisteredToolKind.Ffprobe => SetupDependencyKind.FfmpegBundle,
            RegisteredToolKind.Eac3To => SetupDependencyKind.Eac3To,
            RegisteredToolKind.Deew => SetupDependencyKind.Deew,
            RegisteredToolKind.Dee => SetupDependencyKind.Dee,
            RegisteredToolKind.OpusExt => SetupDependencyKind.OpusExt,
            _ => throw new InvalidOperationException($"Unsupported cached audio tool mapping: {kind}.")
        };

        return SetupGuideModule.ResolveSetupStatus(dependencyKind);
    }

    private static CapabilityReadiness? OverrideOpusCapabilityForFfmpegMappingFamily(CapabilityReadiness? capability)
    {
        if (capability is null)
        {
            return null;
        }

        var filteredRequirements = capability.Requirements
            .Where(requirement => !requirement.Requirement.CandidateTools.Contains(RegisteredToolKind.OpusExt))
            .ToArray();

        if (filteredRequirements.Length == 0)
        {
            return capability;
        }

        return new CapabilityReadiness(
            capability.Kind,
            ReadinessStateResolver.ResolveFromRequirements(filteredRequirements),
            filteredRequirements);
    }

    private static bool CanUseFfmpegOpusMappingFamily1(int? channelCount, string? channelLayout)
    {
        if (channelCount is > 8)
        {
            return false;
        }

        var layout = NormalizeAudioChannelLayoutName(channelLayout);
        return layout is "mono"
            or "stereo"
            or "3.0"
            or "quad"
            or "quad(side)"
            or "5.0"
            or "5.0(side)"
            or "5.1"
            or "5.1(side)"
            or "6.1"
            or "6.1(back)"
            or "7.1";
    }

    private static string NormalizeAudioChannelLayoutName(string? channelLayout)
    {
        return string.IsNullOrWhiteSpace(channelLayout)
            ? string.Empty
            : channelLayout.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    private ReadinessState GetSelectedAudioCapabilityState()
    {
        return GetSelectedAudioCapabilityReadiness()?.State ?? ReadinessState.Unknown;
    }

    private string GetAudioProcessingSuggestedExtension()
    {
        return SelectedAudioWorkflow?.Value switch
        {
            AudioProcessingMode.Eac3To => ResolveSelectedEac3ToOutputFormat() switch
            {
                AudioEac3ToOutputFormat.Ac3 => "ac3",
                _ => "flac"
            },
            AudioProcessingMode.Opus => "opus",
            AudioProcessingMode.Ddp when IsDdpEb3Output() => "eb3",
            AudioProcessingMode.Ddp => "ec3",
            _ => "audio"
        };
    }

    private static string ResolveAudioProcessingOutputPath(
        string sourcePath,
        string outputDirectory,
        AudioProcessingMode workflow,
        AudioEac3ToOutputFormat? eac3ToOutputFormat)
    {
        if (workflow == AudioProcessingMode.Ddp)
        {
            return outputDirectory;
        }

        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "audio";
        }

        var extension = workflow switch
        {
            AudioProcessingMode.Eac3To when eac3ToOutputFormat == AudioEac3ToOutputFormat.Ac3 => "ac3",
            AudioProcessingMode.Eac3To => "flac",
            AudioProcessingMode.Opus => "opus",
            _ => "audio"
        };

        return Path.Combine(outputDirectory, $"{fileName}.{extension}");
    }

    private string? TryResolveAudioProcessingOutputPreviewPath()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AudioProcessingSourcePath) || SelectedAudioWorkflow is null)
            {
                return null;
            }

            var normalizedSource = Path.GetFullPath(AudioProcessingSourcePath.Trim());
            var outputDirectory = !string.IsNullOrWhiteSpace(AudioProcessingOutputPath)
                ? Path.GetFullPath(AudioProcessingOutputPath.Trim())
                : Path.GetDirectoryName(normalizedSource) ?? Environment.CurrentDirectory;
            return ResolveAudioProcessingPreviewPath(
                normalizedSource,
                outputDirectory,
                SelectedAudioWorkflow.Value,
                ResolveSelectedEac3ToOutputFormat(),
                GetAudioProcessingSuggestedExtension());
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveAudioProcessingPreviewPath(
        string sourcePath,
        string outputDirectory,
        AudioProcessingMode workflow,
        AudioEac3ToOutputFormat? eac3ToOutputFormat,
        string ddpExtension)
    {
        if (workflow != AudioProcessingMode.Ddp)
        {
            return ResolveAudioProcessingOutputPath(sourcePath, outputDirectory, workflow, eac3ToOutputFormat);
        }

        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "audio";
        }

        var extension = string.IsNullOrWhiteSpace(ddpExtension)
            ? "ec3"
            : ddpExtension.Trim().TrimStart('.');
        return Path.Combine(outputDirectory, $"{fileName}.{extension}");
    }

    private bool IsDdpEb3Output()
    {
        return _audioSourceInfo?.Channels == 8;
    }

    private string? ResolveExistingAudioProcessingOutputPath(AudioProcessingRequest request)
    {
        if (request.Mode != AudioProcessingMode.Ddp)
        {
            return File.Exists(request.OutputPath) ? request.OutputPath : null;
        }

        var preferredExtension = IsDdpEb3Output() ? "eb3" : "ec3";
        var baseName = Path.GetFileNameWithoutExtension(request.SourcePath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return null;
        }

        foreach (var extension in EnumeratePreferredDdpExtensions(preferredExtension))
        {
            var candidatePath = Path.Combine(request.OutputPath, $"{baseName}.{extension}");
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumeratePreferredDdpExtensions(string preferredExtension)
    {
        yield return preferredExtension;

        var fallbackExtension = string.Equals(preferredExtension, "eb3", StringComparison.OrdinalIgnoreCase)
            ? "ec3"
            : "eb3";

        yield return fallbackExtension;
    }

    private void TryPopulateAudioProcessingOutputPathIfEmpty()
    {
        if (SelectedAudioWorkflow is null || string.IsNullOrWhiteSpace(AudioProcessingSourcePath))
        {
            return;
        }

        var sourceDirectory = Path.GetDirectoryName(AudioProcessingSourcePath);
        var suggestedPath = sourceDirectory ?? Environment.CurrentDirectory;

        if (!string.IsNullOrWhiteSpace(AudioProcessingOutputPath)
            && !string.Equals(AudioProcessingOutputPath, _lastAudioProcessingOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetAudioProcessingOutputPath(suggestedPath);
    }

    private void SetAudioProcessingOutputPath(string path)
    {
        _isUpdatingAudioProcessingOutputPath = true;

        try
        {
            AudioProcessingOutputPath = path;
            _lastAudioProcessingOutputPath = path;
        }
        finally
        {
            _isUpdatingAudioProcessingOutputPath = false;
        }
    }

    private void ScheduleAudioProcessingInputRefresh(bool probeSource)
    {
        CancelPendingAudioProcessingInputRefresh();
        var requestVersion = Interlocked.Increment(ref _audioProcessingInputRefreshVersion);
        var cancellationTokenSource = new CancellationTokenSource();
        _audioProcessingInputRefreshCancellationTokenSource = cancellationTokenSource;

        _ = RefreshAudioProcessingInputDeferredAsync(requestVersion, probeSource, cancellationTokenSource.Token);
    }

    private async Task RefreshAudioProcessingInputDeferredAsync(
        int requestVersion,
        bool probeSource,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InputPathRefreshDelay, cancellationToken);
            if (!IsAudioProcessingInputRefreshCurrent(requestVersion, cancellationToken))
            {
                return;
            }

            var hasPathState = !string.IsNullOrWhiteSpace(AudioProcessingSourcePath) || !string.IsNullOrWhiteSpace(AudioProcessingOutputPath);
            SetAudioProcessingInputRefreshPending(hasPathState);

            if (hasPathState && !_isAudioProcessingRunning)
            {
                AudioProcessingStatusText = Texts.AudioProcessingInputPreparingStatus;
                await Task.Yield();
                if (!IsAudioProcessingInputRefreshCurrent(requestVersion, cancellationToken))
                {
                    return;
                }
            }

            _isApplyingDeferredAudioProcessingInputRefresh = true;
            try
            {
                TryPopulateAudioProcessingOutputPathIfEmpty();
                RaiseAudioProcessingInputPropertyChanges();
                if (probeSource)
                {
                    StartAudioSourceProbe(AudioProcessingSourcePath);
                }

                RefreshAudioProcessingCommandPreview();
            }
            finally
            {
                _isApplyingDeferredAudioProcessingInputRefresh = false;
            }

            if (!IsAudioProcessingInputRefreshCurrent(requestVersion, cancellationToken))
            {
                return;
            }

            SetAudioProcessingInputRefreshPending(false);
            if (!_isAudioProcessingRunning && string.Equals(AudioProcessingStatusText, Texts.AudioProcessingInputPreparingStatus, StringComparison.Ordinal))
            {
                AudioProcessingStatusText = Texts.AudioProcessingIdleStatus;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool IsAudioProcessingInputRefreshCurrent(int requestVersion, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && requestVersion == Volatile.Read(ref _audioProcessingInputRefreshVersion);
    }

    private void SetAudioProcessingInputRefreshPending(bool isPending)
    {
        if (_isAudioProcessingInputRefreshPending == isPending)
        {
            return;
        }

        _isAudioProcessingInputRefreshPending = isPending;
        OnPropertyChanged(nameof(AudioProcessingOutputPreviewText));
    }

    private void CancelAudioSourceProbeForPendingInputRefresh()
    {
        DisposeAudioSourceProbeCancellation();
        Interlocked.Increment(ref _audioSourceProbeVersion);
    }

    private void StartAudioSourceProbe(string sourcePath)
    {
        DisposeAudioSourceProbeCancellation();
        _audioSourceInfo = null;
        _audioSourceInfoError = null;
        _isAudioSourceInfoLoading = false;
        AudioOpusUseMappingFamily1 = false;

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            OnPropertyChanged(nameof(AudioSourceInfoText));
            OnPropertyChanged(nameof(AudioWorkflowRecommendation));
            RaiseAudioProcessingInputPropertyChanges();
            return;
        }

        _isAudioSourceInfoLoading = true;
        OnPropertyChanged(nameof(AudioSourceInfoText));

        _audioSourceProbeCancellationTokenSource = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _audioSourceProbeVersion);
        _ = RefreshAudioSourceInfoAsync(sourcePath, version, _audioSourceProbeCancellationTokenSource.Token);
    }

    private async Task RefreshAudioSourceInfoAsync(string sourcePath, int version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AudioSourceProbeDebounceInterval, cancellationToken);
            var sourceExists = await Task.Run(() => File.Exists(sourcePath), cancellationToken);
            if (!sourceExists)
            {
                if (version == _audioSourceProbeVersion)
                {
                    _audioSourceInfo = null;
                    _audioSourceInfoError = null;
                }

                return;
            }

            var info = await _audioSourceInfoService.ProbeAsync(sourcePath, cancellationToken);
            if (version != _audioSourceProbeVersion)
            {
                return;
            }

            _audioSourceInfo = info;
            _audioSourceInfoError = null;
            AudioOpusUseMappingFamily1 = info?.HasSurround51Or71Channels() == true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (version != _audioSourceProbeVersion)
            {
                return;
            }

            _audioSourceInfo = null;
            _audioSourceInfoError = ex.Message;
            AudioOpusUseMappingFamily1 = false;
        }
        finally
        {
            if (version == _audioSourceProbeVersion)
            {
                _isAudioSourceInfoLoading = false;
                OnPropertyChanged(nameof(AudioSourceInfoText));
                OnPropertyChanged(nameof(AudioWorkflowRecommendation));
                RaiseAudioProcessingInputPropertyChanges();
                TryPopulateAudioProcessingOutputPathIfEmpty();
                RefreshAudioProcessingCommandPreview();
            }
        }
    }

    internal void RefreshAudioProcessingCommandPreview()
    {
        if (_isAudioProcessingRunning)
        {
            return;
        }

        if (!TryCreateAudioProcessingRequest(requireSourceExists: false, out var request, out _)
            || request is null)
        {
            AudioProcessingCommandLine = string.Empty;
            return;
        }

        AudioProcessingCommandLine = _audioProcessingRunner.BuildDisplayCommand(request);
    }

    private void ApplyAudioProcessingProgress(AudioProcessingProgress progress)
    {
        if (_activeAudioProcessingJobId != progress.JobId)
        {
            return;
        }

        SetAudioProcessingDisplayState(progress.State);

        if (progress.State == EncodingJobState.Completed)
        {
            ClampAudioProcessingProgressForTerminalState(EncodingJobState.Completed);
        }
        else if (progress.ProgressFraction.HasValue)
        {
            AudioProcessingProgressIsIndeterminate = false;
            AudioProcessingProgressPercent = progress.ProgressFraction.Value * 100;
        }
        else if (progress.State is EncodingJobState.Failed or EncodingJobState.Cancelled)
        {
            ClampAudioProcessingProgressForTerminalState(progress.State);
        }
        else if (_isAudioProcessingRunning)
        {
            AudioProcessingProgressIsIndeterminate = true;
        }

        if (!string.IsNullOrWhiteSpace(progress.Summary))
        {
            AudioProcessingStatusText = progress.Summary;
        }

        SetAudioProcessingPhaseLabel(progress.PhaseLabel ?? string.Empty);
        SetAudioProcessingTelemetry(progress.Telemetry);
        ApplyAudioProcessingLogUpdate(progress);
        RaiseDashboardCardActivityPropertyChanges();
    }

    private void ReplaceAudioProcessingLogLine(string line)
    {
        var normalized = string.IsNullOrWhiteSpace(line) ? string.Empty : line.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || string.Equals(AudioProcessingLog, normalized, StringComparison.Ordinal)
            || string.Equals(AudioProcessingCommandLine, normalized, StringComparison.Ordinal))
        {
            return;
        }

        AudioProcessingLog = normalized;
    }

    private void ApplyAudioProcessingLogUpdate(AudioProcessingProgress progress)
    {
        var activeMode = _activeAudioProcessingMode ?? SelectedAudioWorkflow?.Value;
        if (!UsesCompactAudioProcessingLog(activeMode))
        {
            ReplaceAudioProcessingLogLine(progress.DetailLine);
            return;
        }

        ReplaceCompactAudioProcessingLog(progress.PhaseLabel, progress.DetailLine);
    }

    private void ApplyFinalAudioProcessingLogLine(string line)
    {
        var activeMode = _activeAudioProcessingMode ?? SelectedAudioWorkflow?.Value;
        if (!UsesCompactAudioProcessingLog(activeMode))
        {
            ReplaceAudioProcessingLogLine(line);
            return;
        }

        ReplaceCompactAudioProcessingLog(null, line);
    }

    private void ApplyAudioProcessingFailureLog(string log, string fallback)
    {
        AudioProcessingLog = BuildAudioProcessingFailureLog(log, fallback);
    }

    private void ReplaceCompactAudioProcessingLog(string? phaseLabel, string line)
    {
        var normalized = string.IsNullOrWhiteSpace(line) ? string.Empty : line.Trim();
        var normalizedPhase = string.IsNullOrWhiteSpace(phaseLabel) ? string.Empty : phaseLabel.Trim();
        if (!string.Equals(_audioProcessingLogPhaseMarker, normalizedPhase, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(normalizedPhase))
        {
            _audioProcessingLogPhaseMarker = normalizedPhase;
            AppendAudioProcessingStageLogLine(normalizedPhase);
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            RefreshCompactAudioProcessingLogText();
            return;
        }

        if ((_activeAudioProcessingMode ?? SelectedAudioWorkflow?.Value) == AudioProcessingMode.Ddp
            && string.Equals(normalized, Texts.AudioProcessingDdpWarmupHint, StringComparison.Ordinal))
        {
            _audioProcessingLiveLogLine = normalized;
            RefreshCompactAudioProcessingLogText();
            return;
        }

        if (IsCompactAudioProcessingLiveLine(_activeAudioProcessingMode ?? SelectedAudioWorkflow?.Value, normalized))
        {
            if (!string.Equals(_audioProcessingLiveLogLine, normalized, StringComparison.Ordinal))
            {
                _audioProcessingLiveLogLine = normalized;
                RefreshCompactAudioProcessingLogText();
            }

            return;
        }

        _audioProcessingLiveLogLine = string.Empty;
        AppendAudioProcessingStageLogLine(normalized);
    }

    private void AppendAudioProcessingStageLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (_audioProcessingLogStageLines.Count > 0
            && string.Equals(_audioProcessingLogStageLines[^1], line, StringComparison.Ordinal))
        {
            RefreshCompactAudioProcessingLogText();
            return;
        }

        _audioProcessingLogStageLines.Add(line);
        if (_audioProcessingLogStageLines.Count > AudioProcessingStageLogLimit)
        {
            _audioProcessingLogStageLines.RemoveAt(0);
        }

        RefreshCompactAudioProcessingLogText();
    }

    private void RefreshCompactAudioProcessingLogText()
    {
        var lines = new List<string>(_audioProcessingLogStageLines);
        if (!string.IsNullOrWhiteSpace(_audioProcessingLiveLogLine))
        {
            lines.Add(_audioProcessingLiveLogLine);
        }

        AudioProcessingLog = string.Join(Environment.NewLine, lines);
    }

    private static string LastMeaningfulAudioProcessingLine(string log, string fallback)
    {
        var line = log
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static entry => !string.IsNullOrWhiteSpace(entry));

        return string.IsNullOrWhiteSpace(line) ? fallback : line;
    }

    private static string LastMeaningfulAudioProcessingStageLine(string log, string fallback)
    {
        var line = log
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static entry =>
                !string.IsNullOrWhiteSpace(entry)
                && !entry.StartsWith("process:", StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(line) ? fallback : line;
    }

    private static string BuildAudioProcessingFailureLog(string log, string fallback)
    {
        var lines = log
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(static line => line.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(fallback)
            && lines.All(line => !string.Equals(line, fallback, StringComparison.Ordinal)))
        {
            lines.Add(fallback.Trim());
        }

        if (lines.Count == 0)
        {
            return fallback;
        }

        var highlighted = lines
            .Where(LooksLikeAudioProcessingFailureLine)
            .TakeLast(12)
            .ToList();

        if (highlighted.Count == 0)
        {
            highlighted = lines.TakeLast(8).ToList();
        }

        return string.Join(Environment.NewLine, highlighted);
    }

    private static bool LooksLikeAudioProcessingFailureLine(string line)
    {
        return line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || line.Contains("traceback", StringComparison.OrdinalIgnoreCase)
            || line.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || line.Contains("退出代码", StringComparison.OrdinalIgnoreCase)
            || line.Contains("取消", StringComparison.OrdinalIgnoreCase);
    }

    private string FormatAudioProcessingPercent(double percent)
    {
        var activeMode = _activeAudioProcessingMode ?? SelectedAudioWorkflow?.Value;
        return activeMode == AudioProcessingMode.Opus && _isAudioProcessingRunning && percent < 10
            ? $"{percent:0.##}%"
            : $"{percent:0.#}%";
    }

    private static bool UsesCompactAudioProcessingLog(AudioProcessingMode? mode)
    {
        return mode is AudioProcessingMode.Eac3To or AudioProcessingMode.Ddp or AudioProcessingMode.Opus;
    }

    private static bool IsCompactAudioProcessingLiveLine(AudioProcessingMode? mode, string line)
    {
        return ToolLogLineClassifier.IsAudioTransientLine(mode, line);
    }

    private void ResetAudioProcessingLogState()
    {
        _audioProcessingLogStageLines.Clear();
        _audioProcessingLiveLogLine = string.Empty;
        _audioProcessingLogPhaseMarker = string.Empty;
    }

    internal void SetAudioProcessingDisplayState(EncodingJobState? state)
    {
        if (_audioProcessingDisplayState == state)
        {
            return;
        }

        _audioProcessingDisplayState = state;
        OnPropertyChanged(nameof(AudioProcessingPresentationState));
        OnPropertyChanged(nameof(AudioProcessingStatusPanelBorderBrush));
        OnPropertyChanged(nameof(AudioProcessingProgressTrackBrush));
        OnPropertyChanged(nameof(AudioProcessingProgressBorderBrush));
        OnPropertyChanged(nameof(AudioProcessingProgressFillBrush));
    }

    private void ClampAudioProcessingProgressForTerminalState(EncodingJobState state)
    {
        AudioProcessingProgressIsIndeterminate = false;
        AudioProcessingProgressPercent = state == EncodingJobState.Completed
            ? 100
            : Math.Min(AudioProcessingProgressPercent, 99.9);
    }

    private static Brush ResolveAudioProcessingProgressTrackBrush(EncodingJobState? state)
    {
        return state switch
        {
            EncodingJobState.Failed => ResolveBrush("AppErrorSoftBrush"),
            EncodingJobState.Cancelled => ResolveBrush("AppNeutralSoftBrush"),
            _ => ResolveBrush("QueueProgressSoftBrush")
        };
    }

    private static Brush ResolveAudioProcessingProgressBorderBrush(EncodingJobState? state)
    {
        return state switch
        {
            EncodingJobState.Failed => ResolveBrush("AppErrorBrush"),
            EncodingJobState.Cancelled => ResolveBrush("AppNeutralBrush"),
            _ => ResolveBrush("QueueProgressFillBrush")
        };
    }

    private static Brush ResolveAudioProcessingProgressFillBrush(EncodingJobState? state)
    {
        return state switch
        {
            EncodingJobState.Failed => ResolveBrush("AppErrorBrush"),
            EncodingJobState.Cancelled => ResolveBrush("AppNeutralBrush"),
            _ => ResolveBrush("QueueProgressAreaBrush")
        };
    }

    private void SetAudioProcessingRunningState(bool isRunning, Guid? activeJobId)
    {
        if (_isAudioProcessingRunning == isRunning && _activeAudioProcessingJobId == activeJobId)
        {
            return;
        }

        _isAudioProcessingRunning = isRunning;
        _activeAudioProcessingJobId = activeJobId;
        OnPropertyChanged(nameof(IsAudioProcessingRunning));
        OnPropertyChanged(nameof(HasRunningAppWork));
        OnPropertyChanged(nameof(CanStartAudioProcessing));
        OnPropertyChanged(nameof(CanCancelAudioProcessing));
        OnPropertyChanged(nameof(CanClearAudioProcessingTask));
        OnPropertyChanged(nameof(AudioProcessingProgressPercentText));
        OnPropertyChanged(nameof(AudioProcessingProgressPrimaryText));
        OnPropertyChanged(nameof(AudioProcessingProgressSecondaryText));
        OnPropertyChanged(nameof(AudioProcessingProgressSecondaryVisibility));
        OnPropertyChanged(nameof(AudioProcessingProgressLabel));
        OnPropertyChanged(nameof(AudioProcessingProgressHintVisibility));
        RaiseDashboardCardActivityPropertyChanges();

        if (isRunning)
        {
            CancelPendingQueueCompletionActionWait();
        }
        else
        {
            TryScheduleQueueCompletionActionAfterSuccessfulQueueDrain();
        }
    }

    private void SetAudioProcessingTelemetry(AudioProcessingTelemetry? telemetry)
    {
        if (EqualityComparer<AudioProcessingTelemetry?>.Default.Equals(_audioProcessingTelemetry, telemetry))
        {
            return;
        }

        _audioProcessingTelemetry = telemetry;
        OnPropertyChanged(nameof(AudioProcessingProgressSecondaryText));
        OnPropertyChanged(nameof(AudioProcessingProgressSecondaryVisibility));
    }

    private void SetAudioProcessingPhaseLabel(string phaseLabel)
    {
        var normalized = phaseLabel ?? string.Empty;
        if (string.Equals(_audioProcessingPhaseLabel, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _audioProcessingPhaseLabel = normalized;
        OnPropertyChanged(nameof(AudioProcessingProgressSecondaryText));
        OnPropertyChanged(nameof(AudioProcessingProgressSecondaryVisibility));
    }

    private void DisposeAudioProcessingCancellation()
    {
        _audioProcessingCancellationTokenSource?.Dispose();
        _audioProcessingCancellationTokenSource = null;
    }

    private void DisposeAudioSourceProbeCancellation()
    {
        _audioSourceProbeCancellationTokenSource?.Cancel();
        _audioSourceProbeCancellationTokenSource?.Dispose();
        _audioSourceProbeCancellationTokenSource = null;
    }

    private void CancelPendingAudioProcessingInputRefresh()
    {
        _audioProcessingInputRefreshCancellationTokenSource?.Cancel();
        _audioProcessingInputRefreshCancellationTokenSource?.Dispose();
        _audioProcessingInputRefreshCancellationTokenSource = null;
    }

    internal void RaiseAudioProcessingInputPropertyChanges()
    {
        OnPropertyChanged(nameof(CanStartAudioProcessing));
        OnPropertyChanged(nameof(CanClearAudioProcessingTask));
        OnPropertyChanged(nameof(AudioProcessingOutputHeader));
        OnPropertyChanged(nameof(AudioProcessingOutputBrowseButtonText));
        OnPropertyChanged(nameof(AudioProcessingSuggestedOutputExtension));
        OnPropertyChanged(nameof(AudioProcessingSuggestedOutputFileName));
        OnPropertyChanged(nameof(AudioProcessingOutputPreviewText));
        OnPropertyChanged(nameof(AudioCapabilitySummary));
    }

    internal void RaiseAudioProcessingEnvironmentPropertyChanges()
    {
        OnPropertyChanged(nameof(CanStartAudioProcessing));
        OnPropertyChanged(nameof(CanClearAudioProcessingTask));
        OnPropertyChanged(nameof(AudioCapabilitySummary));
    }

    internal void RaiseAudioProcessingLanguagePropertyChanges()
    {
        OnPropertyChanged(nameof(SelectedAudioWorkflow));
        OnPropertyChanged(nameof(SelectedAudioEac3ToOutputFormat));
        OnPropertyChanged(nameof(SelectedAudioOpusBitrate));
        OnPropertyChanged(nameof(AudioOpusUseMappingFamily1));
        OnPropertyChanged(nameof(AudioEac3ToOptionsVisibility));
        OnPropertyChanged(nameof(AudioOpusOptionsVisibility));
        RaiseAudioProcessingInputPropertyChanges();
        OnPropertyChanged(nameof(AudioSourceInfoText));
        OnPropertyChanged(nameof(AudioWorkflowRecommendation));
        OnPropertyChanged(nameof(AudioProcessingProgressPrimaryText));
        OnPropertyChanged(nameof(AudioProcessingProgressSecondaryText));
        OnPropertyChanged(nameof(AudioProcessingProgressSecondaryVisibility));
        OnPropertyChanged(nameof(CanCancelAudioProcessing));
        OnPropertyChanged(nameof(AudioProcessingProgressLabel));
        OnPropertyChanged(nameof(AudioProcessingProgressHint));
        OnPropertyChanged(nameof(AudioProcessingProgressHintVisibility));
    }

    internal void BeginAudioProcessingLanguageStateApplication()
    {
        _isApplyingAudioProcessingLanguageState = true;
    }

    internal void EndAudioProcessingLanguageStateApplication()
    {
        _isApplyingAudioProcessingLanguageState = false;
    }
}
