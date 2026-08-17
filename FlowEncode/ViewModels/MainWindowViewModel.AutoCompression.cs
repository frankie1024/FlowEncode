using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Controls.Shared;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FlowEncode.ViewModels;

public partial class MainWindowViewModel
{
    private string _autoCompressionSourcePath = string.Empty;
    private string _autoCompressionOutputPath = string.Empty;
    private string _autoCompressionVideoParameters = string.Empty;
    private string _autoCompressionBackendArguments = string.Empty;
    private double _autoCompressionTargetVmaf = 95.0;
    private AutoCompressionMetric _autoCompressionMetric = AutoCompressionMetric.Vmaf;
    private double _autoCompressionProbes = 4;
    private double _autoCompressionWorkers;
    private double _autoCompressionProbingRate = 1;
    private string _autoCompressionProbeResolution = string.Empty;
    private string _autoCompressionInterpolationMethod = string.Empty;
    private EncoderOption? _selectedAutoEncoder;
    private string _autoCompressionStatusText = string.Empty;
    private string _autoCompressionCommandLine = string.Empty;
    private string _autoCompressionLog = string.Empty;
    private double _autoCompressionProgressPercent;
    private bool _autoCompressionProgressIsIndeterminate;
    private bool _isAutoCompressionRunning;
    private string? _lastAutoCompressionOutputPath;
    private bool _isUpdatingAutoCompressionOutputPath;
    private CancellationTokenSource? _autoCompressionCancellationTokenSource;
    private Guid? _activeAutoCompressionJobId;
    private EncodingJobState? _autoCompressionDisplayState;
    private TaskPresentationState _autoCompressionPresentationState;
    private bool _isAutoCompressionCancellationRequested;
    private bool _isAutoCompressionAdvancedOptionsExpanded;
    private readonly List<string> _autoCompressionLogStageLines = [];
    private string _autoCompressionLiveLogLine = string.Empty;
    private CancellationTokenSource? _autoCompressionInputRefreshCancellationTokenSource;
    private int _autoCompressionInputRefreshVersion;
    private bool _isApplyingDeferredAutoCompressionInputRefresh;
    private bool _isAutoCompressionInputRefreshPending;
    private const int AutoCompressionLogLimit = 120_000;
    private const int AutoCompressionStageLogLimit = 240;

    internal string AutoCompressionSourcePath
    {
        get => _autoCompressionSourcePath;
        set
        {
            if (SetProperty(ref _autoCompressionSourcePath, value))
            {
                ScheduleAutoCompressionInputRefresh();
            }
        }
    }

    internal string AutoCompressionOutputPath
    {
        get => _autoCompressionOutputPath;
        set
        {
            if (SetProperty(ref _autoCompressionOutputPath, value))
            {
                if (!_isUpdatingAutoCompressionOutputPath)
                {
                    _lastAutoCompressionOutputPath = null;
                }

                if (_isApplyingDeferredAutoCompressionInputRefresh)
                {
                    return;
                }

                ScheduleAutoCompressionInputRefresh();
            }
        }
    }

    internal EncoderOption? SelectedAutoEncoder
    {
        get => _selectedAutoEncoder;
        set
        {
            if (SetProperty(ref _selectedAutoEncoder, value))
            {
                ScheduleAutoCompressionInputRefresh();
            }
        }
    }

    internal string AutoCompressionVideoParameters
    {
        get => _autoCompressionVideoParameters;
        set
        {
            if (SetProperty(ref _autoCompressionVideoParameters, value))
            {
                OnPropertyChanged(nameof(CanStartAutoCompression));
                RefreshAutoCompressionCommandPreview();
            }
        }
    }

    internal double AutoCompressionTargetVmaf
    {
        get => _autoCompressionTargetVmaf;
        set
        {
            var normalized = NormalizeBoundedDouble(value, AutoCompressionTargetScoreMinimum, AutoCompressionTargetScoreMaximum);
            if (SetProperty(ref _autoCompressionTargetVmaf, normalized))
            {
                OnPropertyChanged(nameof(CanStartAutoCompression));
                OnPropertyChanged(nameof(AutoCompressionSuggestedOutputFileName));
                OnPropertyChanged(nameof(AutoCompressionOutputPreviewText));
                OnPropertyChanged(nameof(AutoCompressionMetricGuidanceText));
                RefreshAutoCompressionCommandPreview();
            }
        }
    }

    internal AutoCompressionMetricOption? SelectedAutoCompressionMetricOption
    {
        get => _selectedAutoCompressionMetricOption;
        set
        {
            if (SetProperty(ref _selectedAutoCompressionMetricOption, value) && value is not null)
            {
                AutoCompressionMetric = value.Value;
            }
        }
    }

