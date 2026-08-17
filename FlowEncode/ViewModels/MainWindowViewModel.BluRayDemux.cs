using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
    private const int BluRayDemuxLogLimit = 160_000;
    private const int BluRayDemuxStageLogLimit = 400;

    private string _bluRayDemuxSourcePath = string.Empty;
    private string _bluRayDemuxOutputPath = string.Empty;
    private BluRayDemuxBackendOption? _selectedBluRayDemuxBackend;
    private BluRayPlaylistItem? _selectedBluRayPlaylist;
    private string _bluRayDemuxStatusText = string.Empty;
    private string _bluRayDemuxCommandLine = string.Empty;
    private string _bluRayDemuxLog = string.Empty;
    private string _bluRayDiscSummaryText = string.Empty;
    private string _bluRayPlaylistSummaryText = string.Empty;
    private double _bluRayDemuxProgressPercent;
    private bool _bluRayDemuxProgressIsIndeterminate;
    private bool _isBluRayDiscScanning;
    private bool _isBluRayPlaylistLoading;
    private bool _isBluRayDemuxRunning;
    private bool _isUpdatingBluRayOutputPath;
    private bool _isBulkUpdatingBluRayTrackSelection;
    private string? _lastBluRayOutputPath;
    private Guid? _activeBluRayDemuxJobId;
    private EncodingJobState? _bluRayDemuxDisplayState;
    private TaskPresentationState _bluRayDemuxPresentationState;
    private bool _isBluRayDemuxCancellationRequested;
    private CancellationTokenSource? _bluRayProbeCancellationTokenSource;
    private CancellationTokenSource? _bluRayDemuxCancellationTokenSource;
    private CancellationTokenSource? _bluRayDemuxInputRefreshCancellationTokenSource;
    private int _bluRayPlaylistLoadVersion;
    private int _bluRayDemuxInputRefreshVersion;
    private bool _isApplyingDeferredBluRayDemuxInputRefresh;
    private bool _isBluRayDemuxInputRefreshPending;
    private bool _isApplyingBluRayDemuxLanguageState;
    private readonly StringBuilder _bluRayDemuxLogBuilder = new();
    private readonly List<string> _bluRayDemuxLogStageLines = [];
    private readonly Dictionary<string, BluRayPlaylistCacheEntry> _bluRayPlaylistTrackCache = new(StringComparer.OrdinalIgnoreCase);
    private string _bluRayDemuxLastLogLine = string.Empty;
    private string _bluRayDemuxLiveLogLine = string.Empty;
    private string _bluRayDemuxLogPhaseMarker = string.Empty;

    internal ObservableCollection<BluRayDemuxBackendOption> BluRayDemuxBackendOptions { get; } = [];
    internal ObservableCollection<BluRayPlaylistItem> BluRayPlaylists { get; } = [];
    internal ObservableCollection<BluRayTrackItemViewModel> BluRayTracks { get; } = [];

    internal string BluRayDemuxSourcePath
    {
        get => _bluRayDemuxSourcePath;
        set
        {
            if (SetProperty(ref _bluRayDemuxSourcePath, value))
            {
                ScheduleBluRayDemuxInputRefresh(resetScanState: true);
            }
        }
    }

    internal string BluRayDemuxOutputPath
    {
        get => _bluRayDemuxOutputPath;
        set
        {
            if (SetProperty(ref _bluRayDemuxOutputPath, value))
            {
                if (!_isUpdatingBluRayOutputPath)
                {
                    _lastBluRayOutputPath = null;
                }

                if (_isApplyingDeferredBluRayDemuxInputRefresh)
                {
                    return;
                }

                ScheduleBluRayDemuxInputRefresh(resetScanState: false);
            }
        }
    }

    internal BluRayDemuxBackendOption? SelectedBluRayDemuxBackend
    {
        get => _selectedBluRayDemuxBackend;
        set
        {
            if (SetProperty(ref _selectedBluRayDemuxBackend, value))
            {
                if (_isApplyingBluRayDemuxLanguageState)
                {
                    return;
                }

                RaiseBluRayDemuxEnvironmentPropertyChanges();
                ScheduleBluRayDemuxInputRefresh(resetScanState: true);
            }
        }
    }

    internal BluRayPlaylistItem? SelectedBluRayPlaylist
    {
        get => _selectedBluRayPlaylist;
        set
        {
            if (SetProperty(ref _selectedBluRayPlaylist, value))
            {
                if (!TryRestoreCachedBluRayPlaylistState(value, updateStatus: !_isBluRayDemuxRunning))
                {
                    ReplaceBluRayTrackItems([]);
                    _bluRayPlaylistSummaryText = string.Empty;
                }

                OnPropertyChanged(nameof(BluRayPlaylistSummaryText));
                RefreshBluRayTrackOutputPreviews();
                RaiseBluRayDemuxInputPropertyChanges();
                RefreshBluRayDemuxCommandPreview();
            }
        }
    }

    internal string BluRayDemuxStatusText
    {
        get => _bluRayDemuxStatusText;
        set
        {
            if (SetProperty(ref _bluRayDemuxStatusText, value))
            {
                OnPropertyChanged(nameof(CanClearBluRayDemuxTask));
                OnPropertyChanged(nameof(BluRayDemuxProgressSecondaryText));
                RaiseDashboardCardActivityPropertyChanges();
            }
        }
    }

    internal string BluRayDemuxCommandLine
    {
        get => _bluRayDemuxCommandLine;
        set
        {
            if (SetProperty(ref _bluRayDemuxCommandLine, value))
            {
                OnPropertyChanged(nameof(CanClearBluRayDemuxTask));
            }
        }
    }

    internal string BluRayDemuxLog
    {
        get => _bluRayDemuxLog;
        set
        {
            if (SetProperty(ref _bluRayDemuxLog, value))
            {
                OnPropertyChanged(nameof(CanClearBluRayDemuxTask));
            }
        }
    }

    internal double BluRayDemuxProgressPercent
    {
        get => _bluRayDemuxProgressPercent;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _bluRayDemuxProgressPercent, normalized))
            {
                OnPropertyChanged(nameof(BluRayDemuxProgressValue));
                OnPropertyChanged(nameof(BluRayDemuxProgressPercentText));
                OnPropertyChanged(nameof(BluRayDemuxProgressLabel));
            }
        }
    }

    internal bool BluRayDemuxProgressIsIndeterminate
    {
        get => _bluRayDemuxProgressIsIndeterminate;
        set
        {
            if (SetProperty(ref _bluRayDemuxProgressIsIndeterminate, value))
            {
                OnPropertyChanged(nameof(BluRayDemuxProgressPercentText));
                OnPropertyChanged(nameof(BluRayDemuxProgressLabel));
            }
        }
    }

    internal string BluRayDiscSummaryText => string.IsNullOrWhiteSpace(_bluRayDiscSummaryText) ? Texts.BluRayDiscSummaryPlaceholder : _bluRayDiscSummaryText;
    internal string BluRayPlaylistSummaryText => string.IsNullOrWhiteSpace(_bluRayPlaylistSummaryText) ? Texts.BluRayPlaylistSummaryPlaceholder : _bluRayPlaylistSummaryText;
    internal string BluRaySelectedTrackSummary => Texts.BluRayTrackSelectionSummary(BluRayTracks.Count(static track => track.IsSelected), BluRayTracks.Count);
    internal bool IsBluRayDiscScanning => _isBluRayDiscScanning;
    internal Visibility BluRayDiscScanProgressVisibility => _isBluRayDiscScanning ? Visibility.Visible : Visibility.Collapsed;
    internal bool IsBluRayPlaylistLoading => _isBluRayPlaylistLoading;
    internal bool IsBluRayDemuxRunning => _isBluRayDemuxRunning;
    internal bool CanScanBluRayDisc => !_isChangingWorkspaceRoot && !_isBluRayDiscScanning && !_isBluRayPlaylistLoading && !_isBluRayDemuxRunning && SelectedBluRayDemuxBackend is not null && !string.IsNullOrWhiteSpace(BluRayDemuxSourcePath) && GetSelectedBluRayToolState() == ReadinessState.Ready;
    internal bool CanStartBluRayDemux => !_isChangingWorkspaceRoot && !_isBluRayDiscScanning && !_isBluRayPlaylistLoading && !_isBluRayDemuxRunning && SelectedBluRayDemuxBackend is not null && SelectedBluRayPlaylist is not null && BluRayTracks.Any(static track => track.IsSelected) && !string.IsNullOrWhiteSpace(BluRayDemuxSourcePath) && !string.IsNullOrWhiteSpace(BluRayDemuxOutputPath) && GetSelectedBluRayToolState() == ReadinessState.Ready;
    internal bool CanCancelBluRayDemux => _isBluRayDemuxRunning && !_isBluRayDemuxCancellationRequested;

    internal TaskPresentationState BluRayDemuxPresentationState => _bluRayDemuxPresentationState;
    internal bool CanClearBluRayDemuxTask => !_isBluRayDemuxRunning && (!string.IsNullOrWhiteSpace(BluRayDemuxSourcePath) || !string.IsNullOrWhiteSpace(BluRayDemuxOutputPath) || !string.IsNullOrWhiteSpace(BluRayDemuxCommandLine) || !string.IsNullOrWhiteSpace(BluRayDemuxLog) || BluRayPlaylists.Count > 0 || BluRayTracks.Count > 0 || !string.Equals(BluRayDemuxStatusText, Texts.BluRayDemuxIdleStatus, StringComparison.Ordinal));
    internal bool CanSelectAllBluRayTracks => BluRayTracks.Count > 0;
    internal bool CanInvertBluRayTrackSelection => BluRayTracks.Count > 0;
    internal string BluRayDemuxProgressLabel => BluRayDemuxProgressIsIndeterminate && _isBluRayDemuxRunning ? Texts.BluRayDemuxProgressActiveLabel : $"{BluRayDemuxProgressPercent:0.#}%";
    internal double BluRayDemuxProgressValue => BluRayDemuxProgressPercent / 100.0;
    internal string BluRayDemuxProgressPercentText => BluRayDemuxProgressIsIndeterminate && _isBluRayDemuxRunning && BluRayDemuxProgressPercent <= 0 ? "--" : $"{BluRayDemuxProgressPercent:0.#}%";
    internal string BluRayDemuxProgressSecondaryText => !string.IsNullOrWhiteSpace(_bluRayDemuxLastLogLine) ? _bluRayDemuxLastLogLine : BluRayDemuxStatusText;
    internal Visibility BluRayDemuxProgressSecondaryVisibility => string.IsNullOrWhiteSpace(_bluRayDemuxLastLogLine) ? Visibility.Collapsed : Visibility.Visible;
    internal Brush BluRayDemuxStatusPanelBorderBrush => ResolveTaskStatusPanelBorderBrush(_bluRayDemuxDisplayState);
    internal Brush BluRayDemuxProgressTrackBrush => ResolveBluRayDemuxProgressTrackBrush(_bluRayDemuxDisplayState);
    internal Brush BluRayDemuxProgressBorderBrush => ResolveBluRayDemuxProgressBorderBrush(_bluRayDemuxDisplayState);
    internal Brush BluRayDemuxProgressFillBrush => ResolveBluRayDemuxProgressFillBrush(_bluRayDemuxDisplayState);
    internal string BluRayDemuxOutputPreviewText => _isBluRayDemuxInputRefreshPending
        ? Texts.OutputPreviewUpdating
        : BuildOutputPreviewText(TryResolveBluRayOutputPreviewPath());
    internal string BluRayDemuxBackendNote => Texts.BluRayBackendNote(SelectedBluRayDemuxBackend?.Value ?? BluRayDemuxBackend.DgDemux);

    internal string BluRayToolSummary
    {
        get
        {
            var backend = SelectedBluRayDemuxBackend?.Value;
            if (!backend.HasValue)
            {
                return Texts.BluRayToolPreparing;
            }

            var backendLabel = Texts.BluRayBackendLabel(backend.Value);
            var tool = GetSelectedBluRayToolProbeResult();
            if (tool is null)
            {
                return Texts.BluRayToolPreparing;
            }

            var detail = tool.State switch
            {
                ReadinessState.Ready => BuildToolProbeDetail(tool),
                ReadinessState.Missing => Texts.ToolMissingDetail(tool.DisplayName),
                ReadinessState.Unknown => Texts.ToolUnknownDetail(tool.DisplayName),
                _ => string.IsNullOrWhiteSpace(tool.FailureReason) ? BuildToolProbeDetail(tool) : tool.FailureReason
            };

            return tool.State == ReadinessState.Ready
                ? Texts.BluRayToolReadySummary(backendLabel, detail)
                : Texts.BluRayToolUnavailableSummary(backendLabel, detail);
        }
    }

    internal string? ValidateBluRayDemuxForStart() => TryCreateBluRayDemuxRequest(requireSourceExists: true, out _, out var error) ? null : error;

    internal async Task ScanBluRayDiscAsync()
    {
        if (_isChangingWorkspaceRoot || _isBluRayDiscScanning || _isBluRayPlaylistLoading || _isBluRayDemuxRunning)
        {
            return;
        }

        var backend = SelectedBluRayDemuxBackend?.Value ?? BluRayDemuxBackend.DgDemux;
        var backendLabel = Texts.BluRayBackendLabel(backend);
        var cancellationTokenSource = RenewBluRayProbeCancellation();
        SetBluRayDiscScanningState(true);
        _bluRayPlaylistTrackCache.Clear();
        ReplaceItems(BluRayPlaylists, []);
        SelectedBluRayPlaylist = null;
        ReplaceBluRayTrackItems([]);
        _bluRayDiscSummaryText = string.Empty;
        _bluRayPlaylistSummaryText = string.Empty;
        BluRayDemuxCommandLine = string.Empty;
        BluRayDemuxStatusText = Texts.BluRayDiscScanStatus(backendLabel);
        StatusText = BluRayDemuxStatusText;

        try
        {
            var playlists = await _bluRayDiscProbeService.ScanDiscAsync(backend, NormalizeBluRayDiscRoot(BluRayDemuxSourcePath, requireExists: true), cancellationTokenSource.Token);
            ReplaceItems(BluRayPlaylists, playlists);
            _bluRayDiscSummaryText = Texts.BluRayDiscScanCompletedStatus(backendLabel, playlists.Count);
            BluRayDemuxStatusText = _bluRayDiscSummaryText;
            StatusText = BluRayDemuxStatusText;
            OnPropertyChanged(nameof(BluRayDiscSummaryText));
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _bluRayDiscSummaryText = ex.Message;
            BluRayDemuxStatusText = Texts.BluRayDiscScanFailedStatus(ex.Message);
            StatusText = BluRayDemuxStatusText;
            OnPropertyChanged(nameof(BluRayDiscSummaryText));
        }
        finally
        {
            SetBluRayDiscScanningState(false);
            RaiseBluRayDemuxInputPropertyChanges();
        }
    }

    internal async Task LoadSelectedBluRayPlaylistAsync()
    {
        if (_isChangingWorkspaceRoot || _isBluRayDiscScanning || SelectedBluRayPlaylist is null)
        {
            return;
        }

        if (TryRestoreCachedBluRayPlaylistState(SelectedBluRayPlaylist, updateStatus: !_isBluRayDemuxRunning))
        {
            RefreshBluRayTrackOutputPreviews();
            RaiseBluRayDemuxInputPropertyChanges();
            RefreshBluRayDemuxCommandPreview();
            return;
        }

        if (_isBluRayDemuxRunning)
        {
            return;
        }

        var requestVersion = Interlocked.Increment(ref _bluRayPlaylistLoadVersion);
        var selectedPlaylist = SelectedBluRayPlaylist;
        var cancellationTokenSource = RenewBluRayProbeCancellation();
        SetBluRayPlaylistLoadingState(true);
        ReplaceBluRayTrackItems([]);
        _bluRayPlaylistSummaryText = string.Empty;
        BluRayDemuxStatusText = Texts.BluRayPlaylistLoadStatus(selectedPlaylist.DisplayName);
        StatusText = BluRayDemuxStatusText;

        try
        {
            var result = await _bluRayDiscProbeService.ScanPlaylistAsync(SelectedBluRayDemuxBackend?.Value ?? BluRayDemuxBackend.DgDemux, NormalizeBluRayDiscRoot(BluRayDemuxSourcePath, requireExists: true), selectedPlaylist, cancellationTokenSource.Token);
            if (requestVersion != Volatile.Read(ref _bluRayPlaylistLoadVersion) || !ReferenceEquals(selectedPlaylist, SelectedBluRayPlaylist))
            {
                return;
            }

            var trackItems = result.Tracks.Select(static track => new BluRayTrackItemViewModel(track)).ToList();
            StoreBluRayPlaylistCache(selectedPlaylist, result.Summary, trackItems);
            ReplaceBluRayTrackItems(trackItems);
            _bluRayPlaylistSummaryText = result.Summary;
            BluRayDemuxStatusText = Texts.BluRayPlaylistLoadedStatus(selectedPlaylist.DisplayName, result.Tracks.Count);
            StatusText = BluRayDemuxStatusText;
            RefreshBluRayTrackOutputPreviews();
            RaiseBluRayDemuxInputPropertyChanges();
            RefreshBluRayDemuxCommandPreview();
            OnPropertyChanged(nameof(BluRayPlaylistSummaryText));
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _bluRayPlaylistSummaryText = ex.Message;
            BluRayDemuxStatusText = Texts.BluRayPlaylistLoadFailedStatus(ex.Message);
            StatusText = BluRayDemuxStatusText;
            OnPropertyChanged(nameof(BluRayPlaylistSummaryText));
        }
        finally
        {
            SetBluRayPlaylistLoadingState(false);
        }
    }

    internal async Task<string?> StartBluRayDemuxAsync()
    {
        if (_isChangingWorkspaceRoot)
        {
            return Texts.WorkspaceDirectoryChangeInProgressMessage;
        }

        if (_isBluRayDemuxRunning)
        {
            return Texts.BluRayDemuxAlreadyRunningError;
        }

        BluRayDemuxRequest request;
        BluRayDemuxResult result;
        var backendLabel = Texts.BluRayBackendLabel(SelectedBluRayDemuxBackend?.Value ?? BluRayDemuxBackend.DgDemux);

        try
        {
            request = CreateBluRayDemuxRequest(requireSourceExists: true);
            ResetBluRayDemuxLogState();
            BluRayDemuxProgressPercent = 0;
            BluRayDemuxProgressIsIndeterminate = true;
            SetBluRayDemuxDisplayState(EncodingJobState.Running);
            BluRayDemuxCommandLine = _bluRayDemuxRunner.BuildDisplayCommand(request);
            BluRayDemuxStatusText = Texts.BluRayDemuxStartingStatus(backendLabel, request.Playlist.DisplayName);
            StatusText = BluRayDemuxStatusText;
            _bluRayDemuxCancellationTokenSource = new CancellationTokenSource();
            SetBluRayDemuxRunningState(true, request.JobId);
            result = await _bluRayDemuxRunner.RunAsync(request, new Progress<BluRayDemuxProgress>(ApplyBluRayDemuxProgress), _bluRayDemuxCancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (_bluRayDemuxCancellationTokenSource?.IsCancellationRequested == true)
        {
            SetBluRayDemuxRunningState(false, null);
            DisposeBluRayDemuxCancellation();
            SetBluRayDemuxDisplayState(EncodingJobState.Cancelled);
            ClampBluRayDemuxProgressForTerminalState(EncodingJobState.Cancelled);
            BluRayDemuxStatusText = Texts.BluRayDemuxCancelledStatus(backendLabel);
            StatusText = BluRayDemuxStatusText;
            return null;
        }
        catch (Exception ex)
        {
            SetBluRayDemuxRunningState(false, null);
            DisposeBluRayDemuxCancellation();
            ClampBluRayDemuxProgressForTerminalState(EncodingJobState.Failed);
            SetBluRayDemuxDisplayState(EncodingJobState.Failed);
            AppendBluRayDemuxLogLine(ex.Message);
            BluRayDemuxStatusText = Texts.BluRayDemuxFailedStatus(ex.Message);
            StatusText = BluRayDemuxStatusText;
            return ex.Message;
        }

        DisposeBluRayDemuxCancellation();
        SetBluRayDemuxRunningState(false, null);
        if (string.IsNullOrWhiteSpace(BluRayDemuxLog) && !string.IsNullOrWhiteSpace(result.Log))
        {
            BluRayDemuxLog = result.Log;
        }

        switch (result.State)
        {
            case EncodingJobState.Completed:
                SetBluRayDemuxDisplayState(EncodingJobState.Completed);
                ClampBluRayDemuxProgressForTerminalState(EncodingJobState.Completed);
                BluRayDemuxStatusText = Texts.BluRayDemuxCompletedStatus(backendLabel);
                StatusText = BluRayDemuxStatusText;
                return null;
            case EncodingJobState.Cancelled:
                SetBluRayDemuxDisplayState(EncodingJobState.Cancelled);
                ClampBluRayDemuxProgressForTerminalState(EncodingJobState.Cancelled);
                BluRayDemuxStatusText = Texts.BluRayDemuxCancelledStatus(backendLabel);
                StatusText = BluRayDemuxStatusText;
                return null;
            default:
                SetBluRayDemuxDisplayState(EncodingJobState.Failed);
                ClampBluRayDemuxProgressForTerminalState(EncodingJobState.Failed);
                BluRayDemuxStatusText = Texts.BluRayDemuxFailedStatus(result.Summary);
                StatusText = BluRayDemuxStatusText;
                return result.Summary;
        }
    }

    internal void CancelBluRayDemux()
    {
        if (!_isBluRayDemuxRunning || _isBluRayDemuxCancellationRequested)
        {
            return;
        }

        _isBluRayDemuxCancellationRequested = true;
        OnPropertyChanged(nameof(CanCancelBluRayDemux));
        SetBluRayDemuxPresentationState(TaskPresentationState.Canceling);
        var backendLabel = Texts.BluRayBackendLabel(SelectedBluRayDemuxBackend?.Value ?? BluRayDemuxBackend.DgDemux);
        BluRayDemuxStatusText = Texts.BluRayDemuxCancellingStatus(backendLabel);
        StatusText = BluRayDemuxStatusText;
        _bluRayDemuxCancellationTokenSource?.Cancel();
        if (_activeBluRayDemuxJobId is { } jobId)
        {
            _bluRayDemuxRunner.Abort(jobId, SelectedBluRayDemuxBackend?.Value ?? BluRayDemuxBackend.DgDemux);
        }
    }

    internal void SelectAllBluRayTracks()
    {
        UpdateBluRayTrackSelection(static _ => true);
    }

    internal void InvertBluRayTrackSelection()
    {
        UpdateBluRayTrackSelection(static track => !track.IsSelected);
    }

    internal void ToggleBluRayTrackSelection(BluRayTrackItemViewModel? track)
    {
        if (track is null)
        {
            return;
        }

        track.IsSelected = !track.IsSelected;
    }

    internal void ClearBluRayDemuxTask()
    {
        if (_isBluRayDemuxRunning)
        {
            return;
        }

        DisposeBluRayProbeCancellation();
        DisposeBluRayDemuxCancellation();
        Interlocked.Increment(ref _bluRayPlaylistLoadVersion);
        ReplaceItems(BluRayPlaylists, []);
        ReplaceBluRayTrackItems([]);
        _bluRayPlaylistTrackCache.Clear();
        _selectedBluRayPlaylist = null;
        _lastBluRayOutputPath = null;
        _bluRayDiscSummaryText = string.Empty;
        _bluRayPlaylistSummaryText = string.Empty;
        _bluRayDemuxStatusText = Texts.BluRayDemuxIdleStatus;
        _bluRayDemuxCommandLine = string.Empty;
        ResetBluRayDemuxLogState();
        _bluRayDemuxProgressPercent = 0;
        _bluRayDemuxProgressIsIndeterminate = false;
        _bluRayDemuxDisplayState = null;
        SetBluRayDemuxRunningState(false, null);

        OnPropertyChanged(nameof(SelectedBluRayPlaylist));
        OnPropertyChanged(nameof(BluRayDiscSummaryText));
        OnPropertyChanged(nameof(BluRayPlaylistSummaryText));
        OnPropertyChanged(nameof(BluRaySelectedTrackSummary));
        OnPropertyChanged(nameof(BluRayDemuxProgressPercentText));
        OnPropertyChanged(nameof(BluRayDemuxProgressLabel));
        OnPropertyChanged(nameof(BluRayDemuxProgressSecondaryText));
        OnPropertyChanged(nameof(BluRayDemuxProgressSecondaryVisibility));
        OnPropertyChanged(nameof(BluRayDemuxStatusPanelBorderBrush));
        OnPropertyChanged(nameof(BluRayDemuxProgressTrackBrush));
        OnPropertyChanged(nameof(BluRayDemuxProgressBorderBrush));
        OnPropertyChanged(nameof(BluRayDemuxProgressFillBrush));
        RaiseDashboardCardActivityPropertyChanges();

        BluRayDemuxSourcePath = string.Empty;
        BluRayDemuxOutputPath = string.Empty;
        BluRayDemuxStatusText = Texts.BluRayTaskClearedStatus;
        StatusText = BluRayDemuxStatusText;
    }

    partial void InitializeBluRayDemuxState()
    {
        BluRayDemuxModule.InitializeState();
    }

    partial void DisposeBluRayDemuxState()
    {
        CancelPendingBluRayDemuxInputRefresh();
        DisposeBluRayProbeCancellation();
        CancelBluRayDemux();
        DisposeBluRayDemuxCancellation();
        _bluRayPlaylistTrackCache.Clear();
        ReplaceBluRayTrackItems([]);
    }

    partial void HandleBluRayEnvironmentReadinessApplied()
    {
        BluRayDemuxModule.HandleEnvironmentReadinessApplied();
    }

    partial void ApplyBluRayDemuxLanguageState()
    {
        BluRayDemuxModule.ApplyLanguageState();
    }

    private void ScheduleBluRayDemuxInputRefresh(bool resetScanState)
    {
        CancelPendingBluRayDemuxInputRefresh();
        var requestVersion = Interlocked.Increment(ref _bluRayDemuxInputRefreshVersion);
        var cancellationTokenSource = new CancellationTokenSource();
        _bluRayDemuxInputRefreshCancellationTokenSource = cancellationTokenSource;

        _ = RefreshBluRayDemuxInputDeferredAsync(requestVersion, resetScanState, cancellationTokenSource.Token);
    }

    private async Task RefreshBluRayDemuxInputDeferredAsync(
        int requestVersion,
        bool resetScanState,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InputPathRefreshDelay, cancellationToken);
            if (!IsBluRayDemuxInputRefreshCurrent(requestVersion, cancellationToken))
            {
                return;
            }

            var hasPathState = !string.IsNullOrWhiteSpace(BluRayDemuxSourcePath) || !string.IsNullOrWhiteSpace(BluRayDemuxOutputPath);
            SetBluRayDemuxInputRefreshPending(hasPathState);

            if (hasPathState && !_isBluRayDemuxRunning)
            {
                BluRayDemuxStatusText = Texts.BluRayDemuxInputPreparingStatus;
                await Task.Yield();
                if (!IsBluRayDemuxInputRefreshCurrent(requestVersion, cancellationToken))
                {
                    return;
                }
            }

            _isApplyingDeferredBluRayDemuxInputRefresh = true;
            try
            {
                if (resetScanState)
                {
                    ResetBluRayScanState(clearStatus: false);
                }

                TryPopulateBluRayOutputPathIfEmpty();
                RefreshBluRayTrackOutputPreviews();
                RaiseBluRayDemuxInputPropertyChanges();
                RefreshBluRayDemuxCommandPreview();
            }
            finally
            {
                _isApplyingDeferredBluRayDemuxInputRefresh = false;
            }

            if (!IsBluRayDemuxInputRefreshCurrent(requestVersion, cancellationToken))
            {
                return;
            }

            SetBluRayDemuxInputRefreshPending(false);
            if (!_isBluRayDemuxRunning && string.Equals(BluRayDemuxStatusText, Texts.BluRayDemuxInputPreparingStatus, StringComparison.Ordinal))
            {
                BluRayDemuxStatusText = string.IsNullOrWhiteSpace(BluRayDemuxSourcePath)
                    ? Texts.BluRayDemuxIdleStatus
                    : Texts.BluRayDemuxSourceReadyStatus;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool IsBluRayDemuxInputRefreshCurrent(int requestVersion, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && requestVersion == Volatile.Read(ref _bluRayDemuxInputRefreshVersion);
    }

    private void SetBluRayDemuxInputRefreshPending(bool isPending)
    {
        if (_isBluRayDemuxInputRefreshPending == isPending)
        {
            return;
        }

        _isBluRayDemuxInputRefreshPending = isPending;
        OnPropertyChanged(nameof(BluRayDemuxOutputPreviewText));
    }

    private void ResetBluRayScanState(bool clearStatus)
    {
        DisposeBluRayProbeCancellation();
        Interlocked.Increment(ref _bluRayPlaylistLoadVersion);
        ReplaceItems(BluRayPlaylists, []);
        ReplaceBluRayTrackItems([]);
        _bluRayPlaylistTrackCache.Clear();
        _selectedBluRayPlaylist = null;
        _bluRayDiscSummaryText = string.Empty;
        _bluRayPlaylistSummaryText = string.Empty;
        BluRayDemuxCommandLine = string.Empty;

        OnPropertyChanged(nameof(SelectedBluRayPlaylist));
        OnPropertyChanged(nameof(BluRayDiscSummaryText));
        OnPropertyChanged(nameof(BluRayPlaylistSummaryText));
        OnPropertyChanged(nameof(BluRaySelectedTrackSummary));

        if (clearStatus && !_isBluRayDemuxRunning)
        {
            BluRayDemuxStatusText = Texts.BluRayDemuxIdleStatus;
        }
    }

    private CancellationTokenSource RenewBluRayProbeCancellation()
    {
        DisposeBluRayProbeCancellation();
        _bluRayProbeCancellationTokenSource = new CancellationTokenSource();
        return _bluRayProbeCancellationTokenSource;
    }

    private void ReplaceBluRayTrackItems(IEnumerable<BluRayTrackItemViewModel> source)
    {
        foreach (var track in BluRayTracks)
        {
            track.PropertyChanged -= BluRayTrackItem_PropertyChanged;
        }

        ReplaceItems(BluRayTracks, source);

        foreach (var track in BluRayTracks)
        {
            track.PropertyChanged += BluRayTrackItem_PropertyChanged;
        }

        OnPropertyChanged(nameof(BluRaySelectedTrackSummary));
        OnPropertyChanged(nameof(CanSelectAllBluRayTracks));
        OnPropertyChanged(nameof(CanInvertBluRayTrackSelection));
    }

    private void BluRayTrackItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BluRayTrackItemViewModel.IsSelected))
        {
            return;
        }

        if (_isBulkUpdatingBluRayTrackSelection)
        {
            return;
        }

        HandleBluRayTrackSelectionChanged();
    }

    private void UpdateBluRayTrackSelection(Func<BluRayTrackItemViewModel, bool> selector)
    {
        if (BluRayTracks.Count == 0)
        {
            return;
        }

        _isBulkUpdatingBluRayTrackSelection = true;
        try
        {
            foreach (var track in BluRayTracks)
            {
                track.IsSelected = selector(track);
            }
        }
        finally
        {
            _isBulkUpdatingBluRayTrackSelection = false;
        }

        HandleBluRayTrackSelectionChanged();
    }

    private void HandleBluRayTrackSelectionChanged()
    {
        OnPropertyChanged(nameof(BluRaySelectedTrackSummary));
        OnPropertyChanged(nameof(CanStartBluRayDemux));
        RefreshBluRayDemuxCommandPreview();
    }

    private void StoreBluRayPlaylistCache(
        BluRayPlaylistItem playlist,
        string summary,
        IReadOnlyList<BluRayTrackItemViewModel> trackItems)
    {
        var key = TryCreateBluRayPlaylistCacheKey(playlist);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _bluRayPlaylistTrackCache[key] = new BluRayPlaylistCacheEntry(summary, trackItems);
    }

    private bool TryRestoreCachedBluRayPlaylistState(BluRayPlaylistItem? playlist, bool updateStatus)
    {
        if (playlist is null)
        {
            return false;
        }

        var key = TryCreateBluRayPlaylistCacheKey(playlist);
        if (string.IsNullOrWhiteSpace(key) || !_bluRayPlaylistTrackCache.TryGetValue(key, out var entry))
        {
            return false;
        }

        ReplaceBluRayTrackItems(entry.TrackItems);
        _bluRayPlaylistSummaryText = entry.Summary;

        if (updateStatus)
        {
            BluRayDemuxStatusText = Texts.BluRayPlaylistLoadedStatus(playlist.DisplayName, entry.TrackItems.Count);
            StatusText = BluRayDemuxStatusText;
        }

        OnPropertyChanged(nameof(BluRayPlaylistSummaryText));
        return true;
    }

    private string? TryCreateBluRayPlaylistCacheKey(BluRayPlaylistItem playlist)
    {
        var backend = SelectedBluRayDemuxBackend?.Value;
        if (!backend.HasValue || string.IsNullOrWhiteSpace(BluRayDemuxSourcePath))
        {
            return null;
        }

        return $"{backend.Value}|{ResolveBluRayDiscPathForCache()}|{playlist.Id}";
    }

    private string ResolveBluRayDiscPathForCache()
    {
        try
        {
            return NormalizeBluRayDiscRoot(BluRayDemuxSourcePath, requireExists: false);
        }
        catch
        {
            return Path.GetFullPath(BluRayDemuxSourcePath.Trim());
        }
    }

    internal void RefreshBluRayDemuxCommandPreview()
    {
        if (_isBluRayDemuxRunning)
        {
            return;
        }

        BluRayDemuxCommandLine = TryCreateBluRayDemuxRequest(requireSourceExists: false, out var request, out _)
            ? _bluRayDemuxRunner.BuildDisplayCommand(request!)
            : string.Empty;
    }

    private bool TryCreateBluRayDemuxRequest(bool requireSourceExists, out BluRayDemuxRequest? request, out string? error)
    {
        try
        {
            request = CreateBluRayDemuxRequest(requireSourceExists);
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

    private BluRayDemuxRequest CreateBluRayDemuxRequest(bool requireSourceExists)
    {
        if (SelectedBluRayDemuxBackend is null)
        {
            throw new InvalidOperationException(Texts.BluRayToolPreparing);
        }

        if (string.IsNullOrWhiteSpace(BluRayDemuxSourcePath))
        {
            throw new InvalidOperationException(Texts.BluRayDiscSourceMissingError);
        }

        if (string.IsNullOrWhiteSpace(BluRayDemuxOutputPath))
        {
            throw new InvalidOperationException(Texts.BluRayOutputDirectoryMissingError);
        }

        if (SelectedBluRayPlaylist is null)
        {
            throw new InvalidOperationException(Texts.BluRayPlaylistMissingError);
        }

        var normalizedDiscRoot = NormalizeBluRayDiscRoot(BluRayDemuxSourcePath, requireSourceExists);
        var normalizedOutputDirectory = Path.GetFullPath(BluRayDemuxOutputPath.Trim());
        if (requireSourceExists && File.Exists(normalizedOutputDirectory))
        {
            throw new InvalidOperationException(Texts.BluRayOutputDirectoryInvalidError);
        }

        if (GetSelectedBluRayToolState() != ReadinessState.Ready)
        {
            throw new InvalidOperationException(Texts.BluRayToolMissingError(Texts.BluRayBackendLabel(SelectedBluRayDemuxBackend.Value)));
        }

        var selections = BluRayTracks
            .Where(static track => track.IsSelected)
            .Select(track => new BluRayTrackSelection(track.Track, ResolveBluRayTrackOutputPath(SelectedBluRayDemuxBackend.Value, normalizedOutputDirectory, SelectedBluRayPlaylist, track.Track)))
            .ToList();

        if (selections.Count == 0)
        {
            throw new InvalidOperationException(Texts.BluRayTrackSelectionMissingError);
        }

        return new BluRayDemuxRequest(Guid.NewGuid(), SelectedBluRayDemuxBackend.Value, normalizedDiscRoot, normalizedOutputDirectory, Path.Combine(normalizedOutputDirectory, SelectedBluRayPlaylist.Id), SelectedBluRayPlaylist, selections);
    }

    private string NormalizeBluRayDiscRoot(string rawPath, bool requireExists)
    {
        var normalized = Path.GetFullPath(rawPath.Trim());
        if (!requireExists)
        {
            return NormalizeBluRayDiscRootByPathShape(normalized);
        }

        if (requireExists && !Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException(Texts.BluRayDiscSourceMissingError);
        }

        if (Directory.Exists(Path.Combine(normalized, "BDMV", "PLAYLIST")))
        {
            return normalized;
        }

        if (Path.GetFileName(normalized).Equals("BDMV", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(normalized, "PLAYLIST")))
        {
            return Directory.GetParent(normalized)?.FullName ?? throw new InvalidOperationException(Texts.BluRayDiscStructureInvalidError);
        }

        if (Path.GetFileName(normalized).Equals("PLAYLIST", StringComparison.OrdinalIgnoreCase))
        {
            var bdmvDirectory = Directory.GetParent(normalized);
            if (bdmvDirectory is not null && bdmvDirectory.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase) && bdmvDirectory.Parent is not null)
            {
                return bdmvDirectory.Parent.FullName;
            }
        }

        throw new InvalidOperationException(Texts.BluRayDiscStructureInvalidError);
    }

    private static string NormalizeBluRayDiscRootByPathShape(string normalizedPath)
    {
        if (Path.GetFileName(normalizedPath).Equals("PLAYLIST", StringComparison.OrdinalIgnoreCase))
        {
            var bdmvDirectory = Directory.GetParent(normalizedPath);
            if (bdmvDirectory is not null && bdmvDirectory.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase) && bdmvDirectory.Parent is not null)
            {
                return bdmvDirectory.Parent.FullName;
            }
        }

        if (Path.GetFileName(normalizedPath).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(normalizedPath)?.FullName ?? normalizedPath;
        }

        return normalizedPath;
    }

    private void TryPopulateBluRayOutputPathIfEmpty()
    {
        if (string.IsNullOrWhiteSpace(BluRayDemuxSourcePath))
        {
            return;
        }

        try
        {
            var discRoot = NormalizeBluRayDiscRoot(BluRayDemuxSourcePath, requireExists: false);
            var discDirectory = Directory.GetParent(discRoot)?.FullName ?? discRoot;
            var suggestedPath = Path.Combine(discDirectory, $"{Path.GetFileName(discRoot)}_demux");
            if (!string.IsNullOrWhiteSpace(BluRayDemuxOutputPath) && !string.Equals(BluRayDemuxOutputPath, _lastBluRayOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _isUpdatingBluRayOutputPath = true;
            try
            {
                BluRayDemuxOutputPath = suggestedPath;
                _lastBluRayOutputPath = suggestedPath;
            }
            finally
            {
                _isUpdatingBluRayOutputPath = false;
            }
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"Failed to populate Blu-ray demux output path. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string? TryResolveBluRayOutputPreviewPath()
    {
        if (string.IsNullOrWhiteSpace(BluRayDemuxOutputPath))
        {
            return null;
        }

        return SelectedBluRayPlaylist is null
            ? Path.GetFullPath(BluRayDemuxOutputPath.Trim())
            : Path.Combine(Path.GetFullPath(BluRayDemuxOutputPath.Trim()), $"{SelectedBluRayPlaylist.Id}.*");
    }

    internal void RefreshBluRayTrackOutputPreviews()
    {
        var backend = SelectedBluRayDemuxBackend?.Value;
        var playlist = SelectedBluRayPlaylist;
        var hasOutputPath = !string.IsNullOrWhiteSpace(BluRayDemuxOutputPath);

        foreach (var track in BluRayTracks)
        {
            track.OutputPreview = backend.HasValue && playlist is not null && hasOutputPath
                ? ResolveBluRayTrackOutputPath(backend.Value, Path.GetFullPath(BluRayDemuxOutputPath.Trim()), playlist, track.Track)
                : string.Empty;
        }

        OnPropertyChanged(nameof(BluRayDemuxOutputPreviewText));
    }

    private static string ResolveBluRayTrackOutputPath(BluRayDemuxBackend backend, string outputDirectory, BluRayPlaylistItem playlist, BluRayTrackItem track)
    {
        if (backend == BluRayDemuxBackend.DgDemux)
        {
            return Path.Combine(outputDirectory, $"{playlist.Id}.*");
        }

        var baseName = $"{playlist.Id}_T{track.Order:00}_{GetTrackKindToken(track.Kind)}";
        if (!string.IsNullOrWhiteSpace(track.Language))
        {
            baseName = $"{baseName}_{SanitizeFileToken(track.Language)}";
        }

        return track.Kind == BluRayTrackKind.Chapters ? Path.Combine(outputDirectory, $"{baseName}.txt") : Path.Combine(outputDirectory, $"{baseName}.*");
    }

    private static string GetTrackKindToken(BluRayTrackKind kind) => kind switch
    {
        BluRayTrackKind.Chapters => "chapters",
        BluRayTrackKind.Video => "video",
        BluRayTrackKind.Audio => "audio",
        BluRayTrackKind.Subtitle => "subtitle",
        _ => "track"
    };

    private static string SanitizeFileToken(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (!invalidCharacters.Contains(character))
            {
                builder.Append(char.IsWhiteSpace(character) ? '_' : char.ToLowerInvariant(character));
            }
        }

        return builder.Length == 0 ? "track" : builder.ToString();
    }

    private ToolProbeResult? GetSelectedBluRayToolProbeResult()
    {
        var toolKind = SelectedBluRayDemuxBackend?.Value switch
        {
            BluRayDemuxBackend.DgDemux => RegisteredToolKind.DgDemux,
            BluRayDemuxBackend.Eac3To => RegisteredToolKind.Eac3To,
            _ => (RegisteredToolKind?)null
        };

        if (!toolKind.HasValue)
        {
            return null;
        }

        return _environmentReadinessReport?.Tools.FirstOrDefault(result => result.Kind == toolKind.Value)
            ?? BuildCachedBluRayToolProbeResult(toolKind.Value);
    }

    private ReadinessState GetSelectedBluRayToolState() => GetSelectedBluRayToolProbeResult()?.State ?? ReadinessState.Unknown;

    private ToolProbeResult? BuildCachedBluRayToolProbeResult(RegisteredToolKind kind)
    {
        if (!SetupGuideModule.HasSetupGuideStatusReport)
        {
            return null;
        }

        var status = ResolveCachedBluRayToolStatus(kind);
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

    private SetupDependencyStatus ResolveCachedBluRayToolStatus(RegisteredToolKind kind)
    {
        var dependencyKind = kind switch
        {
            RegisteredToolKind.DgDemux => SetupDependencyKind.DgDemux,
            RegisteredToolKind.Eac3To => SetupDependencyKind.Eac3To,
            _ => throw new InvalidOperationException($"Unsupported cached Blu-ray tool mapping: {kind}.")
        };

        return SetupGuideModule.ResolveSetupStatus(dependencyKind);
    }

    private void ApplyBluRayDemuxProgress(BluRayDemuxProgress update)
    {
        if (update.JobId != _activeBluRayDemuxJobId)
        {
            return;
        }

        AppendBluRayDemuxLogLine(update.DetailLine);
        if (update.ProgressFraction.HasValue)
        {
            BluRayDemuxProgressIsIndeterminate = false;
            BluRayDemuxProgressPercent = update.ProgressFraction.Value * 100.0;
        }
        else if (update.State == EncodingJobState.Running)
        {
            BluRayDemuxProgressIsIndeterminate = true;
        }

        var isStaleRunningUpdate = _isBluRayDemuxCancellationRequested && update.State == EncodingJobState.Running;
        var summary = ResolveBluRayDemuxRunningSummary(update);
        if (!isStaleRunningUpdate && !string.IsNullOrWhiteSpace(summary))
        {
            BluRayDemuxStatusText = summary;
            StatusText = summary;
        }

        RaiseDashboardCardActivityPropertyChanges();
    }

    private void AppendBluRayDemuxLogLine(string line)
    {
        var normalized = string.IsNullOrWhiteSpace(line) ? string.Empty : line.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!string.Equals(_bluRayDemuxLastLogLine, normalized, StringComparison.Ordinal))
        {
            _bluRayDemuxLastLogLine = normalized;
            OnPropertyChanged(nameof(BluRayDemuxProgressSecondaryText));
            OnPropertyChanged(nameof(BluRayDemuxProgressSecondaryVisibility));
        }

        if (!UsesCompactBluRayDemuxLog(SelectedBluRayDemuxBackend?.Value))
        {
            _bluRayDemuxLogBuilder.AppendLine(normalized);
            if (_bluRayDemuxLogBuilder.Length > BluRayDemuxLogLimit)
            {
                _bluRayDemuxLogBuilder.Remove(0, _bluRayDemuxLogBuilder.Length - BluRayDemuxLogLimit);
            }

            BluRayDemuxLog = _bluRayDemuxLogBuilder.ToString().Trim();
            return;
        }

        ReplaceCompactBluRayDemuxLog(ResolveBluRayDemuxLogPhaseLabel(normalized), normalized);
    }

    private void ResetBluRayDemuxLogState()
    {
        _bluRayDemuxLogBuilder.Clear();
        _bluRayDemuxLogStageLines.Clear();
        _bluRayDemuxLastLogLine = string.Empty;
        _bluRayDemuxLiveLogLine = string.Empty;
        _bluRayDemuxLogPhaseMarker = string.Empty;
        BluRayDemuxLog = string.Empty;
        OnPropertyChanged(nameof(BluRayDemuxProgressSecondaryText));
        OnPropertyChanged(nameof(BluRayDemuxProgressSecondaryVisibility));
    }

    private string ResolveBluRayDemuxRunningSummary(BluRayDemuxProgress update)
    {
        if (update.State != EncodingJobState.Running)
        {
            return update.Summary;
        }

        var backend = SelectedBluRayDemuxBackend?.Value ?? BluRayDemuxBackend.DgDemux;
        if (backend == BluRayDemuxBackend.Eac3To
            && TryParseEac3ToAnalyzeProgress(update.DetailLine, out var analyzePercent))
        {
            return Texts.BluRayDemuxAnalyzingStatus(Texts.BluRayBackendLabel(backend), analyzePercent);
        }

        return update.Summary;
    }

    private void ReplaceCompactBluRayDemuxLog(string? phaseLabel, string line)
    {
        var normalized = string.IsNullOrWhiteSpace(line) ? string.Empty : line.Trim();
        var normalizedPhase = string.IsNullOrWhiteSpace(phaseLabel) ? string.Empty : phaseLabel.Trim();
        if (!string.Equals(_bluRayDemuxLogPhaseMarker, normalizedPhase, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(normalizedPhase))
        {
            _bluRayDemuxLogPhaseMarker = normalizedPhase;
            AppendBluRayDemuxStageLogLine(normalizedPhase);
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            RefreshCompactBluRayDemuxLogText();
            return;
        }

        if (IsCompactBluRayDemuxLiveLine(SelectedBluRayDemuxBackend?.Value, normalized))
        {
            if (!string.Equals(_bluRayDemuxLiveLogLine, normalized, StringComparison.Ordinal))
            {
                _bluRayDemuxLiveLogLine = normalized;
                RefreshCompactBluRayDemuxLogText();
            }

            return;
        }

        _bluRayDemuxLiveLogLine = string.Empty;
        AppendBluRayDemuxStageLogLine(normalized);
    }

    private void AppendBluRayDemuxStageLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (_bluRayDemuxLogStageLines.Count > 0
            && string.Equals(_bluRayDemuxLogStageLines[^1], line, StringComparison.Ordinal))
        {
            RefreshCompactBluRayDemuxLogText();
            return;
        }

        _bluRayDemuxLogStageLines.Add(line);
        if (_bluRayDemuxLogStageLines.Count > BluRayDemuxStageLogLimit)
        {
            _bluRayDemuxLogStageLines.RemoveAt(0);
        }

        RefreshCompactBluRayDemuxLogText();
    }

    private void RefreshCompactBluRayDemuxLogText()
    {
        var lines = new List<string>(_bluRayDemuxLogStageLines);
        if (!string.IsNullOrWhiteSpace(_bluRayDemuxLiveLogLine))
        {
            lines.Add(_bluRayDemuxLiveLogLine);
        }

        BluRayDemuxLog = string.Join(Environment.NewLine, lines);
    }

    private string ResolveBluRayDemuxLogPhaseLabel(string line)
    {
        return SelectedBluRayDemuxBackend?.Value switch
        {
            BluRayDemuxBackend.Eac3To when ToolLogLineClassifier.IsEac3ToAnalyzeLine(line) => Texts.BluRayDemuxAnalyzePhaseLabel,
            BluRayDemuxBackend.Eac3To when ToolLogLineClassifier.IsEac3ToProcessLine(line) => Texts.BluRayDemuxProcessPhaseLabel,
            _ => string.Empty
        };
    }

    private static bool UsesCompactBluRayDemuxLog(BluRayDemuxBackend? backend)
    {
        return backend is BluRayDemuxBackend.DgDemux or BluRayDemuxBackend.Eac3To;
    }

    private static bool IsCompactBluRayDemuxLiveLine(BluRayDemuxBackend? backend, string line)
    {
        return ToolLogLineClassifier.IsBluRayTransientLine(backend, line);
    }

    private static bool TryParseEac3ToAnalyzeProgress(string line, out double percent)
    {
        percent = 0;
        if (!ToolLogLineClassifier.IsEac3ToAnalyzeLine(line))
        {
            return false;
        }

        var separatorIndex = line.IndexOf(':');
        var percentIndex = line.LastIndexOf('%');
        if (separatorIndex < 0 || percentIndex <= separatorIndex)
        {
            return false;
        }

        var numericText = line[(separatorIndex + 1)..percentIndex].Trim();
        return double.TryParse(numericText, NumberStyles.Float, CultureInfo.InvariantCulture, out percent);
    }

    private void SetBluRayDiscScanningState(bool value)
    {
        if (_isBluRayDiscScanning == value)
        {
            return;
        }

        _isBluRayDiscScanning = value;
        OnPropertyChanged(nameof(IsBluRayDiscScanning));
        OnPropertyChanged(nameof(BluRayDiscScanProgressVisibility));
        RaiseBluRayDemuxInputPropertyChanges();
        RaiseDashboardCardActivityPropertyChanges();
    }

    private void SetBluRayPlaylistLoadingState(bool value)
    {
        if (_isBluRayPlaylistLoading == value)
        {
            return;
        }

        _isBluRayPlaylistLoading = value;
        OnPropertyChanged(nameof(IsBluRayPlaylistLoading));
        RaiseBluRayDemuxInputPropertyChanges();
        RaiseDashboardCardActivityPropertyChanges();
    }

    private void SetBluRayDemuxRunningState(bool isRunning, Guid? jobId)
    {
        if (_isBluRayDemuxRunning == isRunning && _activeBluRayDemuxJobId == jobId)
        {
            return;
        }

        _isBluRayDemuxRunning = isRunning;
        _activeBluRayDemuxJobId = jobId;
        _isBluRayDemuxCancellationRequested = false;
        OnPropertyChanged(nameof(IsBluRayDemuxRunning));
        OnPropertyChanged(nameof(HasRunningAppWork));
        RaiseBluRayDemuxInputPropertyChanges();
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

    internal void SetBluRayDemuxDisplayState(EncodingJobState? state)
    {
        if (_bluRayDemuxDisplayState == state)
        {
            return;
        }

        _bluRayDemuxDisplayState = state;
        SetBluRayDemuxPresentationState(state switch
        {
            EncodingJobState.Running => TaskPresentationState.Running,
            EncodingJobState.Completed => TaskPresentationState.Completed,
            EncodingJobState.Failed => TaskPresentationState.Failed,
            EncodingJobState.Cancelled => TaskPresentationState.Cancelled,
            _ => TaskPresentationState.Idle
        });
        OnPropertyChanged(nameof(BluRayDemuxStatusPanelBorderBrush));
        OnPropertyChanged(nameof(BluRayDemuxProgressTrackBrush));
        OnPropertyChanged(nameof(BluRayDemuxProgressBorderBrush));
        OnPropertyChanged(nameof(BluRayDemuxProgressFillBrush));
        RaiseDashboardCardActivityPropertyChanges();
    }

    private void SetBluRayDemuxPresentationState(TaskPresentationState state)
    {
        if (_bluRayDemuxPresentationState == state)
        {
            return;
        }

        _bluRayDemuxPresentationState = state;
        OnPropertyChanged(nameof(BluRayDemuxPresentationState));
    }

    private void ClampBluRayDemuxProgressForTerminalState(EncodingJobState state)
    {
        BluRayDemuxProgressIsIndeterminate = false;
        BluRayDemuxProgressPercent = state == EncodingJobState.Completed ? 100 : Math.Min(BluRayDemuxProgressPercent, 99.9);
    }

    private void DisposeBluRayProbeCancellation()
    {
        _bluRayProbeCancellationTokenSource?.Cancel();
        _bluRayProbeCancellationTokenSource?.Dispose();
        _bluRayProbeCancellationTokenSource = null;
    }

    private void DisposeBluRayDemuxCancellation()
    {
        _bluRayDemuxCancellationTokenSource?.Dispose();
        _bluRayDemuxCancellationTokenSource = null;
    }

    private void CancelPendingBluRayDemuxInputRefresh()
    {
        _bluRayDemuxInputRefreshCancellationTokenSource?.Cancel();
        _bluRayDemuxInputRefreshCancellationTokenSource?.Dispose();
        _bluRayDemuxInputRefreshCancellationTokenSource = null;
    }

    internal void RaiseBluRayDemuxInputPropertyChanges()
    {
        OnPropertyChanged(nameof(CanScanBluRayDisc));
        OnPropertyChanged(nameof(CanStartBluRayDemux));
        OnPropertyChanged(nameof(CanCancelBluRayDemux));
        OnPropertyChanged(nameof(CanClearBluRayDemuxTask));
        OnPropertyChanged(nameof(BluRayDemuxOutputPreviewText));
    }

    internal void RaiseBluRayDemuxEnvironmentPropertyChanges()
    {
        OnPropertyChanged(nameof(CanScanBluRayDisc));
        OnPropertyChanged(nameof(CanStartBluRayDemux));
        OnPropertyChanged(nameof(BluRayToolSummary));
        OnPropertyChanged(nameof(BluRayDemuxBackendNote));
    }

    internal void RaiseBluRayDemuxLanguagePropertyChanges()
    {
        OnPropertyChanged(nameof(SelectedBluRayDemuxBackend));
        OnPropertyChanged(nameof(BluRayDiscSummaryText));
        OnPropertyChanged(nameof(BluRayPlaylistSummaryText));
        OnPropertyChanged(nameof(BluRaySelectedTrackSummary));
        OnPropertyChanged(nameof(BluRayToolSummary));
        OnPropertyChanged(nameof(BluRayDemuxBackendNote));
        RaiseBluRayDemuxInputPropertyChanges();
    }

    internal void BeginBluRayDemuxLanguageStateApplication()
    {
        _isApplyingBluRayDemuxLanguageState = true;
    }

    internal void EndBluRayDemuxLanguageStateApplication()
    {
        _isApplyingBluRayDemuxLanguageState = false;
    }

    private static Brush ResolveBluRayDemuxProgressTrackBrush(EncodingJobState? state) => state switch
    {
        EncodingJobState.Failed => ResolveBrush("AppErrorSoftBrush"),
        EncodingJobState.Cancelled => ResolveBrush("AppNeutralSoftBrush"),
        _ => ResolveBrush("QueueProgressSoftBrush")
    };

    private static Brush ResolveBluRayDemuxProgressBorderBrush(EncodingJobState? state) => state switch
    {
        EncodingJobState.Failed => ResolveBrush("AppErrorBrush"),
        EncodingJobState.Cancelled => ResolveBrush("AppNeutralBrush"),
        _ => ResolveBrush("QueueProgressFillBrush")
    };

    private static Brush ResolveBluRayDemuxProgressFillBrush(EncodingJobState? state) => state switch
    {
        EncodingJobState.Failed => ResolveBrush("AppErrorBrush"),
        EncodingJobState.Cancelled => ResolveBrush("AppNeutralBrush"),
        _ => ResolveBrush("QueueProgressAreaBrush")
    };

    private sealed record BluRayPlaylistCacheEntry(
        string Summary,
        IReadOnlyList<BluRayTrackItemViewModel> TrackItems);
}