    internal string AutoCompressionBackendArguments
    {
        get => _autoCompressionBackendArguments;
        set
        {
            var normalized = value.Trim();
            if (SetProperty(ref _autoCompressionBackendArguments, normalized))
            {
                OnPropertyChanged(nameof(CanStartAutoCompression));
                RefreshAutoCompressionCommandPreview();
            }
        }
    }

    internal AutoCompressionMetric AutoCompressionMetric
    {
        get => _autoCompressionMetric;
        set
        {
            if (SetProperty(ref _autoCompressionMetric, value))
            {
                _selectedAutoCompressionMetricOption = AutoCompressionMetricOptions.FirstOrDefault(option => option.Value == value)
                    ?? _selectedAutoCompressionMetricOption;
                OnPropertyChanged(nameof(SelectedAutoCompressionMetricOption));
                OnPropertyChanged(nameof(CanStartAutoCompression));
                OnPropertyChanged(nameof(AutoCompressionSuggestedOutputFileName));
                OnPropertyChanged(nameof(AutoCompressionOutputPreviewText));
                OnPropertyChanged(nameof(AutoCompressionMetricGuidanceText));
                OnPropertyChanged(nameof(AutoCompressionTargetScoreMinimum));
                OnPropertyChanged(nameof(AutoCompressionTargetScoreMaximum));
                AutoCompressionTargetVmaf = _autoCompressionTargetVmaf;
                RefreshAutoCompressionCommandPreview();
            }
        }
    }

    internal double AutoCompressionProbes
    {
        get => _autoCompressionProbes;
        set
        {
            var normalized = NormalizeBoundedDouble(value, 1, int.MaxValue);
            if (SetProperty(ref _autoCompressionProbes, normalized))
            {
                OnPropertyChanged(nameof(CanStartAutoCompression));
                RefreshAutoCompressionCommandPreview();
            }
        }
    }

    internal double AutoCompressionWorkers
    {
        get => _autoCompressionWorkers;
        set
        {
            var normalized = NormalizeBoundedDouble(value, 0, int.MaxValue);
            if (SetProperty(ref _autoCompressionWorkers, normalized))
            {
                OnPropertyChanged(nameof(CanStartAutoCompression));
                RefreshAutoCompressionCommandPreview();
            }
        }
    }

    internal double AutoCompressionProbingRate
    {
        get => _autoCompressionProbingRate;
        set
        {
            var normalized = NormalizeBoundedDouble(value, 1, int.MaxValue);
            if (SetProperty(ref _autoCompressionProbingRate, normalized))
            {
                OnPropertyChanged(nameof(CanStartAutoCompression));
                RefreshAutoCompressionCommandPreview();
            }
        }
    }

    internal string AutoCompressionProbeResolution
    {
        get => _autoCompressionProbeResolution;
        set
        {
            var normalized = value.Trim();
            if (SetProperty(ref _autoCompressionProbeResolution, normalized))
            {
                OnPropertyChanged(nameof(CanStartAutoCompression));
                RefreshAutoCompressionCommandPreview();
            }
        }
    }

    internal StringChoiceOption? SelectedAutoCompressionProbingStatisticOption
    {
        get => _selectedAutoCompressionProbingStatisticOption;
        set
        {
            if (SetProperty(ref _selectedAutoCompressionProbingStatisticOption, value) && value is not null)
            {
                AutoCompressionProbingStatistic = value.Value;
            }
        }
    }

    internal string AutoCompressionProbingStatistic
    {
        get => _selectedAutoCompressionProbingStatisticOption?.Value ?? "auto";
        set
        {
            var selectedOption = AutoCompressionProbingStatisticOptions.FirstOrDefault(option =>
                    string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
                ?? AutoCompressionProbingStatisticOptions.FirstOrDefault();
            if (SetProperty(ref _selectedAutoCompressionProbingStatisticOption, selectedOption))
            {
                OnPropertyChanged(nameof(SelectedAutoCompressionProbingStatisticOption));
                OnPropertyChanged(nameof(CanStartAutoCompression));
                RefreshAutoCompressionCommandPreview();
            }
        }
    }

    internal string AutoCompressionInterpolationMethod
    {
        get => _selectedAutoCompressionInterpolationMethodOption?.Value ?? string.Empty;
        set
        {
            var selectedOption = AutoCompressionInterpolationMethodOptions.FirstOrDefault(option =>
                    string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
                ?? AutoCompressionInterpolationMethodOptions.FirstOrDefault();
            if (SetProperty(ref _selectedAutoCompressionInterpolationMethodOption, selectedOption))
            {
                OnPropertyChanged(nameof(SelectedAutoCompressionInterpolationMethodOption));
                OnPropertyChanged(nameof(CanStartAutoCompression));
                RefreshAutoCompressionCommandPreview();
            }
        }
    }

    internal StringChoiceOption? SelectedAutoCompressionInterpolationMethodOption
    {
        get => _selectedAutoCompressionInterpolationMethodOption;
        set
        {
            if (SetProperty(ref _selectedAutoCompressionInterpolationMethodOption, value))
            {
                OnPropertyChanged(nameof(SelectedAutoCompressionInterpolationMethodOption));
                OnPropertyChanged(nameof(CanStartAutoCompression));
                RefreshAutoCompressionCommandPreview();
            }
        }
    }

    internal string AutoCompressionStatusText
    {
        get => _autoCompressionStatusText;
        private set
        {
            if (SetProperty(ref _autoCompressionStatusText, value))
            {
                RaiseDashboardCardActivityPropertyChanges();
            }
        }
    }

    internal string AutoCompressionCommandLine
    {
        get => _autoCompressionCommandLine;
        private set => SetProperty(ref _autoCompressionCommandLine, value);
    }

    internal string AutoCompressionLog
    {
        get => _autoCompressionLog;
        private set => SetProperty(ref _autoCompressionLog, value);
    }

    internal double AutoCompressionProgressPercent
    {
        get => _autoCompressionProgressPercent;
        private set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _autoCompressionProgressPercent, normalized))
            {
                OnPropertyChanged(nameof(AutoCompressionProgressLabel));
                OnPropertyChanged(nameof(AutoCompressionProgressPercentText));
                OnPropertyChanged(nameof(AutoCompressionProgressValue));
            }
        }
    }

    internal bool AutoCompressionProgressIsIndeterminate
    {
        get => _autoCompressionProgressIsIndeterminate;
        private set
        {
            if (SetProperty(ref _autoCompressionProgressIsIndeterminate, value))
            {
                OnPropertyChanged(nameof(AutoCompressionProgressLabel));
                OnPropertyChanged(nameof(AutoCompressionProgressPercentText));
                OnPropertyChanged(nameof(AutoCompressionProgressHintVisibility));
            }
        }
    }

    internal string AutoCompressionOutputPreviewText => _isAutoCompressionInputRefreshPending
        ? Texts.OutputPreviewUpdating
        : BuildOutputPreviewText(TryResolveAutoCompressionOutputPreviewPath());

    internal string AutoCompressionMetricGuidanceText => Texts.AutoCompressionMetricGuidance(AutoCompressionMetric);

    internal double AutoCompressionTargetScoreMinimum => 0;

    internal double AutoCompressionTargetScoreMaximum =>
        AutoCompressionMetric is AutoCompressionMetric.Vmaf or AutoCompressionMetric.Ssimulacra2
            ? 100
            : 1000000;

    internal bool IsAutoCompressionRunning => _isAutoCompressionRunning;

    internal bool CanStartAutoCompression =>
        !_isChangingWorkspaceRoot
        && !_isAutoCompressionRunning
        && SelectedAutoEncoder is not null
        && !string.IsNullOrWhiteSpace(AutoCompressionSourcePath)
        && !string.IsNullOrWhiteSpace(AutoCompressionOutputPath);

    internal bool CanCancelAutoCompression => _isAutoCompressionRunning && !_isAutoCompressionCancellationRequested;

    internal bool IsAutoCompressionAdvancedOptionsExpanded
    {
        get => _isAutoCompressionAdvancedOptionsExpanded;
        set => SetProperty(ref _isAutoCompressionAdvancedOptionsExpanded, value);
    }

    internal TaskPresentationState AutoCompressionPresentationState => _autoCompressionPresentationState;

    internal string AutoCompressionProgressLabel =>
        AutoCompressionProgressIsIndeterminate && _isAutoCompressionRunning
            ? Texts.AutoCompressionProgressActiveLabel
            : $"{AutoCompressionProgressPercent:0.#}%";

    internal string AutoCompressionProgressPercentText =>
        AutoCompressionProgressIsIndeterminate && _isAutoCompressionRunning && AutoCompressionProgressPercent <= 0
            ? "--"
            : $"{AutoCompressionProgressPercent:0.#}%";

    internal double AutoCompressionProgressValue => AutoCompressionProgressPercent / 100.0;

    internal Visibility AutoCompressionProgressHintVisibility =>
        _isAutoCompressionRunning && AutoCompressionProgressIsIndeterminate
            ? Visibility.Visible
            : Visibility.Collapsed;

    internal string AutoCompressionProgressHint => Texts.AutoCompressionProgressIndeterminateHint;

    internal Brush AutoCompressionStatusPanelBorderBrush => ResolveTaskStatusPanelBorderBrush(_autoCompressionDisplayState);

    internal Brush AutoCompressionProgressTrackBrush => ResolveAutoCompressionProgressTrackBrush(_autoCompressionDisplayState);

    internal Brush AutoCompressionProgressBorderBrush => ResolveAutoCompressionProgressBorderBrush(_autoCompressionDisplayState);

    internal Brush AutoCompressionProgressFillBrush => ResolveAutoCompressionProgressFillBrush(_autoCompressionDisplayState);

    internal string AutoCompressionSuggestedOutputExtension => "mkv";

    internal string AutoCompressionSuggestedOutputFileName
    {
        get
        {
            var outputPath = TryResolveAutoCompressionOutputPreviewPath();
            return string.IsNullOrWhiteSpace(outputPath)
                ? Texts.SuggestedOutputName
                : Path.GetFileNameWithoutExtension(outputPath);
        }
    }

    private void ApplyAutoCompressionLanguageState()
    {
        if (!_isAutoCompressionRunning)
        {
            SetAutoCompressionDisplayState(null);
            AutoCompressionStatusText = Texts.AutoCompressionIdleStatus;
        }

        OnPropertyChanged(nameof(AutoCompressionSuggestedOutputFileName));
        OnPropertyChanged(nameof(AutoCompressionOutputPreviewText));
        OnPropertyChanged(nameof(CanStartAutoCompression));
        OnPropertyChanged(nameof(CanCancelAutoCompression));
        OnPropertyChanged(nameof(AutoCompressionProgressLabel));
        OnPropertyChanged(nameof(AutoCompressionProgressHint));
        OnPropertyChanged(nameof(AutoCompressionProgressHintVisibility));
    }

    internal string? ValidateAutoCompressionForStart(out string? existingOutputPath)
    {
        existingOutputPath = null;

        try
        {
            var request = CreateAutoCompressionRequest(requireSourceExists: true);
            existingOutputPath = File.Exists(request.OutputPath) ? request.OutputPath : null;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    internal async Task<string?> StartAutoCompressionAsync()
    {
        if (_isChangingWorkspaceRoot)
        {
            return Texts.WorkspaceDirectoryChangeInProgressMessage;
        }

        if (_isAutoCompressionRunning)
        {
            return Texts.AutoCompressionAlreadyRunningError;
        }

        AutoCompressionResult result;
        string sourceFileName;

        try
        {
            var request = CreateAutoCompressionRequest(requireSourceExists: true);
            sourceFileName = Path.GetFileName(request.SourcePath);

            ResetAutoCompressionLogState();
            AutoCompressionLog = string.Empty;
            AutoCompressionProgressPercent = 0;
            AutoCompressionProgressIsIndeterminate = true;
            SetAutoCompressionDisplayState(EncodingJobState.Running);
            AutoCompressionCommandLine = _autoCompressionRunner.BuildDisplayCommand(request);
            AutoCompressionStatusText = Texts.AutoCompressionStartingStatus(sourceFileName);
            StatusText = Texts.AutoCompressionStartingStatus(sourceFileName);

            _autoCompressionCancellationTokenSource = new CancellationTokenSource();
            SetAutoCompressionRunningState(true, request.JobId);

            var progress = new Progress<AutoCompressionProgress>(ApplyAutoCompressionProgress);
            result = await _autoCompressionRunner.RunAsync(
                request,
                progress,
                _autoCompressionCancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (_autoCompressionCancellationTokenSource?.IsCancellationRequested == true)
        {
            SetAutoCompressionRunningState(false, null);
            DisposeAutoCompressionCancellation();
            SetAutoCompressionDisplayState(EncodingJobState.Cancelled);
            ClampAutoCompressionProgressForTerminalState(EncodingJobState.Cancelled);
            AutoCompressionStatusText = Texts.AutoCompressionCancelledStatus;
            StatusText = Texts.AutoCompressionCancelledStatus;
            return null;
        }
        catch (Exception ex)
        {
            SetAutoCompressionRunningState(false, null);
            DisposeAutoCompressionCancellation();
            ClampAutoCompressionProgressForTerminalState(EncodingJobState.Failed);
            SetAutoCompressionDisplayState(EncodingJobState.Failed);
            AppendAutoCompressionLogLine(ex.Message);
            AutoCompressionStatusText = Texts.AutoCompressionFailedStatus(ex.Message);
            StatusText = Texts.AutoCompressionFailedStatus(ex.Message);
            return ex.Message;
        }

        DisposeAutoCompressionCancellation();
        SetAutoCompressionRunningState(false, null);

        if (string.IsNullOrWhiteSpace(AutoCompressionLog))
        {
            AutoCompressionLog = result.Log;
        }

        switch (result.State)
        {
            case EncodingJobState.Completed:
                SetAutoCompressionDisplayState(EncodingJobState.Completed);
                ClampAutoCompressionProgressForTerminalState(EncodingJobState.Completed);
                AutoCompressionStatusText = Texts.AutoCompressionCompletedStatus;
                StatusText = Texts.AutoCompressionCompletedStatus;
                return null;

            case EncodingJobState.Cancelled:
                SetAutoCompressionDisplayState(EncodingJobState.Cancelled);
                ClampAutoCompressionProgressForTerminalState(EncodingJobState.Cancelled);
                AutoCompressionStatusText = Texts.AutoCompressionCancelledStatus;
                StatusText = Texts.AutoCompressionCancelledStatus;
                return null;

            default:
                SetAutoCompressionDisplayState(EncodingJobState.Failed);
                ClampAutoCompressionProgressForTerminalState(EncodingJobState.Failed);
                AppendAutoCompressionLogLine(result.Summary);
                AutoCompressionStatusText = Texts.AutoCompressionFailedStatus(result.Summary);
                StatusText = Texts.AutoCompressionFailedStatus(result.Summary);
                return result.Summary;
        }
    }

    internal void CancelAutoCompression()
    {
        if (!_isAutoCompressionRunning || _isAutoCompressionCancellationRequested)
        {
            return;
        }

        _isAutoCompressionCancellationRequested = true;
        OnPropertyChanged(nameof(CanCancelAutoCompression));
        SetAutoCompressionPresentationState(TaskPresentationState.Canceling);
        AutoCompressionStatusText = Texts.AutoCompressionCancellingStatus;
        StatusText = Texts.AutoCompressionCancellingStatus;

        _autoCompressionCancellationTokenSource?.Cancel();
        if (_activeAutoCompressionJobId is { } jobId)
        {
            _autoCompressionRunner.Abort(jobId);
        }
    }

    private AutoCompressionRequest CreateAutoCompressionRequest(bool requireSourceExists)
    {
        if (SelectedAutoEncoder is null)
        {
            throw new InvalidOperationException(Texts.AutoCompressionMissingEncoderError);
        }

        if (string.IsNullOrWhiteSpace(AutoCompressionSourcePath))
        {
            throw new InvalidOperationException(Texts.AutoCompressionMissingSourceError);
        }

        if (string.IsNullOrWhiteSpace(AutoCompressionOutputPath))
        {
            throw new InvalidOperationException(Texts.AutoCompressionMissingOutputError);
        }

        var normalizedSource = Path.GetFullPath(AutoCompressionSourcePath.Trim());
        var normalizedOutputDirectory = Path.GetFullPath(AutoCompressionOutputPath.Trim());

        if (requireSourceExists && !File.Exists(normalizedSource))
        {
            throw new FileNotFoundException(Texts.AutoCompressionSourceFileMissingError, normalizedSource);
        }

        if (requireSourceExists && File.Exists(normalizedOutputDirectory))
        {
            throw new InvalidOperationException(Texts.AutoCompressionOutputDirectoryInvalidError);
        }

        var normalizedOutput = ResolveAutoCompressionOutputPath(
            normalizedSource,
            normalizedOutputDirectory,
            SelectedAutoEncoder.Value,
            AutoCompressionMetric,
            AutoCompressionTargetVmaf);

        if (string.Equals(normalizedSource, normalizedOutput, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Texts.AutoCompressionSourceOutputPathConflictError);
        }

        var probes = Math.Max(1, (int)Math.Round(AutoCompressionProbes, MidpointRounding.AwayFromZero));
        var workers = AutoCompressionWorkers > 0
            ? (int?)Math.Round(AutoCompressionWorkers, MidpointRounding.AwayFromZero)
            : null;
        var searchProfile = BuildAutoCompressionSearchProfile();
        var request = new AutoCompressionRequest(
            Guid.NewGuid(),
            normalizedSource,
            normalizedOutput,
            SelectedAutoEncoder.Value,
            AutoCompressionMetric,
            AutoCompressionTargetVmaf,
            probes,
            AutoCompressionVideoParameters.Trim(),
            AutoCompressionBackendArguments.Trim(),
            workers);
        request = request with { SearchProfile = searchProfile };
        RequestValidation.ValidateAutoCompressionRequest(request);
        return request;
    }

    private AutoCompressionSearchProfile? BuildAutoCompressionSearchProfile()
    {
        var interpolationMethod = AutoCompressionInterpolationMethod.Trim();
        var probingStatistic = AutoCompressionProbingStatistic.Trim();
        var probeResolution = AutoCompressionProbeResolution.Trim();
        var probingRate = Math.Max(1, (int)Math.Round(AutoCompressionProbingRate, MidpointRounding.AwayFromZero));

        if (string.IsNullOrWhiteSpace(interpolationMethod)
            && string.Equals(probingStatistic, "auto", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(probeResolution)
            && probingRate == 1)
        {
            return null;
        }

        return new AutoCompressionSearchProfile(
            string.IsNullOrWhiteSpace(interpolationMethod) ? null : interpolationMethod,
            string.Equals(probingStatistic, "auto", StringComparison.OrdinalIgnoreCase) ? null : probingStatistic,
            string.IsNullOrWhiteSpace(probeResolution) ? null : probeResolution,
            probingRate,
            ScoutEnabled: false);
    }

    private bool TryCreateAutoCompressionRequest(
        bool requireSourceExists,
        out AutoCompressionRequest? request,
        out string? error)
    {
        try
        {
            request = CreateAutoCompressionRequest(requireSourceExists);
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

    private void RefreshAutoCompressionCommandPreview()
    {
        if (_isAutoCompressionRunning)
        {
            return;
        }

        AutoCompressionCommandLine = TryCreateAutoCompressionRequest(requireSourceExists: false, out var request, out _)
            ? _autoCompressionRunner.BuildDisplayCommand(request!)
            : string.Empty;
    }

    private static string ResolveAutoCompressionOutputPath(
        string sourcePath,
        string outputDirectory,
        EncoderKind encoderKind,
        AutoCompressionMetric metric,
        double targetQuality)
    {
        return AutoCompressionOutputPathPlanner.BuildOutputPath(
            sourcePath,
            outputDirectory,
            encoderKind,
            metric,
            targetQuality);
    }

    private string? TryResolveAutoCompressionOutputPreviewPath()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AutoCompressionSourcePath) || SelectedAutoEncoder is null)
            {
                return null;
            }

            var normalizedSource = Path.GetFullPath(AutoCompressionSourcePath.Trim());
            var outputDirectory = !string.IsNullOrWhiteSpace(AutoCompressionOutputPath)
                ? Path.GetFullPath(AutoCompressionOutputPath.Trim())
                : Path.GetDirectoryName(normalizedSource) ?? Environment.CurrentDirectory;
            return ResolveAutoCompressionOutputPath(
                normalizedSource,
                outputDirectory,
                SelectedAutoEncoder.Value,
                AutoCompressionMetric,
                AutoCompressionTargetVmaf);
        }
        catch
        {
            return null;
        }
    }

    private void ScheduleAutoCompressionInputRefresh()
    {
        CancelPendingAutoCompressionInputRefresh();
        var requestVersion = Interlocked.Increment(ref _autoCompressionInputRefreshVersion);
        var cancellationTokenSource = new CancellationTokenSource();
        _autoCompressionInputRefreshCancellationTokenSource = cancellationTokenSource;

        _ = RefreshAutoCompressionInputDeferredAsync(requestVersion, cancellationTokenSource.Token);
    }

    private async Task RefreshAutoCompressionInputDeferredAsync(int requestVersion, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InputPathRefreshDelay, cancellationToken);
            if (!IsAutoCompressionInputRefreshCurrent(requestVersion, cancellationToken))
            {
                return;
            }

            var hasPathState = !string.IsNullOrWhiteSpace(AutoCompressionSourcePath) || !string.IsNullOrWhiteSpace(AutoCompressionOutputPath);
            SetAutoCompressionInputRefreshPending(hasPathState);

            if (hasPathState && !_isAutoCompressionRunning)
            {
                AutoCompressionStatusText = Texts.AutoCompressionInputPreparingStatus;
                await Task.Yield();
                if (!IsAutoCompressionInputRefreshCurrent(requestVersion, cancellationToken))
                {
                    return;
                }
            }

            _isApplyingDeferredAutoCompressionInputRefresh = true;
            try
            {
                TryPopulateAutoCompressionOutputPathIfEmpty();
                RaiseAutoCompressionInputPropertyChanges();
                RefreshAutoCompressionCommandPreview();
            }
            finally
            {
                _isApplyingDeferredAutoCompressionInputRefresh = false;
            }

            if (!IsAutoCompressionInputRefreshCurrent(requestVersion, cancellationToken))
            {
                return;
            }

            SetAutoCompressionInputRefreshPending(false);
            if (!_isAutoCompressionRunning && string.Equals(AutoCompressionStatusText, Texts.AutoCompressionInputPreparingStatus, StringComparison.Ordinal))
            {
                AutoCompressionStatusText = Texts.AutoCompressionIdleStatus;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool IsAutoCompressionInputRefreshCurrent(int requestVersion, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && requestVersion == Volatile.Read(ref _autoCompressionInputRefreshVersion);
    }

    private void SetAutoCompressionInputRefreshPending(bool isPending)
    {
        if (_isAutoCompressionInputRefreshPending == isPending)
        {
            return;
        }

        _isAutoCompressionInputRefreshPending = isPending;
        OnPropertyChanged(nameof(AutoCompressionOutputPreviewText));
    }

    private void CancelPendingAutoCompressionInputRefresh()
    {
        _autoCompressionInputRefreshCancellationTokenSource?.Cancel();
        _autoCompressionInputRefreshCancellationTokenSource?.Dispose();
        _autoCompressionInputRefreshCancellationTokenSource = null;
    }

    private void TryPopulateAutoCompressionOutputPathIfEmpty()
    {
        if (string.IsNullOrWhiteSpace(AutoCompressionSourcePath))
        {
            return;
        }

        var sourceDirectory = Path.GetDirectoryName(AutoCompressionSourcePath);
        var suggestedPath = sourceDirectory ?? Environment.CurrentDirectory;

        if (!string.IsNullOrWhiteSpace(AutoCompressionOutputPath)
            && !string.Equals(AutoCompressionOutputPath, _lastAutoCompressionOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetAutoCompressionOutputPath(suggestedPath);
    }

    private void SetAutoCompressionOutputPath(string path)
    {
        _isUpdatingAutoCompressionOutputPath = true;

        try
        {
            AutoCompressionOutputPath = path;
            _lastAutoCompressionOutputPath = path;
        }
        finally
        {
            _isUpdatingAutoCompressionOutputPath = false;
        }
    }

    private void ApplyAutoCompressionProgress(AutoCompressionProgress progress)
    {
        if (_activeAutoCompressionJobId != progress.JobId)
        {
            return;
        }

        var isStaleRunningUpdate = _isAutoCompressionCancellationRequested && progress.State == EncodingJobState.Running;
        if (!isStaleRunningUpdate)
        {
            SetAutoCompressionDisplayState(progress.State);
        }

        if (progress.State == EncodingJobState.Completed)
        {
            ClampAutoCompressionProgressForTerminalState(EncodingJobState.Completed);
        }
        else if (progress.ProgressFraction.HasValue)
        {
            AutoCompressionProgressIsIndeterminate = false;
            AutoCompressionProgressPercent = progress.ProgressFraction.Value * 100;
        }
        else if (progress.State is EncodingJobState.Failed or EncodingJobState.Cancelled)
        {
            ClampAutoCompressionProgressForTerminalState(progress.State);
        }
        else if (_isAutoCompressionRunning)
        {
            AutoCompressionProgressIsIndeterminate = true;
        }

        if (!isStaleRunningUpdate && !string.IsNullOrWhiteSpace(progress.Summary))
        {
            AutoCompressionStatusText = progress.Summary;
        }

        AppendAutoCompressionLogLine(progress.DetailLine);
        RaiseDashboardCardActivityPropertyChanges();
    }

    private void AppendAutoCompressionLogLine(string line)
    {
        var normalized = ConsoleOutputLineNormalizer.Normalize(line);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (ToolLogLineClassifier.IsAutoCompressionTransientLine(normalized))
        {
            if (!string.Equals(_autoCompressionLiveLogLine, normalized, StringComparison.Ordinal))
            {
                _autoCompressionLiveLogLine = normalized;
                RefreshAutoCompressionLogText();
            }

            return;
        }

        _autoCompressionLiveLogLine = string.Empty;
        AppendAutoCompressionStageLogLine(normalized);
    }

    private void AppendAutoCompressionStageLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (_autoCompressionLogStageLines.Count > 0
            && string.Equals(_autoCompressionLogStageLines[^1], line, StringComparison.Ordinal))
        {
            RefreshAutoCompressionLogText();
            return;
        }

        _autoCompressionLogStageLines.Add(line);
        if (_autoCompressionLogStageLines.Count > AutoCompressionStageLogLimit)
        {
            _autoCompressionLogStageLines.RemoveAt(0);
        }

        RefreshAutoCompressionLogText();
    }

    private void RefreshAutoCompressionLogText()
    {
        var lines = new List<string>(_autoCompressionLogStageLines);
        if (!string.IsNullOrWhiteSpace(_autoCompressionLiveLogLine))
        {
            lines.Add(_autoCompressionLiveLogLine);
        }

        var joined = string.Join(Environment.NewLine, lines);
        if (joined.Length > AutoCompressionLogLimit)
        {
            joined = joined[^AutoCompressionLogLimit..];
        }

        AutoCompressionLog = joined;
    }

    private void ResetAutoCompressionLogState()
    {
        _autoCompressionLogStageLines.Clear();
        _autoCompressionLiveLogLine = string.Empty;
    }

    private void SetAutoCompressionRunningState(bool isRunning, Guid? activeJobId)
    {
        if (_isAutoCompressionRunning == isRunning && _activeAutoCompressionJobId == activeJobId)
        {
            return;
        }

        _isAutoCompressionRunning = isRunning;
        _activeAutoCompressionJobId = activeJobId;
        _isAutoCompressionCancellationRequested = false;
        OnPropertyChanged(nameof(IsAutoCompressionRunning));
        OnPropertyChanged(nameof(HasRunningAppWork));
        OnPropertyChanged(nameof(CanStartAutoCompression));
        OnPropertyChanged(nameof(CanCancelAutoCompression));
        OnPropertyChanged(nameof(AutoCompressionProgressLabel));
        OnPropertyChanged(nameof(AutoCompressionProgressPercentText));
        OnPropertyChanged(nameof(AutoCompressionProgressValue));
        OnPropertyChanged(nameof(AutoCompressionProgressHintVisibility));
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

    private void SetAutoCompressionDisplayState(EncodingJobState? state)
    {
        if (_autoCompressionDisplayState == state)
        {
            return;
        }

        _autoCompressionDisplayState = state;
        SetAutoCompressionPresentationState(state switch
        {
            EncodingJobState.Running => TaskPresentationState.Running,
            EncodingJobState.Completed => TaskPresentationState.Completed,
            EncodingJobState.Failed => TaskPresentationState.Failed,
            EncodingJobState.Cancelled => TaskPresentationState.Cancelled,
            _ => TaskPresentationState.Idle
        });
        OnPropertyChanged(nameof(AutoCompressionStatusPanelBorderBrush));
        OnPropertyChanged(nameof(AutoCompressionProgressTrackBrush));
        OnPropertyChanged(nameof(AutoCompressionProgressBorderBrush));
        OnPropertyChanged(nameof(AutoCompressionProgressFillBrush));
        RaiseDashboardCardActivityPropertyChanges();
    }

    private void SetAutoCompressionPresentationState(TaskPresentationState state)
    {
        if (_autoCompressionPresentationState == state)
        {
            return;
        }

        _autoCompressionPresentationState = state;
        OnPropertyChanged(nameof(AutoCompressionPresentationState));
    }

    private void ClampAutoCompressionProgressForTerminalState(EncodingJobState state)
    {
        AutoCompressionProgressIsIndeterminate = false;
        AutoCompressionProgressPercent = state == EncodingJobState.Completed
            ? 100
            : Math.Min(AutoCompressionProgressPercent, 99.9);
    }

    private void DisposeAutoCompressionCancellation()
    {
        _autoCompressionCancellationTokenSource?.Dispose();
        _autoCompressionCancellationTokenSource = null;
    }

    private void RaiseAutoCompressionInputPropertyChanges()
    {
        OnPropertyChanged(nameof(CanStartAutoCompression));
        OnPropertyChanged(nameof(AutoCompressionSuggestedOutputFileName));
        OnPropertyChanged(nameof(AutoCompressionOutputPreviewText));
    }

    private static Brush ResolveAutoCompressionProgressTrackBrush(EncodingJobState? state)
    {
        return state switch
        {
            EncodingJobState.Failed => ResolveBrush("AppErrorSoftBrush"),
            EncodingJobState.Cancelled => ResolveBrush("AppNeutralSoftBrush"),
            _ => ResolveBrush("QueueProgressSoftBrush")
        };
    }

    private static Brush ResolveAutoCompressionProgressBorderBrush(EncodingJobState? state)
    {
        return state switch
        {
            EncodingJobState.Failed => ResolveBrush("AppErrorBrush"),
            EncodingJobState.Cancelled => ResolveBrush("AppNeutralBrush"),
            _ => ResolveBrush("QueueProgressFillBrush")
        };
    }

    private static Brush ResolveAutoCompressionProgressFillBrush(EncodingJobState? state)
    {
        return state switch
        {
            EncodingJobState.Failed => ResolveBrush("AppErrorBrush"),
            EncodingJobState.Cancelled => ResolveBrush("AppNeutralBrush"),
            _ => ResolveBrush("QueueProgressAreaBrush")
        };
    }

    private static double NormalizeBoundedDouble(double value, double minimum, double maximum)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? minimum
            : Math.Clamp(value, minimum, maximum);
    }
}
