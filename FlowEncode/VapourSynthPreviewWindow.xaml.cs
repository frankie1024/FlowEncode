using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using FlowEncode.ViewModels;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.System;
using WinRT.Interop;

namespace FlowEncode;

public sealed partial class VapourSynthPreviewWindow : Window
{
    private static readonly PreviewDisplayFilter[] PreviewFilterCycle = Enum.GetValues<PreviewDisplayFilter>();
    private static readonly PreviewCompareMode[] PreviewCompareModeCycle = Enum.GetValues<PreviewCompareMode>();

    private readonly LocalAppPaths _appPaths;
    private readonly Dictionary<int, PreviewOutputState> _outputStates = [];
    private readonly LatestRequestScheduler<PreviewFrameRequest> _frameRequestScheduler;
    private readonly PreviewFrameComposer _frameComposer = new();
    private readonly PreviewRenderDiagnostics _renderDiagnostics;
    private readonly IVapourSynthPreviewService _previewService;
    private readonly CancellationTokenSource _previewLifetime = new();
    private readonly CancellationToken _previewLifetimeToken;
    private readonly PointerEventHandler _frameSliderPointerPressedHandler;
    private readonly PointerEventHandler _frameSliderPointerReleasedHandler;
    private readonly PointerEventHandler _frameSliderPointerCanceledHandler;
    private readonly PointerEventHandler _frameSliderPointerCaptureLostHandler;
    private CanvasDevice? _previewCanvasDevice;
    private CompositionDrawingSurface? _previewDrawingSurface;
    private CompositionDrawingSurface? _previewDrawingSurfaceB;
    private CompositionDrawingSurface? _previewDrawingSurfaceDiff;
    private CompositionGraphicsDevice? _previewGraphicsDevice;
    private CompositionSurfaceBrush? _previewImageBrush;
    private CompositionSurfaceBrush? _previewImageBrushB;
    private CompositionSurfaceBrush? _previewImageBrushDiff;
    private ContainerVisual? _singlePreviewContainerVisual;
    private ContainerVisual? _wipeLayerAContainerVisual;
    private ContainerVisual? _wipeLayerBContainerVisual;
    private SpriteVisual? _previewImageVisual;
    private SpriteVisual? _previewImageVisualB;
    private SpriteVisual? _previewImageVisualDiff;
    private SpriteVisual? _sideBySideVisualA;
    private SpriteVisual? _sideBySideVisualB;
    private InsetClip? _wipeVisualAClip;
    private InsetClip? _wipeVisualBClip;
    private CanvasBitmap? _cachedBitmapA;
    private CanvasBitmap? _cachedBitmapB;
    private bool _hasManualSizeAlignmentOverride;
    private int _previewDrawingSurfaceHeight;
    private int _previewDrawingSurfaceWidth;
    private int _previewDrawingSurfaceHeightB;
    private int _previewDrawingSurfaceWidthB;
    private int _previewDrawingSurfaceHeightDiff;
    private int _previewDrawingSurfaceWidthDiff;
    private PreviewDisplayFilter _previewFilter;
    private PreviewRenderOptions _lastRenderOptions = PreviewRenderOptions.Default;
    private PreviewRenderOptions _secondaryLastRenderOptions = PreviewRenderOptions.Default;
    private CancellationTokenSource? _pipetteSampleCts;
    private int _pipetteLockedX = -1;
    private int _pipetteLockedY = -1;
    private bool _isClosed;
    private bool _isFullScreenActive;
    private bool _isInternalControlUpdate;
    private bool _isFrameSliderInteracting;
    private bool _isPreviewPanActive;
    private bool _isSyncingCompareScroll;
    private bool _isWipeDividerDragging;
    private bool _isPlaying;
    private bool _hasEverActivated;
    private Task? _closePreviewSessionTask;
    private ScrollViewer? _activePanScrollViewer;
    private uint _previewPanPointerId;
    private uint _wipeDividerPointerId;
    private long _previewSessionRevision;
    private string? _pendingStatusText;
    private byte[]? _displayedFramePixels;
    private byte[]? _reusableDisplayedFramePixels;
    private int _displayedFrameHeight;
    private int _displayedFrameWidth;
    private byte[]? _secondaryDisplayedFramePixels;
    private byte[]? _reusableSecondaryDisplayedFramePixels;
    private int _secondaryDisplayedFrameHeight;
    private int _secondaryDisplayedFrameWidth;
    private readonly List<TextBox> _attachedPreviewNumberBoxEditors = [];
    private readonly KeyEventHandler _numberBoxEditorKeyDownHandler;
    private XamlRoot? _observedXamlRoot;
    private Point _previewPanOrigin;
    private double _previewPanStartHorizontalOffset;
    private double _previewPanStartVerticalOffset;
    private VapourSynthPreviewFrameData? _lastFrameData;
    private VapourSynthPreviewFrameData? _secondaryLastFrameData;
    private byte[]? _sourceFramePixels;
    private byte[]? _reusableSourceFramePixels;
    private int _sourceFrameHeight;
    private int _sourceFrameWidth;
    private byte[]? _secondarySourceFramePixels;
    private byte[]? _reusableSecondarySourceFramePixels;
    private int _secondarySourceFrameHeight;
    private int _secondarySourceFrameWidth;
    private VapourSynthPreviewOpenRequest? _currentRequest;
    private VapourSynthPreviewSessionInfo? _currentSession;
    private VapourSynthPreviewOutputInfo? _selectedOutputInfo;
    private VapourSynthPreviewOutputInfo? _secondaryOutputInfo;
    private int _activeChapterIndex = -1;
    private int? _pendingNavigationFrame;
    private AppLanguage _lastLanguage = AppLanguage.Chinese;
    private AppThemePreference _lastThemePreference;

    public VapourSynthPreviewWindowViewModel ViewModel { get; }

    public Func<Func<string, SmartScriptEditResult>, Task<SmartScriptEditResult?>>? SmartEditScriptAsync { get; set; }

    public event EventHandler? PreviewWindowClosed;

    public VapourSynthPreviewWindow(
        VapourSynthPreviewWindowViewModel viewModel,
        IVapourSynthPreviewService previewService)
    {
        ViewModel = viewModel;
        _appPaths = App.GetService<LocalAppPaths>();
        _previewService = previewService;
        _previewLifetimeToken = _previewLifetime.Token;
        _frameRequestScheduler = new LatestRequestScheduler<PreviewFrameRequest>(ExecuteScheduledFrameRequestAsync);
        _renderDiagnostics = new PreviewRenderDiagnostics(_appPaths);
        InitializeComponent();
        _frameSliderPointerPressedHandler = new PointerEventHandler(FrameSliderInteraction_PointerPressed);
        _frameSliderPointerReleasedHandler = new PointerEventHandler(FrameSliderInteraction_PointerReleased);
        _frameSliderPointerCanceledHandler = new PointerEventHandler(FrameSliderInteraction_PointerCanceled);
        _frameSliderPointerCaptureLostHandler = new PointerEventHandler(FrameSliderInteraction_PointerCaptureLost);
        _numberBoxEditorKeyDownHandler = new KeyEventHandler(NumberBoxEditor_KeyDown);

        if (Content is FrameworkElement root)
        {
            root.DataContext = ViewModel;
        }

        BuildPreviewFilterMenu();
        BuildCompareModeMenu();
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _flickerTimer.Tick += FlickerTimer_Tick;
        _tabHoldFlickerTimer.Tick += TabHoldFlickerTimer_Tick;
        _statusCommitTimer.Tick += StatusCommitTimer_Tick;
        Activated += VapourSynthPreviewWindow_Activated;
        Closed += VapourSynthPreviewWindow_Closed;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        AttachFrameSliderInteractionHandlers();
        RootLayout.Loaded += RootLayout_Loaded;
        RootLayout.Unloaded += RootLayout_Unloaded;
        RootLayout.ActualThemeChanged += RootLayout_ActualThemeChanged;
        PreviewImageHost.SizeChanged += PreviewImageHost_SizeChanged;
        LeftCompareImageHost.SizeChanged += PreviewImageHost_SizeChanged;
        RightCompareImageHost.SizeChanged += PreviewImageHost_SizeChanged;
        RootLayout.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(RootLayout_PreviewKeyDown), true);
        RootLayout.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(RootLayout_PreviewKeyUp), true);
        RootLayout.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RootLayout_KeyDown), true);
        RootLayout.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(RootLayout_KeyUp), true);
        Title = ViewModel.WindowTitle;
    }

    private DispatcherTimer _playbackTimer { get; } = new();
    private DispatcherTimer _flickerTimer { get; } = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DispatcherTimer _tabHoldFlickerTimer { get; } = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DispatcherTimer _statusCommitTimer { get; } = new()
    {
        Interval = TimeSpan.FromMilliseconds(180)
    };

    public async Task<bool> OpenOrRefreshAsync(
        VapourSynthPreviewOpenRequest request,
        AppLanguage language,
        AppThemePreference themePreference,
        PreviewCompareMode? initialCompareMode = null,
        int? repairFrameNumber = null)
    {
        if (_previewLifetimeToken.IsCancellationRequested)
        {
            return false;
        }

        var shouldResetChapters = _currentRequest is not null
            && !IsSamePreviewTarget(_currentRequest, request);
        _currentRequest = request;
        if (shouldResetChapters)
        {
            _activeChapterIndex = -1;
            ViewModel.SecondaryFrameOffset = 0;
            ViewModel.SubpixelOffsetX = 0;
            ViewModel.SubpixelOffsetY = 0;
            ViewModel.AlignStatusText = string.Empty;
            ViewModel.BookmarkedFrames.Clear();
            ViewModel.BasketStatusText = string.Empty;
            ViewModel.NotifyBasketChanged();
            ViewModel.WorstFrameResults.Clear();
            ViewModel.WorstScanStatusText = string.Empty;
            ViewModel.NotifyWorstScanResultsChanged();
            ViewModel.SceneRanges.Clear();
            ViewModel.PendingSceneStartFrame = null;
            ViewModel.NotifySceneRangesChanged();
            ViewModel.ReplaceChapters([]);
        }

        ApplyPresentation(language, themePreference);
        var loaded = await LoadSessionAsync(!shouldResetChapters, initialCompareMode, repairFrameNumber);
        if (_previewLifetimeToken.IsCancellationRequested)
        {
            return false;
        }

        if (!loaded)
        {
            if (_hasEverActivated && !_isClosed)
            {
                Close();
            }

            return false;
        }

        Activate();
        _hasEverActivated = true;
        EnsurePreferredWindowPresentation();
        RefreshDisplayScale();
        ApplyPreviewScalingAlgorithm();
        return true;
    }

    private static bool IsSamePreviewTarget(
        VapourSynthPreviewOpenRequest left,
        VapourSynthPreviewOpenRequest right)
    {
        return string.Equals(
            BuildPreviewTargetKey(left),
            BuildPreviewTargetKey(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPreviewTargetKey(VapourSynthPreviewOpenRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SourceFilePath))
        {
            try
            {
                return $"file:{Path.GetFullPath(request.SourceFilePath)}";
            }
            catch
            {
                return $"file:{request.SourceFilePath.Trim()}";
            }
        }

        return $"buffer:{request.WorkingDirectory}|{request.DisplayName}";
    }

    public async Task CloseForOwnerShutdownAsync()
    {
        if (!_isClosed)
        {
            StopPlayback();
            _frameRequestScheduler.ClearPending();
        }

        await ClosePreviewSessionIfNeededAsync();
        if (!_isClosed)
        {
            Close();
        }
    }

    public void ApplyPresentation(
        AppLanguage language,
        AppThemePreference themePreference)
    {
        _lastLanguage = language;
        _lastThemePreference = themePreference;
        ViewModel.ApplyLanguage(language);
        BuildPreviewFilterMenu();
        BuildCompareModeMenu();
        ApplyTheme(themePreference);
        RefreshDisplayScale();
        SyncControls();
        QueueAttachPreviewNumberBoxEditorHandlers();
    }

    private void RootLayout_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void RootLayout_Drop(object sender, DragEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(RootLayout_Drop),
            async () =>
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    return;
                }

                var items = await e.DataView.GetStorageItemsAsync();
                var files = items
                    .OfType<Windows.Storage.StorageFile>()
                    .Where(static file =>
                    {
                        var kind = AppLaunchActivation.ClassifyFilePath(file.Path);
                        return kind is DragDropPayloadType.Video or DragDropPayloadType.Script;
                    })
                    .ToList();

                if (files.Count == 0)
                {
                    SetStatusText(ViewModel.Texts.VapourSynthPreviewDropCompareUnsupportedStatus);
                    return;
                }

                string sourceAPath;
                string sourceBPath;
                string labelA;
                string labelB;

                if (files.Count >= 2)
                {
                    sourceAPath = Path.GetFullPath(files[0].Path);
                    sourceBPath = Path.GetFullPath(files[1].Path);
                    labelA = Path.GetFileName(sourceAPath);
                    labelB = Path.GetFileName(sourceBPath);
                }
                else
                {
                    sourceBPath = Path.GetFullPath(files[0].Path);
                    labelB = Path.GetFileName(sourceBPath);

                    if (!string.IsNullOrWhiteSpace(_currentRequest?.SourceFilePath) && File.Exists(_currentRequest.SourceFilePath))
                    {
                        sourceAPath = Path.GetFullPath(_currentRequest.SourceFilePath);
                        labelA = Path.GetFileName(sourceAPath);
                    }
                    else if (!string.IsNullOrWhiteSpace(_currentRequest?.Content))
                    {
                        var tempDir = Path.Combine(_appPaths.DataRootPath, "vapoursynth-preview");
                        Directory.CreateDirectory(tempDir);
                        sourceAPath = Path.Combine(tempDir, "drop-compare-source.vpy");
                        await File.WriteAllTextAsync(sourceAPath, _currentRequest.Content, new System.Text.UTF8Encoding(false));
                        labelA = string.IsNullOrWhiteSpace(_currentRequest.DisplayName) ? "Current" : _currentRequest.DisplayName;
                    }
                    else
                    {
                        SetStatusText(ViewModel.Texts.VapourSynthPreviewDropCompareUnsupportedStatus);
                        return;
                    }
                }

                SetStatusText(ViewModel.Texts.VapourSynthPreviewDropCompareLoadingStatus(labelA, labelB));
                var compareScript = PreviewCompareScriptBuilder.BuildTwoSourceCompareScript(sourceAPath, sourceBPath, labelA, labelB);
                var workingDir = Path.GetDirectoryName(sourceBPath) ?? Path.GetDirectoryName(sourceAPath) ?? AppContext.BaseDirectory;
                var displayName = ViewModel.Texts.VapourSynthPreviewQuickCompareTitle(labelA, labelB);
                var compareRequest = new VapourSynthPreviewOpenRequest(
                    null,
                    displayName,
                    compareScript,
                    workingDir);
                SmartEditScriptAsync = null;
                await OpenOrRefreshAsync(
                    compareRequest,
                    _lastLanguage,
                    _lastThemePreference,
                    initialCompareMode: PreviewCompareMode.Wipe);
            });
    }

    private async Task<bool> LoadSessionAsync(
        bool preserveCurrentFrame,
        PreviewCompareMode? initialCompareMode = null,
        int? repairFrameNumber = null)
    {
        if (_currentRequest is null || _previewLifetimeToken.IsCancellationRequested)
        {
            return false;
        }

        _previewSessionRevision++;
        StopPlayback();
        _frameRequestScheduler.ClearPending();
        SaveCurrentOutputState();
        foreach (var state in _outputStates.Values)
        {
            state.AutoCropRepair = null;
        }
        if (repairFrameNumber.HasValue)
        {
            _outputStates.Clear();
            _selectedOutputInfo = null;
            _secondaryOutputInfo = null;
            ViewModel.SecondaryFrameOffset = 0;
            ViewModel.SubpixelOffsetX = 0;
            ViewModel.SubpixelOffsetY = 0;
        }

        var previousOutputIndex = _selectedOutputInfo?.Index ?? 0;
        var previousFrame = repairFrameNumber ?? (preserveCurrentFrame ? ViewModel.CurrentFrame : 0);

        ViewModel.ResetForSession(
            _currentRequest.DisplayName,
            ViewModel.Texts.VapourSynthPreviewEvaluatingStatus(_currentRequest.DisplayName),
            []);
        ResetDisplayedFrameState();
        SyncControls();

        try
        {
            _currentSession = await _previewService.OpenSessionAsync(_currentRequest, _previewLifetimeToken);
            _previewLifetimeToken.ThrowIfCancellationRequested();
            ViewModel.ApplySessionCapabilities(
                _currentSession.HasVsAlign,
                _currentSession.HasCambi,
                _currentSession.HasNativeRes);
            ViewModel.ResetForSession(
                _currentRequest.DisplayName,
                ViewModel.Texts.VapourSynthPreviewSessionReadyStatus,
                _currentSession.Outputs);
            PruneOutputStates(_currentSession.Outputs);
            if (initialCompareMode is { } targetCompareMode && ViewModel.CanCompareOutputs)
            {
                ViewModel.CompareMode = PreviewCompareCoordinator.NormalizeMode(targetCompareMode, ViewModel.Outputs.Count);
                UpdateCompareModeSelection();
                SyncFlickerTimer();
            }

            var output = _currentSession.Outputs.FirstOrDefault(item => item.Index == previousOutputIndex)
                ?? _currentSession.Outputs[0];
            EnsureSecondaryOutputSelection(output);
            UpdateCompareModeSelection();
            await SelectOutputAsync(output, previousFrame, useExplicitFrame: true);
            return true;
        }
        catch (OperationCanceledException) when (_previewLifetimeToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _selectedOutputInfo = null;
            _secondaryOutputInfo = null;
            _currentSession = null;
            SetStatusText(ViewModel.Texts.VapourSynthPreviewRenderFailedStatus(ex.Message));
            SyncControls();
            return false;
        }
    }

    private async Task SelectOutputAsync(
        VapourSynthPreviewOutputInfo outputInfo,
        int? explicitFrameNumber = null,
        bool useExplicitFrame = false)
    {
        var previousOutputInfo = _selectedOutputInfo;
        SaveCurrentOutputState();
        var previousZoomState = previousOutputInfo is null
            ? null
            : CaptureZoomState(GetOrCreateOutputState(previousOutputInfo));

        var outputState = GetOrCreateOutputState(outputInfo);
        if (previousOutputInfo is not null && previousOutputInfo.Index != outputInfo.Index)
        {
            _hasManualSizeAlignmentOverride = false;
            if (previousZoomState is not null)
            {
                ApplyZoomState(outputState, previousZoomState);
            }
        }

        _selectedOutputInfo = outputInfo;
        ViewModel.SelectedOutput = ViewModel.Outputs.FirstOrDefault(item => item.Info.Index == outputInfo.Index);
        EnsureSecondaryOutputSelection(outputInfo);
        ViewModel.UpdateForOutput(outputInfo);
        RestoreOutputPresentationState(outputState, outputInfo);
        SyncControls();

        var targetFrame = DetermineTargetFrame(
            previousOutputInfo,
            outputInfo,
            outputState,
            explicitFrameNumber,
            useExplicitFrame);
        await RequestFrameAsync(outputInfo, targetFrame);
    }

    private void EnsureSecondaryOutputSelection(VapourSynthPreviewOutputInfo primaryOutput)
    {
        if (_currentSession is null || !ViewModel.CanCompareOutputs)
        {
            _secondaryOutputInfo = null;
            ViewModel.SelectedSecondaryOutput = null;
            return;
        }

        var preferredSecondaryIndex = _secondaryOutputInfo?.Index ?? ViewModel.SelectedSecondaryOutput?.Info.Index;
        var resolvedIndex = PreviewCompareCoordinator.ResolveSecondaryOutputIndex(
            _currentSession.Outputs,
            primaryOutput.Index,
            preferredSecondaryIndex);
        _secondaryOutputInfo = resolvedIndex is int index
            ? _currentSession.Outputs.FirstOrDefault(output => output.Index == index)
            : null;
        ViewModel.SelectedSecondaryOutput = _secondaryOutputInfo is not null
            ? ViewModel.Outputs.FirstOrDefault(option => option.Info.Index == _secondaryOutputInfo.Index)
            : null;
    }

    private Task RequestFrameAsync(int frameNumber)
    {
        if (_selectedOutputInfo is null)
        {
            return Task.CompletedTask;
        }

        return RequestFrameAsync(_selectedOutputInfo, frameNumber);
    }

    private Task RequestFrameAsync(
        VapourSynthPreviewOutputInfo outputInfo,
        int frameNumber,
        bool preservePendingNavigationFrame = false)
    {
        if (_previewLifetimeToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        frameNumber = Math.Clamp(frameNumber, 0, Math.Max(0, outputInfo.TotalFrames - 1));
        if (!preservePendingNavigationFrame)
        {
            _pendingNavigationFrame = null;
        }

        var compareMode = ViewModel.IsCompareModeActive ? ViewModel.CompareMode : PreviewCompareMode.Single;
        VapourSynthPreviewOutputInfo? secondaryOutput = null;
        int? secondaryFrameNumber = null;
        if (compareMode != PreviewCompareMode.Single)
        {
            EnsureSecondaryOutputSelection(outputInfo);
            if (_secondaryOutputInfo is { } resolvedSecondary)
            {
                secondaryOutput = resolvedSecondary;
                secondaryFrameNumber = PreviewCompareCoordinator.ResolveSecondaryFrameNumber(
                    outputInfo,
                    resolvedSecondary,
                    frameNumber,
                    ViewModel.SelectedOutputSyncMode?.Value,
                    ViewModel.IsTimelineTimeMode,
                    ViewModel.SecondaryFrameOffset);
            }
            else
            {
                compareMode = PreviewCompareMode.Single;
            }
        }

        var request = new PreviewFrameRequest(
            _previewSessionRevision,
            outputInfo,
            frameNumber,
            compareMode,
            secondaryOutput,
            secondaryFrameNumber,
            ViewModel.CurrentRenderOptions,
            ViewModel.SecondaryRenderOptions);
        return _frameRequestScheduler.ScheduleAsync(request);
    }

    private async Task ExecuteScheduledFrameRequestAsync(
        LatestRequestScheduler<PreviewFrameRequest>.ScheduledRequest scheduledRequest)
    {
        var request = scheduledRequest.Request;
        var helperRenderElapsed = TimeSpan.Zero;
        var transportReadElapsed = TimeSpan.Zero;
        var composeElapsed = TimeSpan.Zero;
        var surfaceUpdateElapsed = TimeSpan.Zero;
        byte[]? loadedSourcePixels = null;
        byte[]? loadedSecondarySourcePixels = null;
        var ownsLoadedSourcePixels = false;
        var ownsLoadedSecondarySourcePixels = false;
        DisplayFramePayload? displayPayload = null;
        DisplayFramePayload? secondaryDisplayPayload = null;

        try
        {
            VapourSynthPreviewFrameData frameData;
            if (_lastFrameData is { } cachedFrameA
                && _sourceFramePixels is { } cachedPixelsA
                && cachedFrameA.OutputIndex == request.OutputInfo.Index
                && cachedFrameA.FrameNumber == request.FrameNumber
                && _lastRenderOptions == request.RenderOptions)
            {
                frameData = cachedFrameA;
                loadedSourcePixels = cachedPixelsA;
            }
            else
            {
                frameData = await _previewService.RenderFrameAsync(
                    request.OutputInfo.Index,
                    request.FrameNumber,
                    request.RenderOptions,
                    ConsumeReusableSourcePixels(),
                    _previewLifetimeToken);
                helperRenderElapsed += frameData.HelperRenderElapsed;
                transportReadElapsed += frameData.TransportReadElapsed;
                loadedSourcePixels = frameData.Pixels;
                ownsLoadedSourcePixels = true;
                _lastRenderOptions = request.RenderOptions;
            }

            if (!IsActiveFrameRequest(request, scheduledRequest))
            {
                if (ownsLoadedSourcePixels && loadedSourcePixels is not null)
                {
                    CaptureReusableFrameBuffers(loadedSourcePixels);
                }

                return;
            }

            VapourSynthPreviewFrameData? secondaryFrameData = null;
            if (request.CompareMode != PreviewCompareMode.Single
                && request.SecondaryOutputInfo is { } secondaryOutput
                && request.SecondaryFrameNumber is int secondaryFrameNumber)
            {
                if (_secondaryLastFrameData is { } cachedFrameB
                    && _secondarySourceFramePixels is { } cachedPixelsB
                    && cachedFrameB.OutputIndex == secondaryOutput.Index
                    && cachedFrameB.FrameNumber == secondaryFrameNumber
                    && _secondaryLastRenderOptions == request.SecondaryRenderOptions)
                {
                    secondaryFrameData = cachedFrameB;
                    loadedSecondarySourcePixels = cachedPixelsB;
                }
                else if (secondaryOutput.Index == request.OutputInfo.Index
                         && secondaryFrameNumber == request.FrameNumber
                         && request.SecondaryRenderOptions == request.RenderOptions)
                {
                    secondaryFrameData = frameData;
                    loadedSecondarySourcePixels = loadedSourcePixels;
                    _secondaryLastRenderOptions = request.SecondaryRenderOptions;
                }
                else
                {
                    secondaryFrameData = await _previewService.RenderFrameAsync(
                        secondaryOutput.Index,
                        secondaryFrameNumber,
                        request.SecondaryRenderOptions,
                        ConsumeReusableSecondarySourcePixels(),
                        _previewLifetimeToken);
                    helperRenderElapsed += secondaryFrameData.HelperRenderElapsed;
                    transportReadElapsed += secondaryFrameData.TransportReadElapsed;
                    loadedSecondarySourcePixels = secondaryFrameData.Pixels;
                    ownsLoadedSecondarySourcePixels = true;
                    _secondaryLastRenderOptions = request.SecondaryRenderOptions;
                }
            }

            displayPayload = CreateDisplayFramePayload(
                request.OutputInfo,
                frameData,
                loadedSourcePixels,
                frameData.Width,
                frameData.Height);
            composeElapsed += displayPayload.ComposeElapsed;

            if (secondaryFrameData is not null
                && request.SecondaryOutputInfo is { } activeSecondaryOutput
                && loadedSecondarySourcePixels is not null)
            {
                secondaryDisplayPayload = CreateSecondaryDisplayFramePayload(
                    request.OutputInfo,
                    activeSecondaryOutput,
                    secondaryFrameData,
                    loadedSecondarySourcePixels,
                    secondaryFrameData.Width,
                    secondaryFrameData.Height);
                composeElapsed += secondaryDisplayPayload.ComposeElapsed;
            }

            if (!IsActiveFrameRequest(request, scheduledRequest))
            {
                if (ownsLoadedSourcePixels && loadedSourcePixels is not null)
                {
                    CaptureReusableFrameBuffers(loadedSourcePixels, displayPayload.Pixels);
                }

                if (ownsLoadedSecondarySourcePixels && loadedSecondarySourcePixels is not null)
                {
                    CaptureReusableSecondaryFrameBuffers(loadedSecondarySourcePixels, secondaryDisplayPayload?.Pixels);
                }

                return;
            }

            surfaceUpdateElapsed = ApplyDisplayedFrame(
                request.OutputInfo,
                frameData,
                loadedSourcePixels,
                displayPayload,
                request.SecondaryOutputInfo,
                secondaryFrameData,
                loadedSecondarySourcePixels,
                secondaryDisplayPayload);
            if (_pendingNavigationFrame == request.FrameNumber)
            {
                _pendingNavigationFrame = null;
            }

            _renderDiagnostics.WriteFrameSuccess(
                request.OutputInfo.Index,
                request.FrameNumber,
                _isPlaying,
                ViewModel.IsCropPanelVisible,
                frameData.Width,
                frameData.Height,
                loadedSourcePixels.Length,
                _displayedFrameWidth,
                _displayedFrameHeight,
                _displayedFramePixels?.Length ?? 0,
                helperRenderElapsed,
                transportReadElapsed,
                composeElapsed,
                surfaceUpdateElapsed);
            QueueFrameStatusText(
                request.CompareMode != PreviewCompareMode.Single
                && request.SecondaryOutputInfo is { } readySecondary
                && secondaryFrameData is not null
                    ? ViewModel.Texts.VapourSynthPreviewCompareReadyStatus(
                        request.OutputInfo.Index,
                        request.FrameNumber,
                        readySecondary.Index,
                        secondaryFrameData.FrameNumber)
                    : ViewModel.Texts.VapourSynthPreviewReadyStatus(request.OutputInfo.Index, request.FrameNumber));
            SyncControls();
            InvalidateScopeCanvasIfVisible();
            if (ViewModel.IsPipetteEnabled && ViewModel.IsPipetteLocked && _pipetteLockedX >= 0 && _pipetteLockedY >= 0)
            {
                UpdatePipetteFromMemory(_pipetteLockedX, _pipetteLockedY, isLocked: true);
                _ = SampleNativePixelAsync(_pipetteLockedX, _pipetteLockedY, isLocked: true);
            }
        }
        catch (OperationCanceledException) when (_previewLifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _renderDiagnostics.WriteFrameFailure(
                request.OutputInfo.Index,
                request.FrameNumber,
                _isPlaying,
                ViewModel.IsCropPanelVisible,
                helperRenderElapsed,
                transportReadElapsed,
                composeElapsed,
                surfaceUpdateElapsed,
                ex);

            if (ownsLoadedSourcePixels && loadedSourcePixels is not null)
            {
                CaptureReusableFrameBuffers(loadedSourcePixels, displayPayload?.Pixels);
            }

            if (ownsLoadedSecondarySourcePixels && loadedSecondarySourcePixels is not null)
            {
                CaptureReusableSecondaryFrameBuffers(loadedSecondarySourcePixels, secondaryDisplayPayload?.Pixels);
            }

            if (IsActiveFrameRequest(request, scheduledRequest))
            {
                StopPlayback();
                SetStatusText(ViewModel.Texts.VapourSynthPreviewRenderFailedStatus(ex.Message));
            }

            if (IsActiveFrameRequest(request, scheduledRequest) && _pendingNavigationFrame == request.FrameNumber)
            {
                _pendingNavigationFrame = null;
            }
        }
    }

    private void RefreshDisplayedFrame(
        VapourSynthPreviewOutputInfo? outputInfo = null,
        VapourSynthPreviewFrameData? frameData = null,
        byte[]? sourcePixels = null)
    {
        outputInfo ??= _selectedOutputInfo;
        frameData ??= _lastFrameData;
        sourcePixels ??= _sourceFramePixels;

        if (outputInfo is null || frameData is null || sourcePixels is null)
        {
            return;
        }

        var displayPayload = CreateDisplayFramePayload(
            outputInfo,
            frameData,
            sourcePixels,
            frameData.Width,
            frameData.Height);

        DisplayFramePayload? secondaryDisplayPayload = null;
        if (ViewModel.IsCompareModeActive
            && _secondaryOutputInfo is { } secondaryOutput
            && _secondaryLastFrameData is { } secondaryFrameData
            && _secondarySourceFramePixels is { } secondarySourcePixels)
        {
            secondaryDisplayPayload = CreateSecondaryDisplayFramePayload(
                outputInfo,
                secondaryOutput,
                secondaryFrameData,
                secondarySourcePixels,
                secondaryFrameData.Width,
                secondaryFrameData.Height);
        }

        ApplyDisplayedFrame(
            outputInfo,
            frameData,
            sourcePixels,
            displayPayload,
            _secondaryOutputInfo,
            _secondaryLastFrameData,
            _secondarySourceFramePixels,
            secondaryDisplayPayload);
    }

    private DisplayFramePayload CreateDisplayFramePayload(
        VapourSynthPreviewOutputInfo outputInfo,
        VapourSynthPreviewFrameData frameData,
        byte[] sourcePixels,
        int sourceWidth,
        int sourceHeight)
    {
        var compositionRequest = CreateFrameCompositionRequest(outputInfo, sourcePixels, sourceWidth, sourceHeight);
        var composeStopwatch = Stopwatch.StartNew();
        var compositionResult = _frameComposer.Compose(compositionRequest, ConsumeReusableDisplayedPixels());
        composeStopwatch.Stop();
        var resolutionText = ViewModel.IsCropPanelVisible
            && (compositionResult.Width != frameData.Width || compositionResult.Height != frameData.Height)
            ? $"{frameData.Width} x {frameData.Height} -> {compositionResult.Width} x {compositionResult.Height}"
            : $"{frameData.Width} x {frameData.Height}";

        return new DisplayFramePayload(
            compositionResult.Pixels,
            compositionResult.Width,
            compositionResult.Height,
            resolutionText,
            composeStopwatch.Elapsed);
    }

    private DisplayFramePayload CreateSecondaryDisplayFramePayload(
        VapourSynthPreviewOutputInfo primaryOutputInfo,
        VapourSynthPreviewOutputInfo secondaryOutputInfo,
        VapourSynthPreviewFrameData secondaryFrameData,
        byte[] secondarySourcePixels,
        int sourceWidth,
        int sourceHeight)
    {
        var applyCropToSecondary = ViewModel.IsCropPanelVisible
            && PreviewCompareCoordinator.AreDimensionsEqual(primaryOutputInfo, secondaryOutputInfo);
        var cropSettings = applyCropToSecondary
            ? CreateCropSettings(GetOrCreateOutputState(primaryOutputInfo), cropEnabled: true)
            : new PreviewFrameCropSettings(
                IsEnabled: false,
                PreviewCropMode.Relative,
                new PreviewAbsoluteCrop(0, 0, Math.Max(1, sourceWidth), Math.Max(1, sourceHeight)),
                new PreviewRelativeCrop(0, 0, 0, 0));

        var composeStopwatch = Stopwatch.StartNew();
        var compositionResult = _frameComposer.Compose(
            new PreviewFrameCompositionRequest(secondarySourcePixels, sourceWidth, sourceHeight, cropSettings),
            ConsumeReusableSecondaryDisplayedPixels());
        composeStopwatch.Stop();

        return new DisplayFramePayload(
            compositionResult.Pixels,
            compositionResult.Width,
            compositionResult.Height,
            $"{secondaryFrameData.Width} x {secondaryFrameData.Height}",
            composeStopwatch.Elapsed);
    }

    private TimeSpan ApplyDisplayedFrame(
        VapourSynthPreviewOutputInfo outputInfo,
        VapourSynthPreviewFrameData frameData,
        byte[] sourcePixels,
        DisplayFramePayload displayPayload,
        VapourSynthPreviewOutputInfo? secondaryOutputInfo = null,
        VapourSynthPreviewFrameData? secondaryFrameData = null,
        byte[]? secondarySourcePixels = null,
        DisplayFramePayload? secondaryDisplayPayload = null)
    {
        _lastFrameData = frameData;
        CommitActiveFrameBuffers(
            frameData,
            sourcePixels,
            displayPayload.Width,
            displayPayload.Height,
            displayPayload.Pixels);

        if (ViewModel.IsCompareModeActive
            && secondaryOutputInfo is not null
            && secondaryFrameData is not null
            && secondarySourcePixels is not null
            && secondaryDisplayPayload is not null)
        {
            _secondaryLastFrameData = secondaryFrameData;
            CommitActiveSecondaryFrameBuffers(
                secondaryFrameData,
                secondarySourcePixels,
                secondaryDisplayPayload.Width,
                secondaryDisplayPayload.Height,
                secondaryDisplayPayload.Pixels);

            if (!_hasManualSizeAlignmentOverride)
            {
                ViewModel.SizeAlignment = PreviewCompareCoordinator.RecommendSizeAlignment(
                    displayPayload.Width,
                    displayPayload.Height,
                    secondaryDisplayPayload.Width,
                    secondaryDisplayPayload.Height);
            }
        }

        ViewModel.UpdateFrame(
            outputInfo,
            frameData,
            displayPayload.Width,
            displayPayload.Height,
            displayPayload.ResolutionText,
            secondaryOutputInfo,
            secondaryFrameData,
            secondaryDisplayPayload?.Width ?? 0,
            secondaryDisplayPayload?.Height ?? 0);

        var surfaceUpdateElapsed = UpdatePreviewDrawingSurface(
            displayPayload.Pixels,
            displayPayload.Width,
            displayPayload.Height,
            _previewFilter);

        if (ViewModel.IsCompareModeActive
            && secondaryDisplayPayload is not null)
        {
            surfaceUpdateElapsed += UpdateSecondaryPreviewDrawingSurface(
                secondaryDisplayPayload.Pixels,
                secondaryDisplayPayload.Width,
                secondaryDisplayPayload.Height,
                _previewFilter);

            if (ViewModel.IsDifferenceMode)
            {
                surfaceUpdateElapsed += RedrawDifferenceSurface();
            }
        }

        UpdateCompareVisualPresentation();
        UpdatePreviewImageVisualSize();
        UpdateWipeClipAndOverlay();
        SaveCurrentOutputState();
        return surfaceUpdateElapsed;
    }

    private PreviewFrameCompositionRequest CreateFrameCompositionRequest(
        VapourSynthPreviewOutputInfo outputInfo,
        byte[] sourcePixels,
        int sourceWidth,
        int sourceHeight)
    {
        var outputState = GetOrCreateOutputState(outputInfo);
        var crop = CreateCropSettings(outputState, ViewModel.IsCropPanelVisible);
        if (crop.IsEnabled && outputState.AutoCropRepair?.Edges is [var left, var top, var right, var bottom])
        {
            crop = crop with { Mode = PreviewCropMode.Relative, Relative = new(left, top, right, bottom) };
        }
        return new PreviewFrameCompositionRequest(
            sourcePixels,
            sourceWidth,
            sourceHeight,
            crop);
    }

    private bool IsActiveFrameRequest(
        PreviewFrameRequest request,
        LatestRequestScheduler<PreviewFrameRequest>.ScheduledRequest scheduledRequest)
    {
        return !_previewLifetimeToken.IsCancellationRequested
            && scheduledRequest.MatchesLatestRequest
            && _selectedOutputInfo is { } selectedOutput
            && selectedOutput.Index == request.OutputInfo.Index
            && request.SessionRevision == _previewSessionRevision
            && (request.CompareMode == PreviewCompareMode.Single
                || (ViewModel.IsCompareModeActive
                    && request.SecondaryOutputInfo is { } secondaryOutput
                    && _secondaryOutputInfo is { } activeSecondary
                    && activeSecondary.Index == secondaryOutput.Index));
    }

    private void SyncControls()
    {
        _isInternalControlUpdate = true;
        DetachControlEvents();

        try
        {
            var activeFrame = _isFrameSliderInteracting
                ? (int)Math.Round(FrameSlider.Value)
                : ViewModel.CurrentFrame;

            OutputSelectorComboBox.SelectedItem = ViewModel.SelectedOutput;
            CompareOutputAComboBox.SelectedItem = ViewModel.SelectedOutput;
            CompareOutputBComboBox.SelectedItem = ViewModel.SelectedSecondaryOutput;
            CompareModeComboBox.SelectedItem = ViewModel.SelectedCompareModeOption;
            DifferenceStyleComboBox.SelectedItem = ViewModel.SelectedDifferenceStyleOption;
            DifferenceGainComboBox.SelectedItem = ViewModel.SelectedDifferenceGainOption;
            FlickerIntervalComboBox.SelectedItem = ViewModel.SelectedFlickerIntervalOption;
            CompareSizeAlignmentComboBox.SelectedItem = ViewModel.SelectedSizeAlignmentOption;
            SecondaryFrameOffsetBox.Value = ViewModel.SecondaryFrameOffset;
            SubpixelOffsetXBox.Value = ViewModel.SubpixelOffsetX;
            SubpixelOffsetYBox.Value = ViewModel.SubpixelOffsetY;
            PipetteToggleButton.IsChecked = ViewModel.IsPipetteEnabled;
            UpdateCompareModeSelection();
            FrameNumberBox.Maximum = Math.Max(0, ViewModel.TotalFrames - 1);
            FrameNumberBox.Value = activeFrame;
            FrameSlider.Maximum = ViewModel.FrameSliderMaximum;
            if (!_isFrameSliderInteracting)
            {
                FrameSlider.Value = ViewModel.CurrentFrame;
            }

            UpdateActiveChapter(activeFrame);
            RedrawChapterMarkers();
            StepSizeBox.Value = ViewModel.StepSize;
            ZoomRatioBox.Value = Math.Round(ViewModel.ZoomRatio * 100);
            ZoomRatioBox.Visibility = ViewModel.CustomZoomVisibility;
            FramePropsPanel.Visibility = ViewModel.IsFramePropsVisible ? Visibility.Visible : Visibility.Collapsed;
            UpdateInspectorColumnWidth();
            FramePropsToggleButton.IsChecked = ViewModel.IsFramePropsVisible;
            CropToggleButton.IsChecked = ViewModel.IsCropPanelVisible;
            TimelineModeComboBox.SelectedItem = ViewModel.SelectedTimelineMode;
            TimeStepSecondsBox.Value = ViewModel.TimeStepSeconds;
            CropModeComboBox.SelectedItem = ViewModel.SelectedCropMode;
            ChapterSelectorComboBox.SelectedItem = _activeChapterIndex >= 0 && _activeChapterIndex < ViewModel.Chapters.Count
                ? ViewModel.Chapters[_activeChapterIndex]
                : null;
            Title = ViewModel.WindowTitle;

            if (_selectedOutputInfo is not null)
            {
                ApplyCropControlsFromState(GetOrCreateOutputState(_selectedOutputInfo), _selectedOutputInfo);
            }
            else
            {
                ResetCropControls();
            }
        }
        finally
        {
            AttachControlEvents();
            _isInternalControlUpdate = false;
        }
    }

    private void ApplyTheme(AppThemePreference preference)
    {
        RootLayout.RequestedTheme = preference switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private async void ReloadPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(ReloadPreviewButton_Click),
            async () =>
            {
                var loaded = await LoadSessionAsync(preserveCurrentFrame: true);
                if (!loaded && !_isClosed)
                {
                    Close();
                }
            });
    }

    private async void SaveSnapshotButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        await RunWindowEventHandlerAsync(
            nameof(SaveSnapshotButton_Click),
            SaveCurrentSnapshotAsync,
            ViewModel.Texts.VapourSynthPreviewSnapshotSaveFailedStatus);
    }

    private async void SaveSnapshotMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(SaveSnapshotMenuItem_Click),
            SaveCurrentSnapshotAsync,
            ViewModel.Texts.VapourSynthPreviewSnapshotSaveFailedStatus);
    }

    private async void SaveAllOutputsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(SaveAllOutputsButton_Click),
            SaveAllOutputsAtCurrentFrameAsync,
            ViewModel.Texts.VapourSynthPreviewAllSnapshotsFailedStatus);
    }

    private async Task SaveCurrentSnapshotAsync()
    {
        if (!TryGetActiveSnapshotPixels(out var pixels, out var width, out var height))
        {
            return;
        }

        var snapshotAlphaMode = ViewModel.IsWipeMode ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore;
        if (ViewModel.SilentSnapshotEnabled)
        {
            try
            {
                var snapshotPath = ResolveSilentSnapshotPath();
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                await SavePixelsAsPngAsync(snapshotPath, pixels, width, height, snapshotAlphaMode);
                SetStatusText(ViewModel.Texts.VapourSynthPreviewSnapshotSavedStatus(snapshotPath));
                return;
            }
            catch (Exception ex)
            {
                LogWindowOperationFailure("SaveSilentSnapshot", ex);
                SetStatusText(ViewModel.Texts.VapourSynthPreviewSnapshotTemplateFailedStatus(GetWindowOperationErrorDetail(ex)));
            }
        }

        var (pickSucceeded, file) = await TryRunWindowOperationAsync(
            "PickSnapshotSaveFile",
            () => Task.FromResult(
                WindowInteractionHelper.PickSaveFilePath(
                    GetWindowHandle(),
                    ViewModel.Texts.SaveButton,
                    ResolvePreviewDialogCurrentPath(),
                    BuildShortcutSnapshotFileName(withExtension: false),
                    ".png",
                    new NativeFileDialogHelper.FileDialogFilter(
                        ViewModel.Texts.VapourSynthPreviewSnapshotFileTypeDescription,
                        "*.png"))),
            ViewModel.Texts.VapourSynthPreviewSnapshotSaveFailedStatus);
        if (!pickSucceeded || file is null)
        {
            return;
        }

        var snapshotSavePath = Path.ChangeExtension(file.Value.Path, ".png");

        var saved = await TryRunWindowActionAsync(
            "SaveSnapshotFile",
            () => SavePixelsAsPngAsync(snapshotSavePath, pixels, width, height, snapshotAlphaMode),
            ViewModel.Texts.VapourSynthPreviewSnapshotSaveFailedStatus);
        if (!saved)
        {
            return;
        }

        SetStatusText(ViewModel.Texts.VapourSynthPreviewSnapshotSavedStatus(snapshotSavePath));
    }

    private async Task QuickSaveSnapshotAsync()
    {
        if (!TryGetActiveSnapshotPixels(out var pixels, out var width, out var height))
        {
            return;
        }

        var snapshotAlphaMode = ViewModel.IsWipeMode ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore;
        var (pickSucceeded, folderPath) = await TryRunWindowOperationAsync(
            "PickQuickSnapshotFolder",
            () => Task.FromResult(
                WindowInteractionHelper.PickFolderPath(
                    GetWindowHandle(),
                    ViewModel.Texts.VapourSynthPreviewSaveFrameButton,
                    ResolvePreviewDialogCurrentPath())),
            ViewModel.Texts.VapourSynthPreviewSnapshotSaveFailedStatus);
        if (!pickSucceeded || string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string? snapshotPath = null;
        var saved = await TryRunWindowActionAsync(
            "SaveQuickSnapshot",
            async () =>
            {
                snapshotPath = Path.Combine(folderPath, BuildShortcutSnapshotFileName(withExtension: true));
                await SavePixelsAsPngAsync(snapshotPath, pixels, width, height, snapshotAlphaMode);
            },
            ViewModel.Texts.VapourSynthPreviewSnapshotSaveFailedStatus);
        if (!saved || string.IsNullOrWhiteSpace(snapshotPath))
        {
            return;
        }

        SetStatusText(ViewModel.Texts.VapourSynthPreviewSnapshotSavedStatus(snapshotPath));
    }

    private async void CopyFrameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetActiveSnapshotPixels(out var pixels, out var width, out var height))
        {
            return;
        }

        try
        {
            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                ViewModel.IsWipeMode ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore,
                (uint)width,
                (uint)height,
                96,
                96,
                pixels);
            await encoder.FlushAsync();
            stream.Seek(0);

            var dataPackage = new DataPackage();
            dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            if (!TrySetClipboardContent(dataPackage, "frame", out var errorDetail))
            {
                SetStatusText(ViewModel.Texts.VapourSynthPreviewFrameCopyFailedStatus(errorDetail));
                return;
            }

            SetStatusText(ViewModel.Texts.VapourSynthPreviewFrameCopiedStatus);
        }
        catch (Exception ex)
        {
            LogWindowOperationFailure("PrepareFrameImage", ex);
            SetStatusText(ViewModel.Texts.VapourSynthPreviewFrameCopyFailedStatus(GetWindowOperationErrorDetail(ex)));
        }
    }

    private bool TryGetActiveSnapshotPixels(out byte[] pixels, out int width, out int height)
    {
        if (ViewModel.IsCompareModeActive
            && ViewModel.IsActiveFlipShowingB
            && _secondaryDisplayedFramePixels is not null
            && _secondaryDisplayedFrameWidth > 0
            && _secondaryDisplayedFrameHeight > 0)
        {
            pixels = _secondaryDisplayedFramePixels;
            width = _secondaryDisplayedFrameWidth;
            height = _secondaryDisplayedFrameHeight;
            return true;
        }

        if (ViewModel.IsCompareModeActive
            && !ViewModel.IsFlickerMode
            && _previewCanvasDevice is not null
            && _cachedBitmapA is not null
            && _cachedBitmapB is not null)
        {
            var composite = PreviewDisplayFilters.RenderCompositeSnapshotBytes(
                _previewCanvasDevice,
                ViewModel.CompareMode,
                _cachedBitmapA,
                _cachedBitmapB,
                _previewFilter,
                ViewModel.DifferenceGain,
                ViewModel.SizeAlignment,
                ViewModel.SplitRatio,
                GetCanvasImageInterpolation(),
                _lastFrameData?.Properties,
                _secondaryLastFrameData?.Properties,
                ViewModel.DifferenceStyle);
            pixels = composite.Pixels;
            width = composite.Width;
            height = composite.Height;
            return width > 0 && height > 0 && pixels.Length > 0;
        }

        if (_displayedFramePixels is not null && _displayedFrameWidth > 0 && _displayedFrameHeight > 0)
        {
            pixels = _displayedFramePixels;
            width = _displayedFrameWidth;
            height = _displayedFrameHeight;
            return true;
        }

        pixels = [];
        width = 0;
        height = 0;
        return false;
    }

    private void ReturnToEditorButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void FramePropsToggleButton_Checked(object sender, RoutedEventArgs e)
    {
        UpdateFramePropsVisibility(true);
        InvalidateScopeCanvasIfVisible();
    }

    private void FramePropsToggleButton_Unchecked(object sender, RoutedEventArgs e)
    {
        UpdateFramePropsVisibility(false);
    }

    private async void CropToggleButton_Checked(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(CropToggleButton_Checked),
            () => UpdateCropPanelVisibilityAsync(true));
    }

    private async void CropToggleButton_Unchecked(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(CropToggleButton_Unchecked),
            () => UpdateCropPanelVisibilityAsync(false));
    }

    private async void AdvancedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(AdvancedSettingsButton_Click),
            ShowAdvancedSettingsAsync,
            ViewModel.Texts.VapourSynthPreviewAdvancedSettingsFailedStatus);
    }

    private async void OutputSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(OutputSelectorComboBox_SelectionChanged),
            async () =>
            {
                if (_isInternalControlUpdate
                    || OutputSelectorComboBox.SelectedItem is not VapourSynthPreviewOutputOption option)
                {
                    return;
                }

                await SelectOutputAsync(option.Info);
            });
    }

    private async void FrameNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        await RunWindowEventHandlerAsync(
            nameof(FrameNumberBox_ValueChanged),
            async () =>
            {
                if (_isInternalControlUpdate || double.IsNaN(sender.Value))
                {
                    return;
                }

                var requestedFrame = (int)Math.Round(sender.Value);
                if (requestedFrame == ViewModel.CurrentFrame)
                {
                    return;
                }

                await RequestFrameAsync(requestedFrame);
            });
    }

    private async void FrameNumberBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(FrameNumberBox_LostFocus),
            async () =>
            {
                if (_isInternalControlUpdate || double.IsNaN(FrameNumberBox.Value))
                {
                    return;
                }

                await RequestFrameAsync((int)Math.Round(FrameNumberBox.Value));
            });
    }

    private void ZoomModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInternalControlUpdate)
        {
            return;
        }

        SaveCurrentOutputState();
        SyncControls();
    }

    private void ZoomRatioBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInternalControlUpdate || double.IsNaN(sender.Value))
        {
            return;
        }

        ViewModel.ZoomRatio = Math.Max(5, sender.Value) / 100.0;
        SaveCurrentOutputState();
    }

    private void TimelineModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInternalControlUpdate)
        {
            return;
        }

        SyncControls();
    }

    private void TimeStepSecondsBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInternalControlUpdate || double.IsNaN(sender.Value))
        {
            return;
        }

        ViewModel.TimeStepSeconds = Math.Max(0.001, sender.Value);
    }

    private async void TimeStepBackButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(TimeStepBackButton_Click),
            () => StepByTimeAsync(-ViewModel.TimeStepSeconds));
    }

    private async void TimeStepForwardButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(TimeStepForwardButton_Click),
            () => StepByTimeAsync(ViewModel.TimeStepSeconds));
    }

    private void CropModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInternalControlUpdate || _selectedOutputInfo is null)
        {
            return;
        }

        var outputState = GetOrCreateOutputState(_selectedOutputInfo);
        outputState.CropMode = ViewModel.SelectedCropMode?.Value ?? "relative";
        EnsureCropStatesWithinBounds(outputState, _selectedOutputInfo.Width, _selectedOutputInfo.Height);
        ViewModel.CropZoomPercentage = GetActiveCropZoomPercentage(outputState);
        SyncControls();
        RefreshDisplayedFrame();
    }

    private async void CropLeftBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        await RunWindowEventHandlerAsync(
            nameof(CropLeftBox_ValueChanged),
            () => UpdateCropValueAsync(CropField.Left, sender));
    }

    private async void CropTopBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        await RunWindowEventHandlerAsync(
            nameof(CropTopBox_ValueChanged),
            () => UpdateCropValueAsync(CropField.Top, sender));
    }

    private async void CropWidthBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        await RunWindowEventHandlerAsync(
            nameof(CropWidthBox_ValueChanged),
            () => UpdateCropValueAsync(CropField.Width, sender));
    }

    private async void CropHeightBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        await RunWindowEventHandlerAsync(
            nameof(CropHeightBox_ValueChanged),
            () => UpdateCropValueAsync(CropField.Height, sender));
    }

    private async void CropRightBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        await RunWindowEventHandlerAsync(
            nameof(CropRightBox_ValueChanged),
            () => UpdateCropValueAsync(CropField.Right, sender));
    }

    private async void CropBottomBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        await RunWindowEventHandlerAsync(
            nameof(CropBottomBox_ValueChanged),
            () => UpdateCropValueAsync(CropField.Bottom, sender));
    }

    private void CropZoomBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInternalControlUpdate || _selectedOutputInfo is null || double.IsNaN(sender.Value))
        {
            return;
        }

        var outputState = GetOrCreateOutputState(_selectedOutputInfo);
        SetActiveCropZoomPercentage(outputState, Math.Clamp(sender.Value, 10, 800));
        ViewModel.CropZoomPercentage = GetActiveCropZoomPercentage(outputState);
        SaveCurrentOutputState();
    }

    private void CopyCropCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOutputInfo is null)
        {
            return;
        }

        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(BuildCropCommandSnippet(useSnippetPlaceholder: false));
            if (!TrySetClipboardContent(dataPackage, "crop-command", out var errorDetail))
            {
                SetStatusText(ViewModel.Texts.VapourSynthPreviewCropCommandCopyFailedStatus(errorDetail));
                return;
            }

            SetStatusText(ViewModel.Texts.VapourSynthPreviewCropCommandCopiedStatus);
        }
        catch (Exception ex)
        {
            LogWindowOperationFailure("PrepareCropCommand", ex);
            SetStatusText(ViewModel.Texts.VapourSynthPreviewCropCommandCopyFailedStatus(GetWindowOperationErrorDetail(ex)));
        }
    }

    private void PreviewViewportHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshDisplayScale();
        ViewModel.UpdateViewport(
            Math.Max(0, e.NewSize.Width - 16),
            Math.Max(0, e.NewSize.Height - 16));
        UpdateWipeClipAndOverlay();
    }

    private void PreviewScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateWipeClipAndOverlay();
    }

    private void CompareScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isSyncingCompareScroll || !ViewModel.IsSideBySideMode)
        {
            return;
        }

        if (sender is not ScrollViewer source)
        {
            return;
        }

        var target = ReferenceEquals(source, LeftCompareScrollViewer)
            ? RightCompareScrollViewer
            : LeftCompareScrollViewer;
        SyncCompareScrollViewer(source, target);
    }

    private void SyncCompareScrollViewer(ScrollViewer source, ScrollViewer target)
    {
        _isSyncingCompareScroll = true;
        try
        {
            var ratioX = source.ScrollableWidth > 0 ? source.HorizontalOffset / source.ScrollableWidth : 0.0;
            var ratioY = source.ScrollableHeight > 0 ? source.VerticalOffset / source.ScrollableHeight : 0.0;
            var targetX = Math.Clamp(ratioX * target.ScrollableWidth, 0, target.ScrollableWidth);
            var targetY = Math.Clamp(ratioY * target.ScrollableHeight, 0, target.ScrollableHeight);
            target.ChangeView(targetX, targetY, null, true);
        }
        finally
        {
            _isSyncingCompareScroll = false;
        }
    }

    private void WipeDividerHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsWipeMode)
        {
            return;
        }

        var point = e.GetCurrentPoint(PreviewViewportHost);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isWipeDividerDragging = true;
        _wipeDividerPointerId = point.PointerId;
        WipeDividerHandle.CapturePointer(e.Pointer);
        UpdateWipeSplitFromPointer(point.Position.X);
        QueueFocusPreviewSurface();
        e.Handled = true;
    }

    private void WipeDividerHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isWipeDividerDragging || e.Pointer.PointerId != _wipeDividerPointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(PreviewViewportHost);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ReleaseWipeDividerCapture();
            return;
        }

        UpdateWipeSplitFromPointer(point.Position.X);
        e.Handled = true;
    }

    private void WipeDividerHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isWipeDividerDragging && e.Pointer.PointerId == _wipeDividerPointerId)
        {
            ReleaseWipeDividerCapture();
            e.Handled = true;
        }
    }

    private void WipeDividerHandle_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isWipeDividerDragging && e.Pointer.PointerId == _wipeDividerPointerId)
        {
            ReleaseWipeDividerCapture();
            e.Handled = true;
        }
    }

    private void WipeDividerHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isWipeDividerDragging && e.Pointer.PointerId == _wipeDividerPointerId)
        {
            ReleaseWipeDividerCapture();
        }
    }

    private void WipeDividerHandle_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!ViewModel.IsWipeMode)
        {
            return;
        }

        CenterWipeSplitInViewport();
        e.Handled = true;
    }

    private void ReleaseWipeDividerCapture()
    {
        _isWipeDividerDragging = false;
        _wipeDividerPointerId = 0;
        WipeDividerHandle.ReleasePointerCaptures();
    }

    private void UpdateWipeSplitFromPointer(double pointerViewportX)
    {
        var imageWidth = ViewModel.PreviewImageWidth;
        if (imageWidth <= 0)
        {
            return;
        }

        var imageLeftInViewport = GetSinglePreviewImageLeftInViewport();
        ViewModel.SplitRatio = PreviewCompareCoordinator.ComputeSplitRatioFromViewportX(
            pointerViewportX,
            imageLeftInViewport,
            PreviewScrollViewer.HorizontalOffset,
            imageWidth);
        UpdateWipeClipAndOverlay();
    }

    private void CenterWipeSplitInViewport()
    {
        var imageWidth = ViewModel.PreviewImageWidth;
        if (imageWidth <= 0)
        {
            ViewModel.SplitRatio = 0.5;
            UpdateWipeClipAndOverlay();
            return;
        }

        var viewportWidth = PreviewScrollViewer.ViewportWidth > 0
            ? PreviewScrollViewer.ViewportWidth
            : PreviewViewportHost.ActualWidth;
        ViewModel.SplitRatio = PreviewCompareCoordinator.ComputeCenteredViewportSplitRatio(
            viewportWidth,
            GetSinglePreviewImageLeftInViewport(),
            PreviewScrollViewer.HorizontalOffset,
            imageWidth);
        UpdateWipeClipAndOverlay();
    }

    private double GetSinglePreviewImageLeftInViewport()
    {
        var viewportWidth = PreviewScrollViewer.ViewportWidth > 0
            ? PreviewScrollViewer.ViewportWidth
            : PreviewViewportHost.ActualWidth;
        return Math.Max(0.0, (viewportWidth - ViewModel.PreviewImageWidth) / 2.0);
    }

    private void PreviewScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled
            || !IsControlKeyPressed()
            || _displayedFrameWidth <= 0
            || _displayedFrameHeight <= 0
            || sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var currentPoint = e.GetCurrentPoint(scrollViewer);
        var wheelDelta = currentPoint.Properties.MouseWheelDelta;
        if (wheelDelta == 0)
        {
            return;
        }

        var isSecondaryPane = ReferenceEquals(scrollViewer, RightCompareScrollViewer);
        var previousWidth = isSecondaryPane ? ViewModel.SecondaryPreviewImageWidth : ViewModel.PreviewImageWidth;
        var previousHeight = isSecondaryPane ? ViewModel.SecondaryPreviewImageHeight : ViewModel.PreviewImageHeight;
        var pointerPosition = currentPoint.Position;
        var anchorXRatio = previousWidth > 0
            ? Math.Clamp((scrollViewer.HorizontalOffset + pointerPosition.X) / previousWidth, 0.0, 1.0)
            : 0.5;
        var anchorYRatio = previousHeight > 0
            ? Math.Clamp((scrollViewer.VerticalOffset + pointerPosition.Y) / previousHeight, 0.0, 1.0)
            : 0.5;

        var factor = Math.Pow(1.1, wheelDelta / 120.0);
        var targetZoomRatio = Math.Clamp(GetCurrentEffectiveZoomRatio() * factor, 0.05, 16.0);
        ApplyCustomZoom(targetZoomRatio);
        RestorePreviewScrollAnchor(scrollViewer, anchorXRatio, anchorYRatio, pointerPosition.X, pointerPosition.Y);
        QueueFocusPreviewSurface();
        e.Handled = true;
    }

    private void PreviewScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.Handled)
        {
            QueueFocusPreviewSurface();
        }

        if (ViewModel.IsPipetteEnabled && sender is ScrollViewer pipetteScrollViewer)
        {
            var ptProps = e.GetCurrentPoint(pipetteScrollViewer).Properties;
            if (ptProps.IsRightButtonPressed)
            {
                ViewModel.IsPipetteLocked = false;
                _pipetteLockedX = -1;
                _pipetteLockedY = -1;
                e.Handled = true;
                return;
            }
            if (ptProps.IsLeftButtonPressed && TryResolveSourcePixelCoordinates(pipetteScrollViewer, e, out var px, out var py))
            {
                ViewModel.IsPipetteLocked = true;
                _pipetteLockedX = px;
                _pipetteLockedY = py;
                UpdatePipetteFromMemory(px, py, isLocked: true);
                _ = SampleNativePixelAsync(px, py, isLocked: true);
                e.Handled = true;
                return;
            }
        }

        if (e.Handled || sender is not ScrollViewer scrollViewer || !CanPanPreview(scrollViewer))
        {
            return;
        }

        var currentPoint = e.GetCurrentPoint(scrollViewer);
        if (!currentPoint.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPreviewPanActive = true;
        _activePanScrollViewer = scrollViewer;
        _previewPanPointerId = currentPoint.PointerId;
        _previewPanOrigin = currentPoint.Position;
        _previewPanStartHorizontalOffset = scrollViewer.HorizontalOffset;
        _previewPanStartVerticalOffset = scrollViewer.VerticalOffset;
        scrollViewer.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PreviewScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsPipetteEnabled && !_isPreviewPanActive && !ViewModel.IsPipetteLocked && sender is ScrollViewer hoverScrollViewer)
        {
            if (TryResolveSourcePixelCoordinates(hoverScrollViewer, e, out var px, out var py))
            {
                UpdatePipetteFromMemory(px, py, isLocked: false);
            }
        }

        if (!_isPreviewPanActive
            || e.Pointer.PointerId != _previewPanPointerId
            || _activePanScrollViewer is null)
        {
            return;
        }

        var scrollViewer = _activePanScrollViewer;
        var currentPoint = e.GetCurrentPoint(scrollViewer);
        if (!currentPoint.Properties.IsLeftButtonPressed)
        {
            ReleasePreviewPanCapture();
            return;
        }

        var deltaX = currentPoint.Position.X - _previewPanOrigin.X;
        var deltaY = currentPoint.Position.Y - _previewPanOrigin.Y;
        var targetHorizontalOffset = Math.Clamp(
            _previewPanStartHorizontalOffset - deltaX,
            0,
            scrollViewer.ScrollableWidth);
        var targetVerticalOffset = Math.Clamp(
            _previewPanStartVerticalOffset - deltaY,
            0,
            scrollViewer.ScrollableHeight);

        SetPreviewScrollOffset(scrollViewer, targetHorizontalOffset, targetVerticalOffset);
        e.Handled = true;
    }

    private void PreviewScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isPreviewPanActive && e.Pointer.PointerId == _previewPanPointerId)
        {
            ReleasePreviewPanCapture();
            e.Handled = true;
        }
    }

    private void PreviewScrollViewer_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isPreviewPanActive && e.Pointer.PointerId == _previewPanPointerId)
        {
            ReleasePreviewPanCapture();
            e.Handled = true;
        }
    }

    private void PreviewScrollViewer_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isPreviewPanActive && e.Pointer.PointerId == _previewPanPointerId)
        {
            ReleasePreviewPanCapture();
        }
    }

    private async void FirstFrameButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(FirstFrameButton_Click),
            () => RequestFrameAsync(0));
    }

    private async void StepBackButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(StepBackButton_Click),
            () => RequestFrameAsync(Math.Max(0, ViewModel.CurrentFrame - ViewModel.StepSize)));
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            StopPlayback(updateStatus: true);
            return;
        }

        if (_selectedOutputInfo is null)
        {
            return;
        }

        var fps = _selectedOutputInfo.FpsNumerator > 0 && _selectedOutputInfo.FpsDenominator > 0
            ? _selectedOutputInfo.FpsNumerator / (double)_selectedOutputInfo.FpsDenominator
            : 24.0;
        _playbackTimer.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1.0, fps));
        _isPlaying = true;
        PlayPauseIcon.Symbol = Symbol.Pause;
        SetStatusText(ViewModel.Texts.VapourSynthPreviewPlaybackStatus);
        _playbackTimer.Start();
    }

    private async void StepForwardButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(StepForwardButton_Click),
            () => RequestFrameAsync(Math.Min(Math.Max(0, ViewModel.TotalFrames - 1), ViewModel.CurrentFrame + ViewModel.StepSize)));
    }

    private async void LastFrameButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(LastFrameButton_Click),
            () => RequestFrameAsync(Math.Max(0, ViewModel.TotalFrames - 1)));
    }

    private void StepSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInternalControlUpdate || double.IsNaN(sender.Value))
        {
            return;
        }

        ViewModel.StepSize = (int)Math.Max(1, Math.Round(sender.Value));
    }

    private async void FrameSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(FrameSlider_ValueChanged),
            async () =>
            {
                if (_isInternalControlUpdate)
                {
                    return;
                }

                var frameNumber = (int)Math.Round(e.NewValue);
                if (_isFrameSliderInteracting)
                {
                    UpdateFrameNumberDisplay(frameNumber);
                    return;
                }

                UpdateActiveChapter(frameNumber);
                await RequestFrameAsync(frameNumber);
            });
    }

    private async void FrameSliderHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(FrameSliderHost_PointerPressed),
            async () =>
            {
                if (_selectedOutputInfo is null || ViewModel.Chapters.Count == 0)
                {
                    return;
                }

                var point = e.GetCurrentPoint(FrameSliderHost).Position;
                var track = MeasureSliderTrackBounds();
                if (track.width <= 0)
                {
                    return;
                }

                var clickedFrame = (int)Math.Round(Math.Clamp((point.X - track.offset) / track.width, 0, 1) * ViewModel.FrameSliderMaximum);
                var nearest = ViewModel.Chapters
                    .Select((chapter, index) => new
                    {
                        Chapter = chapter,
                        Index = index,
                        Frame = TimecodeToFrame(chapter.Timecode, _selectedOutputInfo)
                    })
                    .OrderBy(item => Math.Abs(item.Frame - clickedFrame))
                    .FirstOrDefault();

                if (nearest is null)
                {
                    return;
                }

                var toleranceFrames = Math.Max(3, (int)Math.Round(GetOutputFps(_selectedOutputInfo) * 0.5));
                if (Math.Abs(nearest.Frame - clickedFrame) > toleranceFrames)
                {
                    return;
                }

                _activeChapterIndex = nearest.Index;
                RedrawChapterMarkers();
                e.Handled = true;
                await RequestFrameAsync(nearest.Frame);
            });
    }

    private void ChapterMarkerCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawChapterMarkers();
    }

    private async void ChapterSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(ChapterSelectorComboBox_SelectionChanged),
            async () =>
            {
                if (_isInternalControlUpdate
                    || _selectedOutputInfo is null
                    || ChapterSelectorComboBox.SelectedItem is not VapourSynthPreviewChapterOption chapter)
                {
                    return;
                }

                var index = ViewModel.Chapters.IndexOf(chapter);
                if (index < 0)
                {
                    return;
                }

                _activeChapterIndex = index;
                RedrawChapterMarkers();
                await RequestFrameAsync(TimecodeToFrame(chapter.Timecode, _selectedOutputInfo));
            });
    }

    private async void ImportChapterButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(ImportChapterButton_Click),
            async () =>
            {
                NativeFileDialogHelper.FileDialogResult? PickChapterImportFile()
                {
                    var path = WindowInteractionHelper.PickOpenFilePath(
                        GetWindowHandle(),
                        ViewModel.Texts.ImportButton,
                        ResolvePreviewDialogCurrentPath(),
                        new NativeFileDialogHelper.FileDialogFilter(
                            ViewModel.Texts.VapourSynthPreviewChapterFileTypeDescription,
                            "*.txt;*.xml"),
                        new NativeFileDialogHelper.FileDialogFilter(
                            ViewModel.Texts.VapourSynthPreviewOgmChapterFileTypeDescription,
                            "*.txt"),
                        new NativeFileDialogHelper.FileDialogFilter(
                            ViewModel.Texts.VapourSynthPreviewXmlChapterFileTypeDescription,
                            "*.xml"));
                    return string.IsNullOrWhiteSpace(path)
                        ? null
                        : new NativeFileDialogHelper.FileDialogResult(path, 1);
                }

                var (pickSucceeded, file) = await TryRunWindowOperationAsync(
                    "PickChapterImportFile",
                    () => Task.FromResult(PickChapterImportFile()),
                    ViewModel.Texts.VapourSynthPreviewChapterImportFailedStatus);
                if (!pickSucceeded || file is null)
                {
                    return;
                }

                try
                {
                    var chapters = await LoadChapterFileAsync(file.Value.Path);
                    ViewModel.ReplaceChapters(BuildChapterOptions(chapters));
                    _activeChapterIndex = -1;
                    UpdateChapterButtons();
                    RedrawChapterMarkers();
                    SetStatusText(ViewModel.Chapters.Count > 0
                        ? ViewModel.Texts.VapourSynthPreviewChaptersImportedStatus(ViewModel.Chapters.Count)
                        : ViewModel.Texts.VapourSynthPreviewNoChaptersFoundStatus);
                }
                catch (Exception ex)
                {
                    LogWindowOperationFailure("LoadChapterFile", ex);
                    SetStatusText(ViewModel.Texts.VapourSynthPreviewChapterImportFailedStatus(GetWindowOperationErrorDetail(ex)));
                }
            },
            ViewModel.Texts.VapourSynthPreviewChapterImportFailedStatus);
    }

    private async void ExportChapterButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(ExportChapterButton_Click),
            async () =>
            {
                if (ViewModel.Chapters.Count == 0)
                {
                    return;
                }

                var (pickSucceeded, file) = await TryRunWindowOperationAsync(
                    "PickChapterExportFile",
                    () => Task.FromResult(
                        WindowInteractionHelper.PickSaveFilePath(
                            GetWindowHandle(),
                            ViewModel.Texts.ExportButton,
                            ResolvePreviewDialogCurrentPath(),
                            "chapters",
                            ".txt",
                            new NativeFileDialogHelper.FileDialogFilter(
                                ViewModel.Texts.VapourSynthPreviewOgmChapterFileTypeDescription,
                                "*.txt"),
                            new NativeFileDialogHelper.FileDialogFilter(
                                ViewModel.Texts.VapourSynthPreviewXmlChapterFileTypeDescription,
                                "*.xml"))),
                    ViewModel.Texts.VapourSynthPreviewChapterExportFailedStatus);
                if (!pickSucceeded || file is null)
                {
                    return;
                }

                var selectedPath = file.Value.Path;
                var targetExtension = ResolveChapterExportExtension(file.Value.SelectedFilterIndex);
                selectedPath = Path.ChangeExtension(selectedPath, targetExtension);

                try
                {
                    var chapters = GetChapterEntries();
                    if (string.Equals(targetExtension, ".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        await File.WriteAllTextAsync(selectedPath, BuildMatroskaChaptersXml(chapters));
                    }
                    else
                    {
                        await File.WriteAllLinesAsync(selectedPath, BuildOgmChapterLines(chapters));
                    }

                    SetStatusText(ViewModel.Texts.VapourSynthPreviewChaptersExportedStatus(chapters.Count, selectedPath));
                }
                catch (Exception ex)
                {
                    LogWindowOperationFailure("ExportChapterFile", ex);
                    SetStatusText(ViewModel.Texts.VapourSynthPreviewChapterExportFailedStatus(GetWindowOperationErrorDetail(ex)));
                }
            },
            ViewModel.Texts.VapourSynthPreviewChapterExportFailedStatus);
    }

    private static string ResolveChapterExportExtension(int selectedFilterIndex)
    {
        return selectedFilterIndex == 3 ? ".xml" : ".txt";
    }

    private async void AddChapterButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(AddChapterButton_Click),
            async () =>
            {
                var timecode = CurrentFrameToTimecode();
                var title = ViewModel.Texts.VapourSynthPreviewChapterFallbackTitle(ViewModel.Chapters.Count + 1);
                var result = await ShowChapterEditDialogAsync(timecode, title);
                if (result is null)
                {
                    return;
                }

                var chapters = GetChapterEntries();
                chapters.Add(result);
                ViewModel.ReplaceChapters(BuildChapterOptions(chapters));
                UpdateActiveChapter(TimecodeToFrame(result.Timecode, _selectedOutputInfo));
                UpdateChapterButtons();
                RedrawChapterMarkers();
                SetStatusText(ViewModel.Texts.VapourSynthPreviewChapterAddedStatus(result.Title));
                await RequestFrameAsync(TimecodeToFrame(result.Timecode, _selectedOutputInfo));
            },
            ViewModel.Texts.VapourSynthPreviewChapterDialogFailedStatus);
    }

    private async void EditChapterButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(EditChapterButton_Click),
            async () =>
            {
                if (ViewModel.Chapters.Count == 0)
                {
                    return;
                }

                var index = ResolveActiveChapterIndex();
                if (index < 0)
                {
                    return;
                }

                var current = ViewModel.Chapters[index];
                var result = await ShowChapterEditDialogAsync(current.Timecode, current.Title);
                if (result is null)
                {
                    return;
                }

                var chapters = GetChapterEntries();
                chapters[index] = result;
                ViewModel.ReplaceChapters(BuildChapterOptions(chapters));
                UpdateActiveChapter(TimecodeToFrame(result.Timecode, _selectedOutputInfo));
                UpdateChapterButtons();
                RedrawChapterMarkers();
                SetStatusText(ViewModel.Texts.VapourSynthPreviewChapterUpdatedStatus(result.Title));
                await RequestFrameAsync(TimecodeToFrame(result.Timecode, _selectedOutputInfo));
            },
            ViewModel.Texts.VapourSynthPreviewChapterDialogFailedStatus);
    }

    private void DeleteChapterButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Chapters.Count == 0)
        {
            return;
        }

        var index = ResolveActiveChapterIndex();
        if (index < 0)
        {
            return;
        }

        var chapters = GetChapterEntries();
        var title = chapters[index].Title;
        chapters.RemoveAt(index);
        ViewModel.ReplaceChapters(BuildChapterOptions(chapters));
        _activeChapterIndex = -1;
        UpdateChapterButtons();
        RedrawChapterMarkers();
        SetStatusText(ViewModel.Texts.VapourSynthPreviewChapterDeletedStatus(title));
    }

    private async void NextChapterButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(NextChapterButton_Click),
            async () =>
            {
                if (ViewModel.Chapters.Count == 0)
                {
                    return;
                }

                var currentFrame = ViewModel.CurrentFrame;
                var next = ViewModel.Chapters
                    .Select((chapter, index) => new
                    {
                        Chapter = chapter,
                        Index = index,
                        Frame = TimecodeToFrame(chapter.Timecode, _selectedOutputInfo)
                    })
                    .Where(item => item.Frame > currentFrame)
                    .OrderBy(item => item.Frame)
                    .FirstOrDefault()
                    ?? ViewModel.Chapters
                        .Select((chapter, index) => new
                        {
                            Chapter = chapter,
                            Index = index,
                            Frame = TimecodeToFrame(chapter.Timecode, _selectedOutputInfo)
                        })
                        .OrderBy(item => item.Frame)
                        .First();

                _activeChapterIndex = next.Index;
                RedrawChapterMarkers();
                await RequestFrameAsync(next.Frame);
            });
    }

    private async void PlaybackTimer_Tick(object? sender, object e)
    {
        await RunWindowEventHandlerAsync(
            nameof(PlaybackTimer_Tick),
            async () =>
            {
                if (_frameRequestScheduler.IsBusy)
                {
                    return;
                }

                if (_selectedOutputInfo is null || ViewModel.CurrentFrame >= Math.Max(0, ViewModel.TotalFrames - 1))
                {
                    StopPlayback(updateStatus: true);
                    return;
                }

                await RequestRelativeFrameAsync(1);
            });
    }

    private async void VapourSynthPreviewWindow_Closed(object sender, WindowEventArgs args)
    {
        await RunWindowEventHandlerAsync(
            nameof(VapourSynthPreviewWindow_Closed),
            async () =>
            {
                if (_isClosed)
                {
                    return;
                }

                _isClosed = true;
                _flickerTimer.Stop();
                _tabHoldFlickerTimer.Stop();
                _pipetteSampleCts?.Cancel();
                _pipetteSampleCts?.Dispose();
                _pipetteSampleCts = null;
                ScopeCanvas.RemoveFromVisualTree();
                Activated -= VapourSynthPreviewWindow_Activated;
                StopPlayback();
                _frameRequestScheduler.Dispose();
                SaveCurrentOutputState();
                DetachXamlRoot();
                DetachFrameSliderInteractionHandlers();
                DetachPreviewNumberBoxEditorHandlers();
                RootLayout.Loaded -= RootLayout_Loaded;
                RootLayout.Unloaded -= RootLayout_Unloaded;
                RootLayout.ActualThemeChanged -= RootLayout_ActualThemeChanged;
                PreviewImageHost.SizeChanged -= PreviewImageHost_SizeChanged;
                LeftCompareImageHost.SizeChanged -= PreviewImageHost_SizeChanged;
                RightCompareImageHost.SizeChanged -= PreviewImageHost_SizeChanged;
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                DetachPreviewImageVisual();
                try
                {
                    await ClosePreviewSessionIfNeededAsync();
                }
                finally
                {
                    PreviewWindowClosed?.Invoke(this, EventArgs.Empty);
                }
            });
    }

    private Task ClosePreviewSessionIfNeededAsync() =>
        _closePreviewSessionTask ??= ClosePreviewSessionAsync();

    private async Task ClosePreviewSessionAsync()
    {
        try
        {
            _previewLifetime.Cancel();
            await _previewService.CloseSessionAsync();
        }
        finally
        {
            _previewLifetime.Dispose();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.WindowTitle):
                Title = ViewModel.WindowTitle;
                break;
            case nameof(ViewModel.PreviewImageWidth):
            case nameof(ViewModel.PreviewImageHeight):
            case nameof(ViewModel.SecondaryPreviewImageWidth):
            case nameof(ViewModel.SecondaryPreviewImageHeight):
                UpdatePreviewImageVisualSize();
                UpdateWipeClipAndOverlay();
                break;
        }
    }

    private void UpdateFramePropsVisibility(bool isVisible)
    {
        if (ViewModel.IsFramePropsVisible == isVisible)
        {
            return;
        }

        ViewModel.IsFramePropsVisible = isVisible;
        SyncControls();
    }

    private async Task UpdateCropPanelVisibilityAsync(bool isVisible)
    {
        if (ViewModel.IsCropPanelVisible == isVisible)
        {
            return;
        }

        ViewModel.IsCropPanelVisible = isVisible;
        RefreshDisplayedFrame();
        SyncControls();

        if (isVisible)
        {
            await ScrollToCropBoundaryAsync(null);
        }
    }

    private async Task UpdateCropValueAsync(CropField field, NumberBox sender)
    {
        if (_isInternalControlUpdate || _selectedOutputInfo is null || double.IsNaN(sender.Value))
        {
            return;
        }

        var outputState = GetOrCreateOutputState(_selectedOutputInfo);
        outputState.AutoCropRepair = null;
        if (IsAbsoluteCropMode(outputState.CropMode))
        {
            ApplyAbsoluteCropFieldValue(
                outputState.AbsoluteCrop,
                field,
                (int)Math.Round(sender.Value),
                _selectedOutputInfo.Width,
                _selectedOutputInfo.Height);
        }
        else
        {
            ApplyRelativeCropFieldValue(
                outputState.RelativeCrop,
                field,
                (int)Math.Round(sender.Value),
                _selectedOutputInfo.Width,
                _selectedOutputInfo.Height);
        }

        SyncControls();
        RefreshDisplayedFrame();
        await ScrollToCropBoundaryAsync(field);
    }

    private async Task StepByTimeAsync(double deltaSeconds)
    {
        if (_selectedOutputInfo is null)
        {
            return;
        }

        var fps = GetOutputFps(_selectedOutputInfo);
        var frameDelta = Math.Max(1, (int)Math.Round(Math.Abs(deltaSeconds) * fps));
        var targetFrame = deltaSeconds < 0
            ? ViewModel.CurrentFrame - frameDelta
            : ViewModel.CurrentFrame + frameDelta;
        await RequestFrameAsync(targetFrame);
    }

    private void SaveCurrentOutputState()
    {
        if (_selectedOutputInfo is null)
        {
            return;
        }

        var outputState = GetOrCreateOutputState(_selectedOutputInfo);
        outputState.HasVisited = true;
        outputState.CurrentFrame = ViewModel.CurrentFrame;
        outputState.ZoomMode = ViewModel.SelectedZoomMode?.Value ?? "actual";
        outputState.ZoomRatio = ViewModel.ZoomRatio;
        outputState.CropMode = ViewModel.SelectedCropMode?.Value ?? "relative";

        if (IsAbsoluteCropMode(outputState.CropMode))
        {
            outputState.AbsoluteCrop.Left = GetIntValue(CropLeftBox);
            outputState.AbsoluteCrop.Top = GetIntValue(CropTopBox);
            outputState.AbsoluteCrop.Width = Math.Max(1, GetIntValue(CropWidthBox));
            outputState.AbsoluteCrop.Height = Math.Max(1, GetIntValue(CropHeightBox));
            outputState.AbsoluteCrop.ZoomPercentage = Math.Clamp(CropZoomBox.Value, 10, 800);
        }
        else
        {
            outputState.RelativeCrop.Left = GetIntValue(CropLeftBox);
            outputState.RelativeCrop.Top = GetIntValue(CropTopBox);
            outputState.RelativeCrop.Right = GetIntValue(CropRightBox);
            outputState.RelativeCrop.Bottom = GetIntValue(CropBottomBox);
            outputState.RelativeCrop.ZoomPercentage = Math.Clamp(CropZoomBox.Value, 10, 800);
        }

        EnsureCropStatesWithinBounds(outputState, _selectedOutputInfo.Width, _selectedOutputInfo.Height);
    }

    private void ResetDisplayedFrameState()
    {
        _pendingNavigationFrame = null;
        _selectedOutputInfo = null;
        _secondaryOutputInfo = null;
        _lastFrameData = null;
        _secondaryLastFrameData = null;
        _sourceFramePixels = null;
        _reusableSourceFramePixels = null;
        _sourceFrameWidth = 0;
        _sourceFrameHeight = 0;
        _secondarySourceFramePixels = null;
        _reusableSecondarySourceFramePixels = null;
        _secondarySourceFrameWidth = 0;
        _secondarySourceFrameHeight = 0;
        _displayedFramePixels = null;
        _reusableDisplayedFramePixels = null;
        _displayedFrameWidth = 0;
        _displayedFrameHeight = 0;
        _secondaryDisplayedFramePixels = null;
        _reusableSecondaryDisplayedFramePixels = null;
        _secondaryDisplayedFrameWidth = 0;
        _secondaryDisplayedFrameHeight = 0;
        _hasManualSizeAlignmentOverride = false;
        DisposeCachedBitmaps();
        ClearPreviewDrawingSurface();
    }

    private void RestoreOutputPresentationState(PreviewOutputState outputState, VapourSynthPreviewOutputInfo outputInfo)
    {
        EnsureCropStatesWithinBounds(outputState, outputInfo.Width, outputInfo.Height);
        ViewModel.SelectedZoomMode = ViewModel.ZoomModes.FirstOrDefault(option => option.Value == outputState.ZoomMode)
            ?? ViewModel.ZoomModes[0];
        ViewModel.ZoomRatio = outputState.ZoomRatio;
        ViewModel.SelectedCropMode = ViewModel.CropModes.FirstOrDefault(option => option.Value == outputState.CropMode)
            ?? ViewModel.CropModes[0];
        ViewModel.CropZoomPercentage = GetActiveCropZoomPercentage(outputState);
    }

    private static PreviewZoomState CaptureZoomState(PreviewOutputState outputState)
    {
        return new PreviewZoomState(outputState.ZoomMode, outputState.ZoomRatio);
    }

    private static void ApplyZoomState(PreviewOutputState outputState, PreviewZoomState zoomState)
    {
        outputState.ZoomMode = zoomState.ZoomMode;
        outputState.ZoomRatio = zoomState.ZoomRatio;
    }

    private int DetermineTargetFrame(
        VapourSynthPreviewOutputInfo? previousOutputInfo,
        VapourSynthPreviewOutputInfo targetOutputInfo,
        PreviewOutputState targetOutputState,
        int? explicitFrameNumber,
        bool useExplicitFrame)
    {
        var fallbackFrame = Math.Clamp(explicitFrameNumber ?? ViewModel.CurrentFrame, 0, Math.Max(0, targetOutputInfo.TotalFrames - 1));
        if (useExplicitFrame)
        {
            return fallbackFrame;
        }

        if (previousOutputInfo is null || previousOutputInfo.Index == targetOutputInfo.Index)
        {
            return targetOutputState.HasVisited
                ? Math.Clamp(targetOutputState.CurrentFrame, 0, Math.Max(0, targetOutputInfo.TotalFrames - 1))
                : fallbackFrame;
        }

        var syncMode = ViewModel.SelectedOutputSyncMode?.Value ?? "remember";
        return syncMode switch
        {
            "frame" => fallbackFrame,
            "time" => ConvertFrameByTimestamp(previousOutputInfo, targetOutputInfo, explicitFrameNumber ?? ViewModel.CurrentFrame),
            "timeline" => ViewModel.IsTimelineTimeMode
                ? ConvertFrameByTimestamp(previousOutputInfo, targetOutputInfo, explicitFrameNumber ?? ViewModel.CurrentFrame)
                : fallbackFrame,
            _ => targetOutputState.HasVisited
                ? Math.Clamp(targetOutputState.CurrentFrame, 0, Math.Max(0, targetOutputInfo.TotalFrames - 1))
                : fallbackFrame
        };
    }

    private static int ConvertFrameByTimestamp(
        VapourSynthPreviewOutputInfo sourceOutput,
        VapourSynthPreviewOutputInfo targetOutput,
        int sourceFrameNumber)
    {
        var timestamp = GetFrameTimestampSeconds(sourceFrameNumber, sourceOutput);
        var fps = GetOutputFps(targetOutput);
        return Math.Clamp(
            (int)Math.Round(timestamp * fps),
            0,
            Math.Max(0, targetOutput.TotalFrames - 1));
    }

    private static double GetFrameTimestampSeconds(int frameNumber, VapourSynthPreviewOutputInfo outputInfo)
    {
        if (outputInfo.FpsNumerator <= 0 || outputInfo.FpsDenominator <= 0)
        {
            return frameNumber / 24.0;
        }

        return frameNumber / (outputInfo.FpsNumerator / (double)outputInfo.FpsDenominator);
    }

    private TimeSpan CurrentFrameToTimecode()
    {
        return FrameToTimecode(ViewModel.CurrentFrame, _selectedOutputInfo);
    }

    private static TimeSpan FrameToTimecode(int frameNumber, VapourSynthPreviewOutputInfo? outputInfo)
    {
        var fps = outputInfo is null ? 24.0 : GetOutputFps(outputInfo);
        return TimeSpan.FromSeconds(Math.Max(0, frameNumber) / fps);
    }

    private static int TimecodeToFrame(TimeSpan timecode, VapourSynthPreviewOutputInfo? outputInfo)
    {
        var fps = outputInfo is null ? 24.0 : GetOutputFps(outputInfo);
        var maxFrame = outputInfo is null ? int.MaxValue : Math.Max(0, outputInfo.TotalFrames - 1);
        return Math.Clamp((int)Math.Round(Math.Max(0, timecode.TotalSeconds) * fps), 0, maxFrame);
    }

    private static double GetOutputFps(VapourSynthPreviewOutputInfo outputInfo)
    {
        return outputInfo.FpsNumerator > 0 && outputInfo.FpsDenominator > 0
            ? outputInfo.FpsNumerator / (double)outputInfo.FpsDenominator
            : 24.0;
    }

    private static PreviewFrameCropSettings CreateCropSettings(PreviewOutputState outputState, bool cropEnabled)
    {
        return new PreviewFrameCropSettings(
            cropEnabled,
            IsAbsoluteCropMode(outputState.CropMode) ? PreviewCropMode.Absolute : PreviewCropMode.Relative,
            new PreviewAbsoluteCrop(
                outputState.AbsoluteCrop.Left,
                outputState.AbsoluteCrop.Top,
                outputState.AbsoluteCrop.Width,
                outputState.AbsoluteCrop.Height),
            new PreviewRelativeCrop(
                outputState.RelativeCrop.Left,
                outputState.RelativeCrop.Top,
                outputState.RelativeCrop.Right,
                outputState.RelativeCrop.Bottom));
    }

    private PreviewOutputState GetOrCreateOutputState(VapourSynthPreviewOutputInfo outputInfo)
    {
        if (!_outputStates.TryGetValue(outputInfo.Index, out var outputState))
        {
            outputState = PreviewOutputState.CreateDefault(outputInfo.Width, outputInfo.Height);
            _outputStates[outputInfo.Index] = outputState;
        }

        EnsureCropStatesWithinBounds(outputState, outputInfo.Width, outputInfo.Height);
        return outputState;
    }

    private void PruneOutputStates(IReadOnlyList<VapourSynthPreviewOutputInfo> outputs)
    {
        var existingIndices = outputs.Select(static output => output.Index).ToHashSet();
        var obsolete = _outputStates.Keys.Where(index => !existingIndices.Contains(index)).ToArray();
        foreach (var index in obsolete)
        {
            _outputStates.Remove(index);
        }
    }

    private void ApplyCropControlsFromState(PreviewOutputState outputState, VapourSynthPreviewOutputInfo outputInfo)
    {
        var absoluteMode = IsAbsoluteCropMode(outputState.CropMode);
        var cropBounds = _frameComposer.ResolveCropBounds(
            CreateCropSettings(outputState, cropEnabled: true),
            outputInfo.Width,
            outputInfo.Height);

        CropLeftBox.Minimum = 0;
        CropLeftBox.Maximum = Math.Max(0, outputInfo.Width - 1);
        CropTopBox.Minimum = 0;
        CropTopBox.Maximum = Math.Max(0, outputInfo.Height - 1);
        CropWidthBox.Minimum = 1;
        CropWidthBox.Maximum = outputInfo.Width;
        CropHeightBox.Minimum = 1;
        CropHeightBox.Maximum = outputInfo.Height;
        CropRightBox.Minimum = 0;
        CropRightBox.Maximum = Math.Max(0, outputInfo.Width - 1);
        CropBottomBox.Minimum = 0;
        CropBottomBox.Maximum = Math.Max(0, outputInfo.Height - 1);

        CropLeftBox.Value = cropBounds.Left;
        CropTopBox.Value = cropBounds.Top;
        CropWidthBox.Value = cropBounds.Width;
        CropHeightBox.Value = cropBounds.Height;
        CropRightBox.Value = cropBounds.Right;
        CropBottomBox.Value = cropBounds.Bottom;
        CropZoomBox.Value = GetActiveCropZoomPercentage(outputState);

        CropWidthBox.IsEnabled = absoluteMode;
        CropHeightBox.IsEnabled = absoluteMode;
        CropRightBox.IsEnabled = !absoluteMode;
        CropBottomBox.IsEnabled = !absoluteMode;
    }

    private void ResetCropControls()
    {
        CropLeftBox.Value = 0;
        CropTopBox.Value = 0;
        CropWidthBox.Value = 1;
        CropHeightBox.Value = 1;
        CropRightBox.Value = 0;
        CropBottomBox.Value = 0;
        CropZoomBox.Value = 100;
    }

    private static void EnsureCropStatesWithinBounds(PreviewOutputState outputState, int sourceWidth, int sourceHeight)
    {
        EnsureAbsoluteCropStateWithinBounds(outputState.AbsoluteCrop, sourceWidth, sourceHeight);
        EnsureRelativeCropStateWithinBounds(outputState.RelativeCrop, sourceWidth, sourceHeight);
    }

    private static void EnsureAbsoluteCropStateWithinBounds(AbsoluteCropState cropState, int sourceWidth, int sourceHeight)
    {
        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);

        cropState.Left = Math.Clamp(cropState.Left, 0, sourceWidth - 1);
        cropState.Top = Math.Clamp(cropState.Top, 0, sourceHeight - 1);
        cropState.Width = Math.Clamp(cropState.Width, 1, sourceWidth - cropState.Left);
        cropState.Height = Math.Clamp(cropState.Height, 1, sourceHeight - cropState.Top);
        cropState.ZoomPercentage = Math.Clamp(cropState.ZoomPercentage, 10, 800);
    }

    private static void EnsureRelativeCropStateWithinBounds(RelativeCropState cropState, int sourceWidth, int sourceHeight)
    {
        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);

        cropState.Left = Math.Clamp(cropState.Left, 0, sourceWidth - 1);
        cropState.Top = Math.Clamp(cropState.Top, 0, sourceHeight - 1);
        cropState.ZoomPercentage = Math.Clamp(cropState.ZoomPercentage, 10, 800);
        cropState.Right = Math.Clamp(cropState.Right, 0, Math.Max(0, sourceWidth - cropState.Left - 1));
        cropState.Bottom = Math.Clamp(cropState.Bottom, 0, Math.Max(0, sourceHeight - cropState.Top - 1));
    }

    private static void ApplyAbsoluteCropFieldValue(
        AbsoluteCropState cropState,
        CropField field,
        int value,
        int sourceWidth,
        int sourceHeight)
    {
        switch (field)
        {
            case CropField.Left:
                cropState.Left = value;
                break;
            case CropField.Top:
                cropState.Top = value;
                break;
            case CropField.Width:
                cropState.Width = value;
                break;
            case CropField.Height:
                cropState.Height = value;
                break;
        }

        EnsureAbsoluteCropStateWithinBounds(cropState, sourceWidth, sourceHeight);
    }

    private static void ApplyRelativeCropFieldValue(
        RelativeCropState cropState,
        CropField field,
        int value,
        int sourceWidth,
        int sourceHeight)
    {
        switch (field)
        {
            case CropField.Left:
                cropState.Left = value;
                break;
            case CropField.Top:
                cropState.Top = value;
                break;
            case CropField.Right:
                cropState.Right = value;
                break;
            case CropField.Bottom:
                cropState.Bottom = value;
                break;
        }

        EnsureRelativeCropStateWithinBounds(cropState, sourceWidth, sourceHeight);
    }

    private string BuildCropCommandSnippet(bool useSnippetPlaceholder)
    {
        if (_selectedOutputInfo is null)
        {
            return string.Empty;
        }

        var outputState = GetOrCreateOutputState(_selectedOutputInfo);
        var clipToken = useSnippetPlaceholder ? "${1:clip}" : "clip";
        if (outputState.AutoCropRepair is { } repair)
        {
            return VapourSynthRepairScriptBuilder.FormatSnippet([repair], clipToken);
        }
        if (IsAbsoluteCropMode(outputState.CropMode))
        {
            EnsureAbsoluteCropStateWithinBounds(outputState.AbsoluteCrop, _selectedOutputInfo.Width, _selectedOutputInfo.Height);
            return $"{clipToken} = core.std.CropAbs({clipToken}, x={outputState.AbsoluteCrop.Left}, y={outputState.AbsoluteCrop.Top}, width={outputState.AbsoluteCrop.Width}, height={outputState.AbsoluteCrop.Height})";
        }

        EnsureRelativeCropStateWithinBounds(outputState.RelativeCrop, _selectedOutputInfo.Width, _selectedOutputInfo.Height);
        return $"{clipToken} = core.std.CropRel({clipToken}, left={outputState.RelativeCrop.Left}, top={outputState.RelativeCrop.Top}, right={outputState.RelativeCrop.Right}, bottom={outputState.RelativeCrop.Bottom})";
    }

    private static bool IsAbsoluteCropMode(string? mode)
    {
        return string.Equals(mode, "absolute", StringComparison.OrdinalIgnoreCase);
    }

    private static double GetActiveCropZoomPercentage(PreviewOutputState outputState)
    {
        return IsAbsoluteCropMode(outputState.CropMode)
            ? outputState.AbsoluteCrop.ZoomPercentage
            : outputState.RelativeCrop.ZoomPercentage;
    }

    private static void SetActiveCropZoomPercentage(PreviewOutputState outputState, double zoomPercentage)
    {
        if (IsAbsoluteCropMode(outputState.CropMode))
        {
            outputState.AbsoluteCrop.ZoomPercentage = zoomPercentage;
        }
        else
        {
            outputState.RelativeCrop.ZoomPercentage = zoomPercentage;
        }
    }

    private string ResolveSilentSnapshotPath()
    {
        if (_selectedOutputInfo is null || _currentRequest is null)
        {
            throw new InvalidOperationException("Preview context is not ready.");
        }

        var template = string.IsNullOrWhiteSpace(ViewModel.SnapshotTemplate)
            ? "{scriptName}-out{output}-frame{frame}.{ext}"
            : ViewModel.SnapshotTemplate.Trim();
        var scriptName = string.IsNullOrWhiteSpace(_currentRequest.SourceFilePath)
            ? SanitizePathToken(Path.GetFileNameWithoutExtension(_currentRequest.DisplayName))
            : SanitizePathToken(Path.GetFileNameWithoutExtension(_currentRequest.SourceFilePath));
        var timestampText = SanitizePathToken(ViewModel.CurrentTimeText.Replace(":", "-", StringComparison.Ordinal));
        var outputToken = SanitizePathToken(_selectedOutputInfo.Name);
        var result = template
            .Replace("{scriptName}", scriptName, StringComparison.Ordinal)
            .Replace("{output}", string.IsNullOrWhiteSpace(outputToken) ? _selectedOutputInfo.Index.ToString() : outputToken, StringComparison.Ordinal)
            .Replace("{frame}", ViewModel.CurrentFrame.ToString(), StringComparison.Ordinal)
            .Replace("{time}", timestampText, StringComparison.Ordinal)
            .Replace("{ext}", "png", StringComparison.Ordinal);

        if (!Path.IsPathRooted(result))
        {
            var rootDirectory = !string.IsNullOrWhiteSpace(_currentRequest.SourceFilePath)
                ? Path.GetDirectoryName(_currentRequest.SourceFilePath)
                : _currentRequest.WorkingDirectory;
            result = Path.Combine(rootDirectory ?? AppContext.BaseDirectory, result);
        }

        if (string.IsNullOrWhiteSpace(Path.GetExtension(result)))
        {
            result += ".png";
        }

        return Path.GetFullPath(result);
    }

    private string BuildShortcutSnapshotFileName(bool withExtension)
    {
        if (_selectedOutputInfo is null || _currentRequest is null)
        {
            throw new InvalidOperationException("Preview context is not ready.");
        }

        var scriptFileName = !string.IsNullOrWhiteSpace(_currentRequest.SourceFilePath)
            ? Path.GetFileName(_currentRequest.SourceFilePath)
            : _currentRequest.DisplayName;
        if (string.IsNullOrWhiteSpace(scriptFileName))
        {
            scriptFileName = "preview.vpy";
        }
        else if (string.IsNullOrWhiteSpace(Path.GetExtension(scriptFileName)))
        {
            scriptFileName += ".vpy";
        }

        scriptFileName = SanitizePathToken(scriptFileName);
        var compareSuffix = ViewModel.IsCompareModeActive && _secondaryOutputInfo is not null
            ? ViewModel.CompareMode switch
            {
                PreviewCompareMode.Wipe => $"-wipe-{_selectedOutputInfo.Index}vs{_secondaryOutputInfo.Index}",
                PreviewCompareMode.SideBySide => $"-sbs-{_selectedOutputInfo.Index}vs{_secondaryOutputInfo.Index}",
                PreviewCompareMode.Difference => $"-diff-{_selectedOutputInfo.Index}vs{_secondaryOutputInfo.Index}",
                _ => $"-{_selectedOutputInfo.Index}"
            }
            : $"-{_selectedOutputInfo.Index}";
        var fileName = $"{scriptFileName}-{ViewModel.CurrentFrame}{compareSuffix}";
        return withExtension ? $"{fileName}.png" : fileName;
    }

    private string BuildShortcutSnapshotFileName(
        VapourSynthPreviewOutputInfo outputInfo,
        int frameNumber,
        bool withExtension)
    {
        if (_currentRequest is null)
        {
            throw new InvalidOperationException("Preview context is not ready.");
        }

        var scriptFileName = !string.IsNullOrWhiteSpace(_currentRequest.SourceFilePath)
            ? Path.GetFileName(_currentRequest.SourceFilePath)
            : _currentRequest.DisplayName;
        if (string.IsNullOrWhiteSpace(scriptFileName))
        {
            scriptFileName = "preview.vpy";
        }
        else if (string.IsNullOrWhiteSpace(Path.GetExtension(scriptFileName)))
        {
            scriptFileName += ".vpy";
        }

        scriptFileName = SanitizePathToken(scriptFileName);
        var fileName = $"{scriptFileName}-{frameNumber}-{outputInfo.Index}";
        return withExtension ? $"{fileName}.png" : fileName;
    }

    private string ResolvePreviewDialogCurrentPath()
    {
        var scriptDirectory = ResolveExistingDirectory(_currentRequest?.SourceFilePath is null
            ? null
            : Path.GetDirectoryName(_currentRequest.SourceFilePath));
        if (!string.IsNullOrWhiteSpace(scriptDirectory))
        {
            return scriptDirectory;
        }

        var workingDirectory = ResolveExistingDirectory(_currentRequest?.WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            return workingDirectory;
        }

        return string.Empty;
    }

    private static string? ResolveExistingDirectory(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(candidatePath);
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveAllOutputsAtCurrentFrameAsync()
    {
        if (_currentRequest is null || _selectedOutputInfo is null || ViewModel.Outputs.Count == 0)
        {
            return;
        }

        var (pickSucceeded, folderPath) = await TryRunWindowOperationAsync(
            "PickAllSnapshotsFolder",
            () => Task.FromResult(
                WindowInteractionHelper.PickFolderPath(
                    GetWindowHandle(),
                    ViewModel.Texts.VapourSynthPreviewSaveAllFramesButton,
                    ResolvePreviewDialogCurrentPath())),
            ViewModel.Texts.VapourSynthPreviewAllSnapshotsFailedStatus);
        if (!pickSucceeded || string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        StopPlayback();
        var lockedFrame = ViewModel.CurrentFrame;
        var previousOutput = _selectedOutputInfo;
        var savedCount = 0;
        var outputs = ViewModel.Outputs.Select(static option => option.Info).ToList();

        try
        {
            foreach (var output in outputs)
            {
                var frameNumber = Math.Clamp(lockedFrame, 0, Math.Max(0, output.TotalFrames - 1));
                var snapshotFrame = await RenderSnapshotFrameAsync(output, frameNumber);

                if (snapshotFrame.Pixels.Length == 0 || snapshotFrame.Width <= 0 || snapshotFrame.Height <= 0)
                {
                    continue;
                }

                var snapshotPath = Path.Combine(folderPath, BuildShortcutSnapshotFileName(output, frameNumber, withExtension: true));
                await SavePixelsAsPngAsync(snapshotPath, snapshotFrame.Pixels, snapshotFrame.Width, snapshotFrame.Height);
                savedCount++;
            }

            await RestoreOutputAfterBatchSaveAsync(previousOutput, lockedFrame);
            SetStatusText(ViewModel.Texts.VapourSynthPreviewAllSnapshotsSavedStatus(savedCount, outputs.Count, lockedFrame));
        }
        catch (Exception ex)
        {
            LogWindowOperationFailure("SaveAllSnapshots", ex);
            SetStatusText(ViewModel.Texts.VapourSynthPreviewAllSnapshotsFailedStatus(GetWindowOperationErrorDetail(ex)));
            if (_selectedOutputInfo?.Index != previousOutput.Index)
            {
                await RestoreOutputAfterBatchSaveAsync(previousOutput, lockedFrame);
            }
        }
    }

    private async Task<IReadOnlyList<ChapterEntry>> LoadChapterFileAsync(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
        {
            return ParseMatroskaChaptersXml(await File.ReadAllTextAsync(filePath));
        }

        return ParseOgmChapters(await File.ReadAllLinesAsync(filePath));
    }

    private async Task<PreviewFrameCompositionResult> RenderSnapshotFrameAsync(
        VapourSynthPreviewOutputInfo outputInfo,
        int frameNumber)
    {
        var frameData = await _previewService.RenderFrameAsync(outputInfo.Index, frameNumber, ViewModel.CurrentRenderOptions, cancellationToken: _previewLifetimeToken);
        _previewLifetimeToken.ThrowIfCancellationRequested();
        var sourcePixels = frameData.Pixels;
        return _frameComposer.Compose(
            CreateFrameCompositionRequest(outputInfo, sourcePixels, frameData.Width, frameData.Height));
    }

    private IReadOnlyList<VapourSynthPreviewChapterOption> BuildChapterOptions(IEnumerable<ChapterEntry> chapters)
    {
        return chapters
            .OrderBy(static chapter => chapter.Timecode)
            .Select((chapter, index) => new VapourSynthPreviewChapterOption(
                chapter.Timecode,
                chapter.Title,
                ViewModel.FormatChapterLabel(index + 1, chapter.Timecode, chapter.Title)))
            .ToList();
    }

    private List<ChapterEntry> GetChapterEntries()
    {
        return ViewModel.Chapters
            .Select(static chapter => new ChapterEntry(chapter.Timecode, chapter.Title))
            .OrderBy(static chapter => chapter.Timecode)
            .ToList();
    }

    private static IReadOnlyList<ChapterEntry> ParseOgmChapters(IEnumerable<string> lines)
    {
        var timecodes = new Dictionary<int, TimeSpan>();
        var titles = new Dictionary<int, string>();
        var pattern = new Regex(@"^CHAPTER(?<index>\d+)(?<name>NAME)?=(?<value>.*)$", RegexOptions.IgnoreCase);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var match = pattern.Match(line);
            if (!match.Success
                || !int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            var value = match.Groups["value"].Value.Trim();
            if (match.Groups["name"].Success)
            {
                titles[index] = StripOgmLanguagePrefix(value);
            }
            else if (TryParseChapterTimecode(value, out var timecode))
            {
                timecodes[index] = timecode;
            }
        }

        return timecodes
            .OrderBy(static item => item.Key)
            .Select(item => new ChapterEntry(
                item.Value,
                titles.TryGetValue(item.Key, out var title) && !string.IsNullOrWhiteSpace(title)
                    ? title
                    : $"Chapter {item.Key:00}"))
            .ToList();
    }

    private static IReadOnlyList<ChapterEntry> ParseMatroskaChaptersXml(string xml)
    {
        var document = XDocument.Parse(xml);
        return document
            .Descendants("ChapterAtom")
            .Select(atom =>
            {
                var startText = atom.Element("ChapterTimeStart")?.Value.Trim();
                var title = atom
                    .Elements("ChapterDisplay")
                    .Elements("ChapterString")
                    .FirstOrDefault()
                    ?.Value
                    .Trim();

                return TryParseChapterTimecode(startText, out var timecode)
                    ? new ChapterEntry(timecode, string.IsNullOrWhiteSpace(title) ? "Chapter" : title)
                    : null;
            })
            .Where(static chapter => chapter is not null)
            .Select(static chapter => chapter!)
            .OrderBy(static chapter => chapter.Timecode)
            .ToList();
    }

    private static IEnumerable<string> BuildOgmChapterLines(IReadOnlyList<ChapterEntry> chapters)
    {
        for (var index = 0; index < chapters.Count; index++)
        {
            var chapterNumber = index + 1;
            yield return $"CHAPTER{chapterNumber:00}={FormatChapterTimecode(chapters[index].Timecode)}";
            yield return $"CHAPTER{chapterNumber:00}NAME={chapters[index].Title}";
        }
    }

    private static string BuildMatroskaChaptersXml(IReadOnlyList<ChapterEntry> chapters)
    {
        var editionEntry = new XElement("EditionEntry",
            chapters.Select((chapter, index) =>
                new XElement("ChapterAtom",
                    new XElement("ChapterTimeStart", FormatChapterTimecode(chapter.Timecode)),
                    new XElement("ChapterUID", index + 1),
                    new XElement("ChapterDisplay",
                        new XElement("ChapterString", chapter.Title),
                        new XElement("ChapterLanguage", "und")))));

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("Chapters", editionEntry));
        return document.ToString();
    }

    private static bool TryParseChapterTimecode(string? value, out TimeSpan timecode)
    {
        timecode = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().Replace(',', '.');
        var parts = text.Split(':');
        if (parts.Length == 2)
        {
            text = $"00:{text}";
        }

        return TimeSpan.TryParseExact(
                text,
                [@"h\:mm\:ss\.fff", @"hh\:mm\:ss\.fff", @"h\:mm\:ss", @"hh\:mm\:ss"],
                CultureInfo.InvariantCulture,
                out timecode)
            || TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out timecode);
    }

    private static string FormatChapterTimecode(TimeSpan timecode)
    {
        return timecode.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static string StripOgmLanguagePrefix(string value)
    {
        var separatorIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex > 0 && separatorIndex <= 3)
        {
            return value[(separatorIndex + 1)..].Trim();
        }

        return value.Trim();
    }

    private async Task<ChapterEntry?> ShowChapterEditDialogAsync(TimeSpan timecode, string title)
    {
        var titleBox = new TextBox
        {
            Header = ViewModel.Texts.VapourSynthPreviewChapterTitleHeader,
            Text = title
        };
        var timecodeBox = new TextBox
        {
            Header = ViewModel.Texts.VapourSynthPreviewChapterTimecodeHeader,
            Text = FormatChapterTimecode(timecode)
        };
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                titleBox,
                timecodeBox
            }
        };

        var dialog = new ContentDialog
        {
            Title = ViewModel.Texts.VapourSynthPreviewChapterDialogTitle,
            PrimaryButtonText = ViewModel.Texts.SaveButton,
            CloseButtonText = ViewModel.Texts.CancelButton,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootLayout.XamlRoot,
            RequestedTheme = RootLayout.ActualTheme,
            Content = panel
        };

        var (showSucceeded, result) = await TryRunWindowOperationAsync(
            "ShowChapterEditDialog",
            async () => await dialog.ShowAsync(),
            ViewModel.Texts.VapourSynthPreviewChapterDialogFailedStatus);
        if (!showSucceeded || result != ContentDialogResult.Primary)
        {
            return null;
        }

        if (!TryParseChapterTimecode(timecodeBox.Text, out var parsedTimecode))
        {
            SetStatusText(ViewModel.Texts.VapourSynthPreviewChapterTimecodeInvalidStatus);
            return null;
        }

        var normalizedTitle = titleBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            normalizedTitle = ViewModel.Texts.VapourSynthPreviewChapterFallbackTitle(ViewModel.Chapters.Count + 1);
        }

        return new ChapterEntry(parsedTimecode, normalizedTitle);
    }

    private void UpdateChapterButtons()
    {
        var hasChapters = ViewModel.Chapters.Count > 0;
        ExportChapterButton.IsEnabled = hasChapters;
        EditChapterButton.IsEnabled = hasChapters;
        DeleteChapterButton.IsEnabled = hasChapters;
        NextChapterButton.IsEnabled = hasChapters;
        ViewModel.NotifyChaptersChanged();
    }

    private int ResolveActiveChapterIndex()
    {
        if (ViewModel.Chapters.Count == 0)
        {
            return -1;
        }

        if (_activeChapterIndex >= 0 && _activeChapterIndex < ViewModel.Chapters.Count)
        {
            return _activeChapterIndex;
        }

        if (_selectedOutputInfo is null)
        {
            return 0;
        }

        var currentFrame = ViewModel.CurrentFrame;
        var nearest = ViewModel.Chapters
            .Select((chapter, index) => new
            {
                Index = index,
                Frame = TimecodeToFrame(chapter.Timecode, _selectedOutputInfo)
            })
            .OrderBy(item => Math.Abs(item.Frame - currentFrame))
            .First();

        _activeChapterIndex = nearest.Index;
        return nearest.Index;
    }

    private void UpdateActiveChapter(int frameNumber)
    {
        if (_selectedOutputInfo is null || ViewModel.Chapters.Count == 0)
        {
            _activeChapterIndex = -1;
            UpdateChapterButtons();
            return;
        }

        var nearest = ViewModel.Chapters
            .Select((chapter, index) => new
            {
                Index = index,
                Frame = TimecodeToFrame(chapter.Timecode, _selectedOutputInfo)
            })
            .OrderBy(item => Math.Abs(item.Frame - frameNumber))
            .First();
        var toleranceFrames = Math.Max(1, (int)Math.Round(GetOutputFps(_selectedOutputInfo) * 0.1));
        _activeChapterIndex = Math.Abs(nearest.Frame - frameNumber) <= toleranceFrames
            ? nearest.Index
            : -1;
        UpdateChapterButtons();
    }

    private void RedrawChapterMarkers()
    {
        ChapterMarkerCanvas.Children.Clear();
        if (_selectedOutputInfo is null || ViewModel.Chapters.Count == 0)
        {
            return;
        }

        var height = ChapterMarkerCanvas.ActualHeight;
        if (height <= 0)
        {
            height = FrameSlider.ActualHeight;
        }

        if (height <= 0 || FrameSlider.ActualWidth <= 0)
        {
            return;
        }

        var track = MeasureSliderTrackBounds();
        if (track.width <= 0)
        {
            return;
        }

        var maxFrame = Math.Max(1, ViewModel.FrameSliderMaximum);
        var markerStyle = (Style)ChapterMarkerCanvas.Resources["ChapterMarkerStyle"];
        var activeMarkerStyle = (Style)ChapterMarkerCanvas.Resources["ActiveChapterMarkerStyle"];
        for (var index = 0; index < ViewModel.Chapters.Count; index++)
        {
            var frame = TimecodeToFrame(ViewModel.Chapters[index].Timecode, _selectedOutputInfo);
            var x = track.offset + (frame / maxFrame) * track.width;
            var marker = new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = x,
                X2 = x,
                Y1 = 3,
                Y2 = Math.Max(4, height - 3),
                Style = index == _activeChapterIndex ? activeMarkerStyle : markerStyle
            };
            ChapterMarkerCanvas.Children.Add(marker);
        }
    }

    private (double offset, double width) MeasureSliderTrackBounds()
    {
        var thumbWidth = 24.0;
        try
        {
            var thumb = FindDescendant<Thumb>(FrameSlider);
            if (thumb is not null && thumb.ActualWidth > 0)
            {
                thumbWidth = thumb.ActualWidth;
            }
        }
        catch (Exception ex)
        {
            LogWindowOperationFailure("MeasureSliderTrackBounds", ex, AppDiagnosticSeverity.Debug);
        }

        return (thumbWidth / 2.0, Math.Max(1, FrameSlider.ActualWidth - thumbWidth));
    }

    private static string SanitizePathToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "preview";
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }

        return value;
    }

    private static async Task SavePixelsAsPngAsync(
        string filePath,
        byte[] pixels,
        int width,
        int height,
        BitmapAlphaMode alphaMode = BitmapAlphaMode.Ignore)
    {
        using var outputStream = File.Open(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None).AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            alphaMode,
            (uint)width,
            (uint)height,
            96,
            96,
            pixels);
        await encoder.FlushAsync();
    }

    private async Task RestoreOutputAfterBatchSaveAsync(
        VapourSynthPreviewOutputInfo outputInfo,
        int frameNumber)
    {
        try
        {
            await SelectOutputAsync(
                outputInfo,
                Math.Clamp(frameNumber, 0, Math.Max(0, outputInfo.TotalFrames - 1)),
                useExplicitFrame: true);
        }
        catch (Exception ex)
        {
            LogWindowOperationFailure("RestoreOutputAfterBatchSave", ex);
        }
    }

    private async Task ShowAdvancedSettingsAsync()
    {
        var outputSyncComboBox = new ComboBox
        {
            DisplayMemberPath = nameof(StringChoiceOption.Label),
            ItemsSource = ViewModel.OutputSyncModes,
            SelectedItem = ViewModel.SelectedOutputSyncMode
        };
        var scalingAlgorithmComboBox = new ComboBox
        {
            DisplayMemberPath = nameof(StringChoiceOption.Label),
            ItemsSource = ViewModel.ScalingAlgorithmModes,
            SelectedItem = ViewModel.SelectedScalingAlgorithmMode
        };
        var silentSnapshotToggle = new ToggleSwitch
        {
            Header = ViewModel.Texts.VapourSynthPreviewSilentSnapshotHeader,
            IsOn = ViewModel.SilentSnapshotEnabled,
            OnContent = ViewModel.Texts.ToggleOnLabel,
            OffContent = ViewModel.Texts.ToggleOffLabel
        };
        var snapshotTemplateTextBox = new TextBox
        {
            Header = ViewModel.Texts.VapourSynthPreviewSnapshotTemplateHeader,
            Text = ViewModel.SnapshotTemplate,
            IsEnabled = ViewModel.SilentSnapshotEnabled
        };

        silentSnapshotToggle.Toggled += (_, _) =>
        {
            snapshotTemplateTextBox.IsEnabled = silentSnapshotToggle.IsOn;
        };

        var dialog = new ContentDialog
        {
            Title = ViewModel.Texts.VapourSynthPreviewAdvancedSettingsTitle,
            PrimaryButtonText = ViewModel.Texts.SaveButton,
            CloseButtonText = ViewModel.Texts.CancelButton,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootLayout.XamlRoot,
            RequestedTheme = RootLayout.ActualTheme,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = ViewModel.Texts.VapourSynthPreviewOutputSyncHeader
                    },
                    outputSyncComboBox,
                    new TextBlock
                    {
                        Text = ViewModel.Texts.VapourSynthPreviewScalingAlgorithmHeader
                    },
                    scalingAlgorithmComboBox,
                    silentSnapshotToggle,
                    snapshotTemplateTextBox,
                    new TextBlock
                    {
                        Text = ViewModel.Texts.VapourSynthPreviewSnapshotTemplateHint,
                        TextWrapping = TextWrapping.WrapWholeWords
                    }
                }
            }
        };

        var (showSucceeded, result) = await TryRunWindowOperationAsync(
            "ShowAdvancedSettingsDialog",
            async () => await dialog.ShowAsync(),
            ViewModel.Texts.VapourSynthPreviewAdvancedSettingsFailedStatus);
        if (!showSucceeded || result != ContentDialogResult.Primary)
        {
            return;
        }

        ViewModel.SelectedOutputSyncMode = outputSyncComboBox.SelectedItem as StringChoiceOption
            ?? ViewModel.OutputSyncModes.FirstOrDefault();
        ViewModel.SelectedScalingAlgorithmMode = scalingAlgorithmComboBox.SelectedItem as StringChoiceOption
            ?? ViewModel.ScalingAlgorithmModes.FirstOrDefault();
        ViewModel.SilentSnapshotEnabled = silentSnapshotToggle.IsOn;
        ViewModel.SnapshotTemplate = snapshotTemplateTextBox.Text?.Trim() ?? string.Empty;
        ViewModel.SaveScalingAlgorithmPreference();
        ApplyPreviewScalingAlgorithm();
        if (ViewModel.IsCompareModeActive && _selectedOutputInfo is not null)
        {
            await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
        }

        SetStatusText(ViewModel.Texts.VapourSynthPreviewAdvancedSettingsSavedStatus);
    }

    private void ApplyPreviewScalingAlgorithm()
    {
        EnsurePreviewImageVisual();
        var interpolationMode = GetPreviewBitmapInterpolationMode();
        if (_previewImageBrush is not null)
        {
            _previewImageBrush.BitmapInterpolationMode = interpolationMode;
        }

        if (_previewImageBrushB is not null)
        {
            _previewImageBrushB.BitmapInterpolationMode = interpolationMode;
        }

        if (_previewImageBrushDiff is not null)
        {
            _previewImageBrushDiff.BitmapInterpolationMode = interpolationMode;
        }

        if (ViewModel.IsDifferenceMode && _cachedBitmapA is not null && _cachedBitmapB is not null)
        {
            RedrawDifferenceSurface();
        }
    }

    private void StopPlayback(bool updateStatus = false)
    {
        _playbackTimer.Stop();
        _isPlaying = false;
        _frameRequestScheduler.ClearPending();
        PlayPauseIcon.Symbol = Symbol.Play;

        if (updateStatus && _selectedOutputInfo is not null && _lastFrameData is not null)
        {
            SetStatusText(ViewModel.Texts.VapourSynthPreviewReadyStatus(_selectedOutputInfo.Index, ViewModel.CurrentFrame));
        }
    }

    private void SetStatusText(string statusText)
    {
        _pendingStatusText = null;
        _statusCommitTimer.Stop();
        ViewModel.UpdateStatus(statusText);
    }

    private async Task<bool> TryRunWindowActionAsync(
        string operationName,
        Func<Task> operationAsync,
        Func<string, string> failureStatusFactory)
    {
        try
        {
            await operationAsync();
            return true;
        }
        catch (Exception ex)
        {
            LogWindowOperationFailure(operationName, ex);
            SetStatusText(failureStatusFactory(GetWindowOperationErrorDetail(ex)));
            return false;
        }
    }

    private async Task RunWindowEventHandlerAsync(
        string operationName,
        Func<Task> operationAsync,
        Func<string, string>? failureStatusFactory = null)
    {
        try
        {
            await operationAsync();
        }
        catch (OperationCanceledException ex)
        {
            LogWindowOperationFailure(operationName, ex, AppDiagnosticSeverity.Warning);
        }
        catch (Exception ex)
        {
            LogWindowOperationFailure(operationName, ex);
            if (!_isClosed)
            {
                var statusFactory = failureStatusFactory ?? ViewModel.Texts.VapourSynthPreviewRenderFailedStatus;
                SetStatusText(statusFactory(GetWindowOperationErrorDetail(ex)));
            }
        }
    }

    private async Task<(bool Success, TResult Result)> TryRunWindowOperationAsync<TResult>(
        string operationName,
        Func<Task<TResult>> operationAsync,
        Func<string, string> failureStatusFactory)
    {
        try
        {
            return (true, await operationAsync());
        }
        catch (Exception ex)
        {
            LogWindowOperationFailure(operationName, ex);
            SetStatusText(failureStatusFactory(GetWindowOperationErrorDetail(ex)));
            return (false, default!);
        }
    }

    private bool TrySetClipboardContent(DataPackage dataPackage, string operationName, out string errorDetail)
    {
        try
        {
            Clipboard.SetContent(dataPackage);
        }
        catch (Exception ex)
        {
            LogWindowOperationFailure($"Clipboard.SetContent:{operationName}", ex);
            errorDetail = GetWindowOperationErrorDetail(ex);
            return false;
        }

        try
        {
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            LogWindowOperationFailure($"Clipboard.Flush:{operationName}", ex);
        }

        errorDetail = string.Empty;
        return true;
    }

    private void LogWindowOperationFailure(
        string operationName,
        Exception exception,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Error)
    {
        try
        {
            App.GetService<IAppDiagnostics>().WriteException(
                nameof(VapourSynthPreviewWindow),
                operationName,
                exception,
                severity);
        }
        catch (Exception logException)
        {
            AppDiagnosticsLog.Write(
                _appPaths,
                nameof(VapourSynthPreviewWindow),
                $"{operationName} failed. {exception.GetType().Name}: {exception.Message}",
                severity,
                exception: exception);
            Debug.WriteLine($"Failed to write preview window diagnostic through app service. {logException}");
        }

        Debug.WriteLine(exception);
    }

    private static string GetWindowOperationErrorDetail(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;

    private void QueueFrameStatusText(string statusText)
    {
        _pendingStatusText = statusText;
        _statusCommitTimer.Stop();
        _statusCommitTimer.Start();
    }

    private void StatusCommitTimer_Tick(object? sender, object e)
    {
        _statusCommitTimer.Stop();
        if (string.IsNullOrWhiteSpace(_pendingStatusText))
        {
            return;
        }

        ViewModel.UpdateStatus(_pendingStatusText);
        _pendingStatusText = null;
    }

    private void DetachControlEvents()
    {
        OutputSelectorComboBox.SelectionChanged -= OutputSelectorComboBox_SelectionChanged;
        CompareModeComboBox.SelectionChanged -= CompareModeComboBox_SelectionChanged;
        CompareOutputAComboBox.SelectionChanged -= CompareOutputAComboBox_SelectionChanged;
        CompareOutputBComboBox.SelectionChanged -= CompareOutputBComboBox_SelectionChanged;
        DifferenceStyleComboBox.SelectionChanged -= DifferenceStyleComboBox_SelectionChanged;
        DifferenceGainComboBox.SelectionChanged -= DifferenceGainComboBox_SelectionChanged;
        FlickerIntervalComboBox.SelectionChanged -= FlickerIntervalComboBox_SelectionChanged;
        CompareSizeAlignmentComboBox.SelectionChanged -= CompareSizeAlignmentComboBox_SelectionChanged;
        SecondaryFrameOffsetBox.ValueChanged -= SecondaryFrameOffsetBox_ValueChanged;
        SubpixelOffsetXBox.ValueChanged -= SubpixelOffsetBox_ValueChanged;
        SubpixelOffsetYBox.ValueChanged -= SubpixelOffsetBox_ValueChanged;
        FrameNumberBox.ValueChanged -= FrameNumberBox_ValueChanged;
        ZoomModeComboBox.SelectionChanged -= ZoomModeComboBox_SelectionChanged;
        ZoomRatioBox.ValueChanged -= ZoomRatioBox_ValueChanged;
        TimelineModeComboBox.SelectionChanged -= TimelineModeComboBox_SelectionChanged;
        TimeStepSecondsBox.ValueChanged -= TimeStepSecondsBox_ValueChanged;
        CropModeComboBox.SelectionChanged -= CropModeComboBox_SelectionChanged;
        CropLeftBox.ValueChanged -= CropLeftBox_ValueChanged;
        CropTopBox.ValueChanged -= CropTopBox_ValueChanged;
        CropWidthBox.ValueChanged -= CropWidthBox_ValueChanged;
        CropHeightBox.ValueChanged -= CropHeightBox_ValueChanged;
        CropRightBox.ValueChanged -= CropRightBox_ValueChanged;
        CropBottomBox.ValueChanged -= CropBottomBox_ValueChanged;
        CropZoomBox.ValueChanged -= CropZoomBox_ValueChanged;
        StepSizeBox.ValueChanged -= StepSizeBox_ValueChanged;
        FrameSlider.ValueChanged -= FrameSlider_ValueChanged;
        ChapterSelectorComboBox.SelectionChanged -= ChapterSelectorComboBox_SelectionChanged;
    }

    private void AttachControlEvents()
    {
        OutputSelectorComboBox.SelectionChanged -= OutputSelectorComboBox_SelectionChanged;
        CompareModeComboBox.SelectionChanged -= CompareModeComboBox_SelectionChanged;
        CompareOutputAComboBox.SelectionChanged -= CompareOutputAComboBox_SelectionChanged;
        CompareOutputBComboBox.SelectionChanged -= CompareOutputBComboBox_SelectionChanged;
        DifferenceStyleComboBox.SelectionChanged -= DifferenceStyleComboBox_SelectionChanged;
        DifferenceGainComboBox.SelectionChanged -= DifferenceGainComboBox_SelectionChanged;
        FlickerIntervalComboBox.SelectionChanged -= FlickerIntervalComboBox_SelectionChanged;
        CompareSizeAlignmentComboBox.SelectionChanged -= CompareSizeAlignmentComboBox_SelectionChanged;
        SecondaryFrameOffsetBox.ValueChanged -= SecondaryFrameOffsetBox_ValueChanged;
        SubpixelOffsetXBox.ValueChanged -= SubpixelOffsetBox_ValueChanged;
        SubpixelOffsetYBox.ValueChanged -= SubpixelOffsetBox_ValueChanged;
        FrameNumberBox.ValueChanged -= FrameNumberBox_ValueChanged;
        ZoomModeComboBox.SelectionChanged -= ZoomModeComboBox_SelectionChanged;
        ZoomRatioBox.ValueChanged -= ZoomRatioBox_ValueChanged;
        TimelineModeComboBox.SelectionChanged -= TimelineModeComboBox_SelectionChanged;
        TimeStepSecondsBox.ValueChanged -= TimeStepSecondsBox_ValueChanged;
        CropModeComboBox.SelectionChanged -= CropModeComboBox_SelectionChanged;
        CropLeftBox.ValueChanged -= CropLeftBox_ValueChanged;
        CropTopBox.ValueChanged -= CropTopBox_ValueChanged;
        CropWidthBox.ValueChanged -= CropWidthBox_ValueChanged;
        CropHeightBox.ValueChanged -= CropHeightBox_ValueChanged;
        CropRightBox.ValueChanged -= CropRightBox_ValueChanged;
        CropBottomBox.ValueChanged -= CropBottomBox_ValueChanged;
        CropZoomBox.ValueChanged -= CropZoomBox_ValueChanged;
        StepSizeBox.ValueChanged -= StepSizeBox_ValueChanged;
        FrameSlider.ValueChanged -= FrameSlider_ValueChanged;

        OutputSelectorComboBox.SelectionChanged += OutputSelectorComboBox_SelectionChanged;
        CompareModeComboBox.SelectionChanged += CompareModeComboBox_SelectionChanged;
        CompareOutputAComboBox.SelectionChanged += CompareOutputAComboBox_SelectionChanged;
        CompareOutputBComboBox.SelectionChanged += CompareOutputBComboBox_SelectionChanged;
        DifferenceStyleComboBox.SelectionChanged += DifferenceStyleComboBox_SelectionChanged;
        DifferenceGainComboBox.SelectionChanged += DifferenceGainComboBox_SelectionChanged;
        FlickerIntervalComboBox.SelectionChanged += FlickerIntervalComboBox_SelectionChanged;
        CompareSizeAlignmentComboBox.SelectionChanged += CompareSizeAlignmentComboBox_SelectionChanged;
        SecondaryFrameOffsetBox.ValueChanged += SecondaryFrameOffsetBox_ValueChanged;
        SubpixelOffsetXBox.ValueChanged += SubpixelOffsetBox_ValueChanged;
        SubpixelOffsetYBox.ValueChanged += SubpixelOffsetBox_ValueChanged;
        FrameNumberBox.ValueChanged += FrameNumberBox_ValueChanged;
        ZoomModeComboBox.SelectionChanged += ZoomModeComboBox_SelectionChanged;
        ZoomRatioBox.ValueChanged += ZoomRatioBox_ValueChanged;
        TimelineModeComboBox.SelectionChanged += TimelineModeComboBox_SelectionChanged;
        TimeStepSecondsBox.ValueChanged += TimeStepSecondsBox_ValueChanged;
        CropModeComboBox.SelectionChanged += CropModeComboBox_SelectionChanged;
        CropLeftBox.ValueChanged += CropLeftBox_ValueChanged;
        CropTopBox.ValueChanged += CropTopBox_ValueChanged;
        CropWidthBox.ValueChanged += CropWidthBox_ValueChanged;
        CropHeightBox.ValueChanged += CropHeightBox_ValueChanged;
        CropRightBox.ValueChanged += CropRightBox_ValueChanged;
        CropBottomBox.ValueChanged += CropBottomBox_ValueChanged;
        CropZoomBox.ValueChanged += CropZoomBox_ValueChanged;
        StepSizeBox.ValueChanged += StepSizeBox_ValueChanged;
        FrameSlider.ValueChanged += FrameSlider_ValueChanged;
        ChapterSelectorComboBox.SelectionChanged += ChapterSelectorComboBox_SelectionChanged;
    }

    private nint GetWindowHandle()
    {
        return WindowNative.GetWindowHandle(this);
    }

    private void EnsurePreferredWindowPresentation()
    {
        if (TryEnterFullScreen())
        {
            return;
        }

        if (TryMaximizeWindow())
        {
            return;
        }

        TryResizeWindow();
    }

    private bool TryRestoreWindowedPresentation()
    {
        if (TryMaximizeWindow())
        {
            return true;
        }

        return TryResizeWindow();
    }

    private bool TryEnterFullScreen()
    {
        try
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            _isFullScreenActive = true;
            return true;
        }
        catch (Exception ex)
        {
            _isFullScreenActive = false;
            LogWindowOperationFailure("TryEnterFullScreen", ex, AppDiagnosticSeverity.Warning);
            return false;
        }
    }

    private bool TryMaximizeWindow()
    {
        try
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
                _isFullScreenActive = false;
                return true;
            }
        }
        catch (Exception ex)
        {
            LogWindowOperationFailure("TryMaximizeWindow", ex, AppDiagnosticSeverity.Warning);
        }

        return false;
    }

    private bool TryResizeWindow()
    {
        try
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            AppWindow.Resize(new SizeInt32(1560, 1040));
            _isFullScreenActive = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private byte[]? ConsumeReusableSourcePixels()
    {
        var reusableBuffer = _reusableSourceFramePixels;
        _reusableSourceFramePixels = null;
        return reusableBuffer;
    }

    private byte[]? ConsumeReusableDisplayedPixels()
    {
        var reusableBuffer = _reusableDisplayedFramePixels;
        _reusableDisplayedFramePixels = null;
        return reusableBuffer;
    }

    private byte[]? ConsumeReusableSecondarySourcePixels()
    {
        var reusableBuffer = _reusableSecondarySourceFramePixels;
        _reusableSecondarySourceFramePixels = null;
        return reusableBuffer;
    }

    private byte[]? ConsumeReusableSecondaryDisplayedPixels()
    {
        var reusableBuffer = _reusableSecondaryDisplayedFramePixels;
        _reusableSecondaryDisplayedFramePixels = null;
        return reusableBuffer;
    }

    private void CommitActiveFrameBuffers(
        VapourSynthPreviewFrameData frameData,
        byte[] sourcePixels,
        int displayWidth,
        int displayHeight,
        byte[] displayPixels)
    {
        var previousSourcePixels = _sourceFramePixels;
        var previousDisplayedPixels = _displayedFramePixels;

        _sourceFramePixels = sourcePixels;
        _sourceFrameWidth = frameData.Width;
        _sourceFrameHeight = frameData.Height;
        _displayedFramePixels = displayPixels;
        _displayedFrameWidth = displayWidth;
        _displayedFrameHeight = displayHeight;

        _reusableSourceFramePixels = CanReuseBuffer(previousSourcePixels, _sourceFramePixels, _displayedFramePixels, _secondarySourceFramePixels, _secondaryDisplayedFramePixels)
            ? previousSourcePixels
            : null;
        _reusableDisplayedFramePixels = CanReuseBuffer(previousDisplayedPixels, _sourceFramePixels, _displayedFramePixels, _reusableSourceFramePixels, _secondarySourceFramePixels, _secondaryDisplayedFramePixels)
            ? previousDisplayedPixels
            : null;
    }

    private void CommitActiveSecondaryFrameBuffers(
        VapourSynthPreviewFrameData frameData,
        byte[] sourcePixels,
        int displayWidth,
        int displayHeight,
        byte[] displayPixels)
    {
        var previousSourcePixels = _secondarySourceFramePixels;
        var previousDisplayedPixels = _secondaryDisplayedFramePixels;

        _secondarySourceFramePixels = sourcePixels;
        _secondarySourceFrameWidth = frameData.Width;
        _secondarySourceFrameHeight = frameData.Height;
        _secondaryDisplayedFramePixels = displayPixels;
        _secondaryDisplayedFrameWidth = displayWidth;
        _secondaryDisplayedFrameHeight = displayHeight;

        _reusableSecondarySourceFramePixels = CanReuseBuffer(previousSourcePixels, _secondarySourceFramePixels, _secondaryDisplayedFramePixels, _sourceFramePixels, _displayedFramePixels)
            ? previousSourcePixels
            : null;
        _reusableSecondaryDisplayedFramePixels = CanReuseBuffer(previousDisplayedPixels, _secondarySourceFramePixels, _secondaryDisplayedFramePixels, _reusableSecondarySourceFramePixels, _sourceFramePixels, _displayedFramePixels)
            ? previousDisplayedPixels
            : null;
    }

    private void CaptureReusableFrameBuffers(byte[] sourcePixels, byte[]? displayPixels = null)
    {
        if (CanReuseBuffer(sourcePixels, _sourceFramePixels, _displayedFramePixels, _reusableDisplayedFramePixels, _secondarySourceFramePixels, _secondaryDisplayedFramePixels))
        {
            _reusableSourceFramePixels = sourcePixels;
        }

        if (displayPixels is not null
            && CanReuseBuffer(displayPixels, sourcePixels, _sourceFramePixels, _displayedFramePixels, _reusableSourceFramePixels, _secondarySourceFramePixels, _secondaryDisplayedFramePixels))
        {
            _reusableDisplayedFramePixels = displayPixels;
        }
    }

    private void CaptureReusableSecondaryFrameBuffers(byte[] sourcePixels, byte[]? displayPixels = null)
    {
        if (CanReuseBuffer(sourcePixels, _secondarySourceFramePixels, _secondaryDisplayedFramePixels, _reusableSecondaryDisplayedFramePixels, _sourceFramePixels, _displayedFramePixels))
        {
            _reusableSecondarySourceFramePixels = sourcePixels;
        }

        if (displayPixels is not null
            && CanReuseBuffer(displayPixels, sourcePixels, _secondarySourceFramePixels, _secondaryDisplayedFramePixels, _reusableSecondarySourceFramePixels, _sourceFramePixels, _displayedFramePixels))
        {
            _reusableSecondaryDisplayedFramePixels = displayPixels;
        }
    }

    private static bool CanReuseBuffer(byte[]? candidate, params byte[]?[] activeBuffers)
    {
        if (candidate is null)
        {
            return false;
        }

        foreach (var activeBuffer in activeBuffers)
        {
            if (ReferenceEquals(candidate, activeBuffer))
            {
                return false;
            }
        }

        return true;
    }

    private static int GetIntValue(NumberBox numberBox)
    {
        return double.IsNaN(numberBox.Value) ? 0 : (int)Math.Round(numberBox.Value);
    }

    private bool CanPanPreview(ScrollViewer scrollViewer)
    {
        return scrollViewer.ScrollableWidth > 1
            || scrollViewer.ScrollableHeight > 1;
    }

    private double GetCurrentEffectiveZoomRatio()
    {
        var referenceWidth = (ViewModel.IsWipeMode || ViewModel.IsDifferenceMode) && _secondaryDisplayedFrameWidth > 0
            ? Math.Max(_displayedFrameWidth, _secondaryDisplayedFrameWidth)
            : _displayedFrameWidth;
        if (referenceWidth <= 0 || ViewModel.PreviewImageWidth <= 0)
        {
            return Math.Clamp(ViewModel.ZoomRatio, 0.05, 16.0);
        }

        var displayScale = RootLayout.XamlRoot?.RasterizationScale ?? 1.0;
        var visibleRatio = ViewModel.PreviewImageWidth / referenceWidth;
        var cropMultiplier = ViewModel.IsCropPanelVisible
            ? Math.Max(0.1, ViewModel.CropZoomPercentage / 100.0)
            : 1.0;

        var zoomRatio = visibleRatio * displayScale / cropMultiplier;
        if (zoomRatio <= 0 || double.IsNaN(zoomRatio) || double.IsInfinity(zoomRatio))
        {
            return Math.Clamp(ViewModel.ZoomRatio, 0.05, 16.0);
        }

        return Math.Clamp(zoomRatio, 0.05, 16.0);
    }

    private void ApplyCustomZoom(double zoomRatio)
    {
        var customZoomOption = ViewModel.ZoomModes.FirstOrDefault(option => option.Value == "custom");
        ViewModel.ZoomRatio = zoomRatio;
        if (customZoomOption is not null)
        {
            ViewModel.SelectedZoomMode = customZoomOption;
        }

        SaveCurrentOutputState();
        SyncControls();
    }

    private void RestorePreviewScrollAnchor(
        ScrollViewer scrollViewer,
        double anchorXRatio,
        double anchorYRatio,
        double pointerViewportX,
        double pointerViewportY)
    {
        scrollViewer.UpdateLayout();
        var isSecondaryPane = ReferenceEquals(scrollViewer, RightCompareScrollViewer);
        var imageWidth = isSecondaryPane ? ViewModel.SecondaryPreviewImageWidth : ViewModel.PreviewImageWidth;
        var imageHeight = isSecondaryPane ? ViewModel.SecondaryPreviewImageHeight : ViewModel.PreviewImageHeight;

        var targetHorizontalOffset = Math.Clamp(
            imageWidth * anchorXRatio - pointerViewportX,
            0,
            scrollViewer.ScrollableWidth);
        var targetVerticalOffset = Math.Clamp(
            imageHeight * anchorYRatio - pointerViewportY,
            0,
            scrollViewer.ScrollableHeight);

        SetPreviewScrollOffset(scrollViewer, targetHorizontalOffset, targetVerticalOffset);
    }

    private void ReleasePreviewPanCapture()
    {
        _isPreviewPanActive = false;
        _previewPanPointerId = 0;
        _activePanScrollViewer?.ReleasePointerCaptures();
        _activePanScrollViewer = null;
    }

    private static bool IsControlKeyPressed()
    {
        return IsKeyPressed(VirtualKey.Control);
    }

    private static bool IsKeyPressed(VirtualKey key)
    {
        return Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);
    }

    private void RootLayout_Loaded(object sender, RoutedEventArgs e)
    {
        AttachXamlRoot();
        RefreshDisplayScale();
        EnsurePreviewImageVisual();
        QueueAttachPreviewNumberBoxEditorHandlers();
    }

    private void RootLayout_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachPreviewImageVisual();
        DetachXamlRoot();
        DetachPreviewNumberBoxEditorHandlers();
    }

    private void RootLayout_ActualThemeChanged(FrameworkElement sender, object args)
    {
        QueueAttachPreviewNumberBoxEditorHandlers();
    }

    private void AttachXamlRoot()
    {
        if (ReferenceEquals(_observedXamlRoot, RootLayout.XamlRoot))
        {
            return;
        }

        DetachXamlRoot();
        _observedXamlRoot = RootLayout.XamlRoot;
        if (_observedXamlRoot is not null)
        {
            _observedXamlRoot.Changed += ObservedXamlRoot_Changed;
        }
    }

    private void DetachXamlRoot()
    {
        if (_observedXamlRoot is null)
        {
            return;
        }

        _observedXamlRoot.Changed -= ObservedXamlRoot_Changed;
        _observedXamlRoot = null;
    }

    private void ObservedXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        RefreshDisplayScale();
    }

    private void RefreshDisplayScale()
    {
        AttachXamlRoot();
        ViewModel.UpdateDisplayScale(RootLayout.XamlRoot?.RasterizationScale ?? 1.0);
    }

    private void PreviewImageHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePreviewImageVisualSize();
        UpdateWipeClipAndOverlay();
    }

    private void EnsurePreviewImageVisual()
    {
        if (PreviewImageHost.XamlRoot is null)
        {
            return;
        }

        if (_singlePreviewContainerVisual is not null
            && _wipeLayerAContainerVisual is not null
            && _wipeLayerBContainerVisual is not null
            && _previewImageVisual is not null
            && _previewImageVisualB is not null
            && _previewImageVisualDiff is not null
            && _sideBySideVisualA is not null
            && _sideBySideVisualB is not null
            && _previewImageBrush is not null
            && _previewImageBrushB is not null
            && _previewImageBrushDiff is not null
            && _previewGraphicsDevice is not null)
        {
            return;
        }

        DetachPreviewImageVisual();
        var compositor = ElementCompositionPreview.GetElementVisual(PreviewImageHost).Compositor;
        _previewCanvasDevice ??= CanvasDevice.GetSharedDevice();
        _previewGraphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _previewCanvasDevice);

        var interpolationMode = GetPreviewBitmapInterpolationMode();
        _previewImageBrush = CreatePreviewSurfaceBrush(compositor, interpolationMode);
        _previewImageBrushB = CreatePreviewSurfaceBrush(compositor, interpolationMode);
        _previewImageBrushDiff = CreatePreviewSurfaceBrush(compositor, interpolationMode);

        _singlePreviewContainerVisual = compositor.CreateContainerVisual();

        _wipeVisualAClip = compositor.CreateInsetClip();
        _wipeLayerAContainerVisual = compositor.CreateContainerVisual();
        _previewImageVisual = compositor.CreateSpriteVisual();
        _previewImageVisual.Brush = _previewImageBrush;
        _wipeLayerAContainerVisual.Children.InsertAtTop(_previewImageVisual);

        _wipeVisualBClip = compositor.CreateInsetClip();
        _wipeLayerBContainerVisual = compositor.CreateContainerVisual();
        _wipeLayerBContainerVisual.Clip = _wipeVisualBClip;
        _wipeLayerBContainerVisual.IsVisible = false;
        _previewImageVisualB = compositor.CreateSpriteVisual();
        _previewImageVisualB.Brush = _previewImageBrushB;
        _wipeLayerBContainerVisual.Children.InsertAtTop(_previewImageVisualB);

        _previewImageVisualDiff = compositor.CreateSpriteVisual();
        _previewImageVisualDiff.Brush = _previewImageBrushDiff;
        _previewImageVisualDiff.IsVisible = false;

        _singlePreviewContainerVisual.Children.InsertAtTop(_wipeLayerAContainerVisual);
        _singlePreviewContainerVisual.Children.InsertAtTop(_wipeLayerBContainerVisual);
        _singlePreviewContainerVisual.Children.InsertAtTop(_previewImageVisualDiff);
        ElementCompositionPreview.SetElementChildVisual(PreviewImageHost, _singlePreviewContainerVisual);

        _sideBySideVisualA = compositor.CreateSpriteVisual();
        _sideBySideVisualA.Brush = _previewImageBrush;
        ElementCompositionPreview.SetElementChildVisual(LeftCompareImageHost, _sideBySideVisualA);

        _sideBySideVisualB = compositor.CreateSpriteVisual();
        _sideBySideVisualB.Brush = _previewImageBrushB;
        ElementCompositionPreview.SetElementChildVisual(RightCompareImageHost, _sideBySideVisualB);

        UpdateCompareVisualPresentation();
        UpdatePreviewImageVisualSize();
        UpdateWipeClipAndOverlay();
    }

    private static CompositionSurfaceBrush CreatePreviewSurfaceBrush(
        Compositor compositor,
        CompositionBitmapInterpolationMode interpolationMode)
    {
        var brush = compositor.CreateSurfaceBrush();
        brush.BitmapInterpolationMode = interpolationMode;
        brush.Stretch = CompositionStretch.Fill;
        brush.SnapToPixels = true;
        return brush;
    }

    private CompositionBitmapInterpolationMode GetPreviewBitmapInterpolationMode()
    {
        return string.Equals(ViewModel.ScalingAlgorithm, "nearest", StringComparison.OrdinalIgnoreCase)
            ? CompositionBitmapInterpolationMode.NearestNeighbor
            : CompositionBitmapInterpolationMode.Linear;
    }

    private CanvasImageInterpolation GetCanvasImageInterpolation()
    {
        return string.Equals(ViewModel.ScalingAlgorithm, "nearest", StringComparison.OrdinalIgnoreCase)
            ? CanvasImageInterpolation.NearestNeighbor
            : CanvasImageInterpolation.Linear;
    }

    private void DetachPreviewImageVisual()
    {
        if (_singlePreviewContainerVisual is null
            && _wipeLayerAContainerVisual is null
            && _wipeLayerBContainerVisual is null
            && _previewImageVisual is null
            && _previewImageVisualB is null
            && _previewImageVisualDiff is null
            && _sideBySideVisualA is null
            && _sideBySideVisualB is null
            && _previewImageBrush is null
            && _previewImageBrushB is null
            && _previewImageBrushDiff is null
            && _previewGraphicsDevice is null
            && _previewDrawingSurface is null
            && _previewDrawingSurfaceB is null
            && _previewDrawingSurfaceDiff is null
            && _cachedBitmapA is null
            && _cachedBitmapB is null)
        {
            return;
        }

        ElementCompositionPreview.SetElementChildVisual(PreviewImageHost, null);
        ElementCompositionPreview.SetElementChildVisual(LeftCompareImageHost, null);
        ElementCompositionPreview.SetElementChildVisual(RightCompareImageHost, null);
        DisposeCachedBitmaps();
        ClearPreviewDrawingSurface();
        _wipeVisualAClip?.Dispose();
        _wipeVisualBClip?.Dispose();
        _wipeLayerAContainerVisual?.Dispose();
        _wipeLayerBContainerVisual?.Dispose();
        _singlePreviewContainerVisual?.Dispose();
        _previewImageVisual?.Dispose();
        _previewImageVisualB?.Dispose();
        _previewImageVisualDiff?.Dispose();
        _sideBySideVisualA?.Dispose();
        _sideBySideVisualB?.Dispose();
        _previewImageBrush?.Dispose();
        _previewImageBrushB?.Dispose();
        _previewImageBrushDiff?.Dispose();
        _previewGraphicsDevice?.Dispose();
        _wipeVisualAClip = null;
        _wipeVisualBClip = null;
        _wipeLayerAContainerVisual = null;
        _wipeLayerBContainerVisual = null;
        _singlePreviewContainerVisual = null;
        _previewImageVisual = null;
        _previewImageVisualB = null;
        _previewImageVisualDiff = null;
        _sideBySideVisualA = null;
        _sideBySideVisualB = null;
        _previewImageBrush = null;
        _previewImageBrushB = null;
        _previewImageBrushDiff = null;
        _previewGraphicsDevice = null;
        _previewCanvasDevice = null;
    }

    private void DisposeCachedBitmaps()
    {
        _cachedBitmapA?.Dispose();
        _cachedBitmapA = null;
        _cachedBitmapB?.Dispose();
        _cachedBitmapB = null;
    }

    private TimeSpan UpdatePreviewDrawingSurface(byte[] pixels, int width, int height, PreviewDisplayFilter filter)
    {
        EnsurePreviewImageVisual();
        if (_previewCanvasDevice is null || _previewGraphicsDevice is null || _previewImageBrush is null)
        {
            return TimeSpan.Zero;
        }

        if (width <= 0 || height <= 0 || pixels.Length == 0)
        {
            _cachedBitmapA?.Dispose();
            _cachedBitmapA = null;
            ClearPrimaryPreviewDrawingSurface();
            return TimeSpan.Zero;
        }

        var surfaceStopwatch = Stopwatch.StartNew();
        var nextBitmap = CanvasBitmap.CreateFromBytes(
            _previewCanvasDevice,
            pixels,
            width,
            height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96,
            CanvasAlphaMode.Ignore);
        _cachedBitmapA?.Dispose();
        _cachedBitmapA = nextBitmap;

        RedrawPrimarySurfaceFromCachedBitmap(filter);
        surfaceStopwatch.Stop();
        return surfaceStopwatch.Elapsed;
    }

    private TimeSpan UpdateSecondaryPreviewDrawingSurface(byte[] pixels, int width, int height, PreviewDisplayFilter filter)
    {
        EnsurePreviewImageVisual();
        if (_previewCanvasDevice is null || _previewGraphicsDevice is null || _previewImageBrushB is null)
        {
            return TimeSpan.Zero;
        }

        if (width <= 0 || height <= 0 || pixels.Length == 0)
        {
            _cachedBitmapB?.Dispose();
            _cachedBitmapB = null;
            ClearSecondaryPreviewDrawingSurface();
            return TimeSpan.Zero;
        }

        var surfaceStopwatch = Stopwatch.StartNew();
        var nextBitmap = CanvasBitmap.CreateFromBytes(
            _previewCanvasDevice,
            pixels,
            width,
            height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96,
            CanvasAlphaMode.Ignore);
        _cachedBitmapB?.Dispose();
        _cachedBitmapB = nextBitmap;

        RedrawSecondarySurfaceFromCachedBitmap(filter);
        surfaceStopwatch.Stop();
        return surfaceStopwatch.Elapsed;
    }

    private void RedrawPrimarySurfaceFromCachedBitmap(PreviewDisplayFilter filter)
    {
        if (_cachedBitmapA is null || _previewGraphicsDevice is null || _previewImageBrush is null)
        {
            return;
        }

        var width = (int)_cachedBitmapA.SizeInPixels.Width;
        var height = (int)_cachedBitmapA.SizeInPixels.Height;
        EnsurePreviewDrawingSurface(width, height);
        if (_previewDrawingSurface is null)
        {
            return;
        }

        using var drawingSession = CanvasComposition.CreateDrawingSession(_previewDrawingSurface);
        PreviewDisplayFilters.Draw(drawingSession, _cachedBitmapA, filter, _lastFrameData?.Properties);
    }

    private void RedrawSecondarySurfaceFromCachedBitmap(PreviewDisplayFilter filter)
    {
        if (_cachedBitmapB is null || _previewGraphicsDevice is null || _previewImageBrushB is null)
        {
            return;
        }

        var width = (int)_cachedBitmapB.SizeInPixels.Width;
        var height = (int)_cachedBitmapB.SizeInPixels.Height;
        EnsureSecondaryPreviewDrawingSurface(width, height);
        if (_previewDrawingSurfaceB is null)
        {
            return;
        }

        using var drawingSession = CanvasComposition.CreateDrawingSession(_previewDrawingSurfaceB);
        PreviewDisplayFilters.Draw(drawingSession, _cachedBitmapB, filter, _secondaryLastFrameData?.Properties);
    }

    private TimeSpan RedrawDifferenceSurface()
    {
        EnsurePreviewImageVisual();
        if (_cachedBitmapA is null
            || _cachedBitmapB is null
            || _previewGraphicsDevice is null
            || _previewImageBrushDiff is null)
        {
            return TimeSpan.Zero;
        }

        var layout = PreviewCompareCoordinator.ComputeAlignedCanvasLayout(
            (int)_cachedBitmapA.SizeInPixels.Width,
            (int)_cachedBitmapA.SizeInPixels.Height,
            (int)_cachedBitmapB.SizeInPixels.Width,
            (int)_cachedBitmapB.SizeInPixels.Height,
            ViewModel.SizeAlignment);
        EnsureDifferencePreviewDrawingSurface(layout.CanvasWidth, layout.CanvasHeight);
        if (_previewDrawingSurfaceDiff is null)
        {
            return TimeSpan.Zero;
        }

        var stopwatch = Stopwatch.StartNew();
        using (var drawingSession = CanvasComposition.CreateDrawingSession(_previewDrawingSurfaceDiff))
        {
            PreviewDisplayFilters.DrawDifference(
                drawingSession,
                _cachedBitmapA,
                _cachedBitmapB,
                _previewFilter,
                ViewModel.DifferenceGain,
                ViewModel.SizeAlignment,
                GetCanvasImageInterpolation(),
                _lastFrameData?.Properties,
                _secondaryLastFrameData?.Properties,
                ViewModel.DifferenceStyle);
        }

        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private void UpdateCompareVisualPresentation()
    {
        var isFullFlipActive = ViewModel.IsCompareModeActive
            && (ViewModel.IsTemporaryFlipToB || ViewModel.IsFlickerMode);
        var showingBFull = isFullFlipActive && ViewModel.IsShowingSecondaryInFlip;
        var isWipeActive = ViewModel.IsWipeMode && !ViewModel.IsTemporaryFlipToB;
        var isDiffActive = ViewModel.IsDifferenceMode && !ViewModel.IsTemporaryFlipToB;

        if (_wipeLayerAContainerVisual is not null)
        {
            _wipeLayerAContainerVisual.IsVisible = !isDiffActive && !showingBFull;
            _wipeLayerAContainerVisual.Clip = isWipeActive ? _wipeVisualAClip : null;
        }

        if (_previewImageVisual is not null)
        {
            _previewImageVisual.IsVisible = !isDiffActive && !showingBFull;
        }

        if (_wipeLayerBContainerVisual is not null)
        {
            _wipeLayerBContainerVisual.IsVisible = isWipeActive || showingBFull;
            _wipeLayerBContainerVisual.Clip = isWipeActive ? _wipeVisualBClip : null;
        }

        if (_previewImageVisualB is not null)
        {
            _previewImageVisualB.IsVisible = isWipeActive || showingBFull;
        }

        if (_previewImageVisualDiff is not null)
        {
            _previewImageVisualDiff.IsVisible = isDiffActive;
        }
    }

    private void UpdateWipeClipAndOverlay()
    {
        var imageWidth = Math.Max(0, ViewModel.PreviewImageWidth);
        var splitRatio = PreviewCompareCoordinator.ClampSplitRatio(ViewModel.SplitRatio);
        var splitX = (float)Math.Clamp(splitRatio * imageWidth, 0.0, imageWidth);
        if (_wipeVisualAClip is not null)
        {
            _wipeVisualAClip.RightInset = (float)Math.Clamp(imageWidth - splitX, 0.0, imageWidth);
        }

        if (_wipeVisualBClip is not null)
        {
            _wipeVisualBClip.LeftInset = splitX;
        }

        if (!ViewModel.IsWipeMode || imageWidth <= 0)
        {
            return;
        }

        var viewportWidth = PreviewScrollViewer.ViewportWidth > 0
            ? PreviewScrollViewer.ViewportWidth
            : PreviewViewportHost.ActualWidth;
        var dividerX = PreviewCompareCoordinator.ComputeViewportDividerX(
            splitRatio,
            GetSinglePreviewImageLeftInViewport(),
            PreviewScrollViewer.HorizontalOffset,
            imageWidth);
        var halfHandle = WipeDividerHandle.Width / 2.0;
        var clampedX = viewportWidth > 0
            ? Math.Clamp(dividerX, halfHandle, Math.Max(halfHandle, viewportWidth - halfHandle))
            : dividerX;
        var isOffscreen = viewportWidth > 0 && (dividerX < halfHandle || dividerX > viewportWidth - halfHandle);
        WipeDividerTransform.X = clampedX - halfHandle;
        WipeDividerHandle.Opacity = isOffscreen ? 0.55 : 1.0;
    }

    private void BuildCompareModeMenu()
    {
        CompareModeMenu.Items.Clear();
        foreach (var mode in PreviewCompareModeCycle)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = ViewModel.Texts.VapourSynthPreviewCompareModeLabel(mode),
                Tag = mode,
                GroupName = nameof(CompareModeMenu)
            };
            item.Click += CompareModeMenuItem_Click;
            CompareModeMenu.Items.Add(item);
        }

        UpdateCompareModeSelection();
    }

    private void UpdateCompareModeSelection()
    {
        var modeLabel = ViewModel.Texts.VapourSynthPreviewCompareModeLabel(ViewModel.CompareMode);
        CompareModeButton.Content = ViewModel.CompareMode == PreviewCompareMode.Single
            ? ViewModel.Texts.VapourSynthPreviewCompareHeader
            : modeLabel;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(CompareModeButton, modeLabel);
        foreach (RadioMenuFlyoutItem item in CompareModeMenu.Items)
        {
            item.IsChecked = Equals(item.Tag, ViewModel.CompareMode);
        }
    }

    private async void CompareModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(CompareModeMenuItem_Click),
            async () =>
            {
                await SetCompareModeAsync((PreviewCompareMode)((RadioMenuFlyoutItem)sender).Tag);
                RequestPreviewFocus();
            });
    }

    private async void CompareModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(CompareModeComboBox_SelectionChanged),
            async () =>
            {
                if (_isInternalControlUpdate
                    || CompareModeComboBox.SelectedItem is not PreviewCompareModeOption option)
                {
                    return;
                }

                await SetCompareModeAsync(option.Mode);
                RequestPreviewFocus();
            });
    }

    private async void CompareOutputAComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(CompareOutputAComboBox_SelectionChanged),
            async () =>
            {
                if (_isInternalControlUpdate
                    || CompareOutputAComboBox.SelectedItem is not VapourSynthPreviewOutputOption option)
                {
                    return;
                }

                await SelectOutputAsync(option.Info);
            });
    }

    private async void CompareOutputBComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(CompareOutputBComboBox_SelectionChanged),
            async () =>
            {
                if (_isInternalControlUpdate
                    || CompareOutputBComboBox.SelectedItem is not VapourSynthPreviewOutputOption option)
                {
                    return;
                }

                await SelectSecondaryOutputAsync(option.Info);
            });
    }

    private async void SwapCompareOutputsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(SwapCompareOutputsButton_Click),
            async () =>
            {
                await SwapCompareOutputsAsync();
                RequestPreviewFocus();
            });
    }

    private void CenterWipeSplitButton_Click(object sender, RoutedEventArgs e)
    {
        CenterWipeSplitInViewport();
        RequestPreviewFocus();
    }

    private void DifferenceGainComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInternalControlUpdate
            || DifferenceGainComboBox.SelectedItem is not PreviewDifferenceGainOption option)
        {
            return;
        }

        ViewModel.DifferenceGain = option.Gain;
        if (ViewModel.IsDifferenceMode && _cachedBitmapA is not null && _cachedBitmapB is not null)
        {
            RedrawDifferenceSurface();
        }
    }

    private void DifferenceStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInternalControlUpdate
            || DifferenceStyleComboBox.SelectedItem is not PreviewDifferenceStyleOption option)
        {
            return;
        }

        ViewModel.DifferenceStyle = option.Style;
        if (ViewModel.IsDifferenceMode && _cachedBitmapA is not null && _cachedBitmapB is not null)
        {
            RedrawDifferenceSurface();
        }
    }

    private void FlickerIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInternalControlUpdate
            || FlickerIntervalComboBox.SelectedItem is not PreviewFlickerIntervalOption option)
        {
            return;
        }

        ViewModel.FlickerIntervalMs = option.Milliseconds;
        SyncFlickerTimer();
    }

    private void FlickerTimer_Tick(object? sender, object e)
    {
        if (_isClosed || !ViewModel.IsFlickerMode || _isPlaying || ViewModel.IsTemporaryFlipToB)
        {
            _flickerTimer.Stop();
            return;
        }

        ViewModel.IsShowingSecondaryInFlip = !ViewModel.IsShowingSecondaryInFlip;
        UpdateCompareVisualPresentation();
    }

    private void SyncFlickerTimer()
    {
        if (!_isClosed && ViewModel.IsFlickerMode && !_isPlaying && !ViewModel.IsTemporaryFlipToB)
        {
            _flickerTimer.Interval = TimeSpan.FromMilliseconds(ViewModel.FlickerIntervalMs);
            if (!_flickerTimer.IsEnabled)
            {
                _flickerTimer.Start();
            }
        }
        else
        {
            _flickerTimer.Stop();
            if (!ViewModel.IsFlickerMode)
            {
                ViewModel.IsShowingSecondaryInFlip = false;
            }
        }
    }

    private void TabHoldFlickerTimer_Tick(object? sender, object e)
    {
        if (_isClosed || !ViewModel.IsCompareModeActive || !ViewModel.IsTemporaryFlipToB || !IsKeyPressed(VirtualKey.Tab))
        {
            StopTabHoldFlip();
            return;
        }

        ViewModel.IsShowingSecondaryInFlip = !ViewModel.IsShowingSecondaryInFlip;
        UpdateCompareVisualPresentation();
    }

    private void StartTabHoldFlip()
    {
        if (_isClosed || !ViewModel.IsCompareModeActive)
        {
            return;
        }

        if (ViewModel.IsTemporaryFlipToB)
        {
            return;
        }

        ViewModel.IsShowingSecondaryInFlip = true;
        ViewModel.IsTemporaryFlipToB = true;
        SyncFlickerTimer();
        UpdateCompareVisualPresentation();
        UpdatePreviewImageVisualSize();
        UpdateWipeClipAndOverlay();
        _tabHoldFlickerTimer.Start();
    }

    private void StopTabHoldFlip()
    {
        _tabHoldFlickerTimer.Stop();
        if (!ViewModel.IsTemporaryFlipToB)
        {
            return;
        }

        ViewModel.IsTemporaryFlipToB = false;
        if (!ViewModel.IsFlickerMode)
        {
            ViewModel.IsShowingSecondaryInFlip = false;
        }

        SyncFlickerTimer();
        UpdateCompareVisualPresentation();
        UpdatePreviewImageVisualSize();
        UpdateWipeClipAndOverlay();
    }

    private void VapourSynthPreviewWindow_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && ViewModel.IsTemporaryFlipToB)
        {
            StopTabHoldFlip();
        }
    }

    private async void SecondaryFrameOffsetBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInternalControlUpdate || double.IsNaN(sender.Value))
        {
            return;
        }

        await RunWindowEventHandlerAsync(
            nameof(SecondaryFrameOffsetBox_ValueChanged),
            () => SetSecondaryFrameOffsetAsync((int)Math.Round(sender.Value)));
    }

    private async void DecrementFrameOffsetButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(DecrementFrameOffsetButton_Click),
            () => SetSecondaryFrameOffsetAsync(ViewModel.SecondaryFrameOffset - 1));
    }

    private async void IncrementFrameOffsetButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(IncrementFrameOffsetButton_Click),
            () => SetSecondaryFrameOffsetAsync(ViewModel.SecondaryFrameOffset + 1));
    }

    private async void ResetFrameOffsetButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(ResetFrameOffsetButton_Click),
            () => SetSecondaryFrameOffsetAsync(0));
    }

    private void CopyAlignSnippetButton_Click(object sender, RoutedEventArgs e)
    {
        var snippet = PreviewCompareCoordinator.FormatAlignmentScriptSnippet(
            ViewModel.SecondaryFrameOffset,
            ViewModel.SubpixelOffsetX,
            ViewModel.SubpixelOffsetY);
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(snippet);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        SetStatusText(ViewModel.Texts.VapourSynthPreviewCopyAlignSnippetStatus);
    }

    private async void SubpixelOffsetBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInternalControlUpdate || double.IsNaN(sender.Value))
        {
            return;
        }

        await RunWindowEventHandlerAsync(
            nameof(SubpixelOffsetBox_ValueChanged),
            async () =>
            {
                ViewModel.SubpixelOffsetX = double.IsNaN(SubpixelOffsetXBox.Value) ? 0.0 : SubpixelOffsetXBox.Value;
                ViewModel.SubpixelOffsetY = double.IsNaN(SubpixelOffsetYBox.Value) ? 0.0 : SubpixelOffsetYBox.Value;
                if (ViewModel.IsCompareModeActive && _selectedOutputInfo is not null)
                {
                    await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
                }
            });
    }

    private async void ResetSubpixelOffsetButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(ResetSubpixelOffsetButton_Click),
            async () =>
            {
                ViewModel.SubpixelOffsetX = 0.0;
                ViewModel.SubpixelOffsetY = 0.0;
                SyncControls();
                if (ViewModel.IsCompareModeActive && _selectedOutputInfo is not null)
                {
                    await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
                }
            });
    }

    private async void AutoTemporalAlignButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(AutoTemporalAlignButton_Click),
            async () =>
            {
                if (!ViewModel.IsCompareModeActive || _selectedOutputInfo is null || _secondaryOutputInfo is null || ViewModel.IsAutoAlignBusy)
                {
                    return;
                }

                StopPlayback();
                ViewModel.IsAutoAlignBusy = true;
                ViewModel.AlignStatusText = ViewModel.Texts.VapourSynthPreviewTemporalAligningStatus;
                SetStatusText(ViewModel.Texts.VapourSynthPreviewTemporalAligningStatus);

                try
                {
                    var baseFrameB = PreviewCompareCoordinator.ResolveSecondaryFrameNumber(
                        _selectedOutputInfo,
                        _secondaryOutputInfo,
                        ViewModel.CurrentFrame,
                        ViewModel.SelectedOutputSyncMode?.Value,
                        ViewModel.IsTimelineTimeMode,
                        secondaryFrameOffset: 0);

                    var result = await _previewService.AutoAlignTemporalAsync(
                        _selectedOutputInfo.Index,
                        _secondaryOutputInfo.Index,
                        ViewModel.CurrentFrame,
                        baseFrameB,
                        ViewModel.SecondaryFrameOffset,
                        ViewModel.TemporalSearchRadius,
                        verifyMultiAnchor: true,
                        _previewLifetimeToken);

                    ViewModel.SecondaryFrameOffset = result.FrameOffset;
                    var msg = ViewModel.Texts.VapourSynthPreviewTemporalAlignedStatus(
                        result.FrameOffset,
                        result.Confidence,
                        result.ElapsedMs,
                        result.HasDrift,
                        result.DriftSummary);
                    ViewModel.AlignStatusText = msg;
                    SetStatusText(msg);
                    SyncControls();
                    await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var err = ViewModel.Texts.VapourSynthPreviewAlignFailedStatus(ex.Message);
                    ViewModel.AlignStatusText = err;
                    SetStatusText(err);
                }
                finally
                {
                    ViewModel.IsAutoAlignBusy = false;
                }
            });
    }

    private async void AutoSpatialAlignButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(AutoSpatialAlignButton_Click),
            async () =>
            {
                if (!ViewModel.IsCompareModeActive || _selectedOutputInfo is null || _secondaryOutputInfo is null || ViewModel.IsAutoAlignBusy)
                {
                    return;
                }

                StopPlayback();
                ViewModel.IsAutoAlignBusy = true;
                ViewModel.AlignStatusText = ViewModel.Texts.VapourSynthPreviewSpatialAligningStatus;
                SetStatusText(ViewModel.Texts.VapourSynthPreviewSpatialAligningStatus);

                try
                {
                    var frameB = PreviewCompareCoordinator.ResolveSecondaryFrameNumber(
                        _selectedOutputInfo,
                        _secondaryOutputInfo,
                        ViewModel.CurrentFrame,
                        ViewModel.SelectedOutputSyncMode?.Value,
                        ViewModel.IsTimelineTimeMode,
                        ViewModel.SecondaryFrameOffset);

                    var result = await _previewService.AutoAlignSpatialAsync(
                        _selectedOutputInfo.Index,
                        _secondaryOutputInfo.Index,
                        ViewModel.CurrentFrame,
                        frameB,
                        _previewLifetimeToken);

                    ViewModel.SubpixelOffsetX = result.OffsetX;
                    ViewModel.SubpixelOffsetY = result.OffsetY;
                    var msg = ViewModel.Texts.VapourSynthPreviewSpatialAlignedStatus(
                        result.OffsetX,
                        result.OffsetY,
                        result.Confidence,
                        result.ElapsedMs);
                    ViewModel.AlignStatusText = msg;
                    SetStatusText(msg);
                    SyncControls();
                    await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var err = ViewModel.Texts.VapourSynthPreviewAlignFailedStatus(ex.Message);
                    ViewModel.AlignStatusText = err;
                    SetStatusText(err);
                }
                finally
                {
                    ViewModel.IsAutoAlignBusy = false;
                }
            });
    }

    private void PipetteToggleButton_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.IsPipetteEnabled = true;
        if (string.IsNullOrWhiteSpace(ViewModel.PipetteReadoutText))
        {
            ViewModel.PipetteReadoutText = ViewModel.Texts.VapourSynthPreviewPipetteIdleText;
        }
        RequestPreviewFocus();
    }

    private void PipetteToggleButton_Unchecked(object sender, RoutedEventArgs e)
    {
        ViewModel.IsPipetteEnabled = false;
        ViewModel.IsPipetteLocked = false;
        _pipetteLockedX = -1;
        _pipetteLockedY = -1;
        _pipetteSampleCts?.Cancel();
        RequestPreviewFocus();
    }

    private void UpdateInspectorColumnWidth()
    {
        if (!ViewModel.IsFramePropsVisible)
        {
            PropsColumn.Width = new GridLength(0);
            return;
        }

        if (ViewModel.IsScopesTabActive)
        {
            var windowWidth = RootLayout.ActualWidth > 0 ? RootLayout.ActualWidth : 1280;
            var scopeWidth = Math.Clamp(Math.Round(windowWidth * 0.36), 460, 680);
            PropsColumn.Width = new GridLength(scopeWidth);
            return;
        }

        PropsColumn.Width = new GridLength(340);
    }

    private void SetActiveInspectorTab(string tab)
    {
        ViewModel.ActiveInspectorTab = tab;
        InspectorPropsTabButton.IsChecked = ViewModel.IsPropsTabActive;
        InspectorScopesTabButton.IsChecked = ViewModel.IsScopesTabActive;
        InspectorBasketTabButton.IsChecked = ViewModel.IsBasketTabActive;
        InspectorWorstTabButton.IsChecked = ViewModel.IsWorstTabActive;
        InspectorScenesTabButton.IsChecked = ViewModel.IsScenesTabActive;
        UpdateInspectorColumnWidth();
        if (ViewModel.IsScopesTabActive)
        {
            InvalidateScopeCanvasIfVisible();
        }
    }

    private void InspectorWorstTabButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveInspectorTab("worst");
    }

    private void InspectorScenesTabButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveInspectorTab("scenes");
    }

    private async void StartWorstScanButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(StartWorstScanButton_Click),
            async () =>
            {
                if (!ViewModel.CanStartWorstScan || _selectedOutputInfo is null)
                {
                    return;
                }

                EnsureSecondaryOutputSelection(_selectedOutputInfo);
                var effectiveSecondary = _secondaryOutputInfo
                    ?? (string.Equals(ViewModel.WorstMetric, "cambi", StringComparison.OrdinalIgnoreCase) ? _selectedOutputInfo : null);
                if (effectiveSecondary is null)
                {
                    return;
                }

                StopPlayback();
                ViewModel.IsWorstScanBusy = true;
                try
                {
                    var fps = _selectedOutputInfo.FpsNumerator > 0 && _selectedOutputInfo.FpsDenominator > 0
                        ? _selectedOutputInfo.FpsNumerator / (double)_selectedOutputInfo.FpsDenominator
                        : 24.0;
                    var plan = PreviewWorstFramePlanner.BuildSamplingPlan(
                        _selectedOutputInfo.TotalFrames,
                        fps,
                        ViewModel.WorstSkipMinutes,
                        ViewModel.WorstSampleCount);

                    if (plan.SampleFrames.Count == 0)
                    {
                        return;
                    }

                    var framesB = plan.SampleFrames
                        .Select(f => ReferenceEquals(effectiveSecondary, _selectedOutputInfo)
                            ? f
                            : PreviewCompareCoordinator.ResolveSecondaryFrameNumber(
                                _selectedOutputInfo,
                                effectiveSecondary,
                                f,
                                ViewModel.SelectedOutputSyncMode?.Value,
                                ViewModel.IsTimelineTimeMode,
                                ViewModel.SecondaryFrameOffset))
                        .ToArray();

                    var scanningMsg = ViewModel.Texts.VapourSynthPreviewWorstScanningStatus(
                        plan.SampleFrames.Count,
                        plan.AppliedSkipMinutes,
                        plan.AdjustedForShortVideo);
                    ViewModel.WorstScanStatusText = scanningMsg;
                    SetStatusText(scanningMsg);

                    var result = await _previewService.ScanWorstFramesAsync(
                        _selectedOutputInfo.Index,
                        effectiveSecondary.Index,
                        plan.SampleFrames,
                        framesB,
                        ViewModel.WorstMetric,
                        topN: 15,
                        ViewModel.SubpixelOffsetX,
                        ViewModel.SubpixelOffsetY,
                        _previewLifetimeToken);

                    ViewModel.WorstFrameResults.Clear();
                    foreach (var entry in result.WorstFrames)
                    {
                        var tc = TimeSpan.FromSeconds(entry.FrameNumber / fps);
                        var tcStr = tc.TotalHours >= 1 ? tc.ToString(@"h\:mm\:ss\.fff") : tc.ToString(@"mm\:ss\.fff");
                        var typeSuffix = string.IsNullOrWhiteSpace(entry.FrameType) ? string.Empty : $" [{entry.FrameType}]";
                        ViewModel.WorstFrameResults.Add(new PreviewWorstFrameRowItem(
                            entry.FrameNumber,
                            entry.SecondaryFrameNumber,
                            entry.Score,
                            $"{entry.Score:0.##}",
                            $"#{entry.FrameNumber}{typeSuffix} · {tcStr}"));
                    }
                    ViewModel.NotifyWorstScanResultsChanged();

                    var summary = ViewModel.Texts.VapourSynthPreviewWorstSummary(
                        result.MetricUsed,
                        result.BackendUsed,
                        result.SampledCount,
                        result.MedianScore,
                        result.P10Score,
                        result.MinScore,
                        result.ElapsedMs);
                    ViewModel.WorstScanStatusText = summary;
                    SetStatusText(summary);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var err = ViewModel.Texts.VapourSynthPreviewWorstFailedStatus(ex.Message);
                    ViewModel.WorstScanStatusText = err;
                    SetStatusText(err);
                }
                finally
                {
                    ViewModel.IsWorstScanBusy = false;
                }
            });
    }

    private async void WorstFramesListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PreviewWorstFrameRowItem item || _selectedOutputInfo is null)
        {
            return;
        }

        await RunWindowEventHandlerAsync(
            nameof(WorstFramesListView_ItemClick),
            async () =>
            {
                if (!ViewModel.IsCompareModeActive && ViewModel.CanCompareOutputs)
                {
                    await SetCompareModeAsync(PreviewCompareMode.Wipe);
                }
                await RequestFrameAsync(_selectedOutputInfo, item.FrameNumber);
            });
    }

    private void SendWorstToBasketButton_Click(object sender, RoutedEventArgs e)
    {
        var count = 0;
        foreach (var item in ViewModel.WorstFrameResults.Take(10))
        {
            AddBookmarkedFrame(item.FrameNumber);
            count++;
        }

        SetStatusText(ViewModel.Texts.VapourSynthPreviewWorstAddedToBasketStatus(count));
        SetActiveInspectorTab("basket");
    }

    private void MarkSceneInAtCurrentFrame()
    {
        if (_selectedOutputInfo is null)
        {
            return;
        }

        ViewModel.PendingSceneStartFrame = ViewModel.CurrentFrame;
        SetStatusText(ViewModel.PendingSceneInText);
    }

    private void MarkSceneOutAtCurrentFrame()
    {
        if (_selectedOutputInfo is null)
        {
            return;
        }

        var end = ViewModel.CurrentFrame;
        var start = ViewModel.PendingSceneStartFrame ?? end;
        if (start > end)
        {
            (start, end) = (end, start);
        }

        var label = $"[{start}, {end}] ({end - start + 1}f)";
        var item = new PreviewSceneRangeItem(start, end, label);
        var insertAt = 0;
        while (insertAt < ViewModel.SceneRanges.Count && ViewModel.SceneRanges[insertAt].StartFrame < start)
        {
            insertAt++;
        }
        ViewModel.SceneRanges.Insert(insertAt, item);
        ViewModel.PendingSceneStartFrame = null;
        ViewModel.NotifySceneRangesChanged();
    }

    private void SceneMarkInButton_Click(object sender, RoutedEventArgs e)
    {
        MarkSceneInAtCurrentFrame();
        RequestPreviewFocus();
    }

    private void SceneMarkOutButton_Click(object sender, RoutedEventArgs e)
    {
        MarkSceneOutAtCurrentFrame();
        RequestPreviewFocus();
    }

    private void SceneClearButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SceneRanges.Clear();
        ViewModel.PendingSceneStartFrame = null;
        ViewModel.NotifySceneRangesChanged();
    }

    private async void SceneRangesListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PreviewSceneRangeItem item || _selectedOutputInfo is null)
        {
            return;
        }

        await RunWindowEventHandlerAsync(
            nameof(SceneRangesListView_ItemClick),
            () => RequestFrameAsync(_selectedOutputInfo, item.StartFrame));
    }

    private void SceneRemoveRangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PreviewSceneRangeItem item })
        {
            ViewModel.SceneRanges.Remove(item);
            ViewModel.NotifySceneRangesChanged();
        }
    }

    private IReadOnlyList<PreviewSceneRange> GetDomainSceneRanges() =>
        ViewModel.SceneRanges.Select(static r => new PreviewSceneRange(r.StartFrame, r.EndFrame, r.Label)).ToArray();

    private void CopyTextToClipboardWithStatus(string text, string statusText)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        SetStatusText(statusText);
    }

    private void CopyReplaceRangesButton_Click(object sender, RoutedEventArgs e)
    {
        CopyTextToClipboardWithStatus(
            PreviewSceningFormatter.FormatReplaceRanges(GetDomainSceneRanges()),
            ViewModel.Texts.VapourSynthPreviewSceneCopiedStatus);
    }

    private void CopyEncoderZonesButton_Click(object sender, RoutedEventArgs e)
    {
        CopyTextToClipboardWithStatus(
            PreviewSceningFormatter.FormatEncoderZones(GetDomainSceneRanges()),
            ViewModel.Texts.VapourSynthPreviewSceneCopiedStatus);
    }

    private void CopyAv1anZonesButton_Click(object sender, RoutedEventArgs e)
    {
        CopyTextToClipboardWithStatus(
            PreviewSceningFormatter.FormatAv1anZones(GetDomainSceneRanges()),
            ViewModel.Texts.VapourSynthPreviewSceneCopiedStatus);
    }

    private async void AutoCropButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(AutoCropButton_Click),
            async () =>
            {
                if (_selectedOutputInfo is not { } output)
                {
                    return;
                }

                StopPlayback();
                SetStatusText(ViewModel.Texts.VapourSynthPreviewAutoCropRunningStatus);
                try
                {
                    var result = await _previewService.AutoDetectCropAsync(
                        output.Index,
                        sampleCount: 12,
                        _previewLifetimeToken);
                    if (!ReferenceEquals(output, _selectedOutputInfo))
                    {
                        return;
                    }

                    var (left, top, right, bottom) = (result.Left, result.Top, result.Right, result.Bottom);
                    var outputState = GetOrCreateOutputState(output);
                    outputState.AutoCropRepair = null;
                    var note = string.Empty;
                    if (result.CropRepair is { Failure: null, Edges: [var exactLeft, var exactTop, var exactRight, var exactBottom] } repair)
                    {
                        (left, top, right, bottom) = (exactLeft, exactTop, exactRight, exactBottom);
                        outputState.AutoCropRepair = repair;
                        note = " " + ViewModel.Texts.VapourSynthRepairCropRemainder;
                    }
                    else if (result.CropRepair?.Failure is not null)
                    {
                        note = " " + ViewModel.Texts.VapourSynthPreviewAutoCropRemainderUnavailable;
                    }

                    outputState.RelativeCrop.Left = result.Left;
                    outputState.RelativeCrop.Top = result.Top;
                    outputState.RelativeCrop.Right = result.Right;
                    outputState.RelativeCrop.Bottom = result.Bottom;
                    outputState.AbsoluteCrop.Left = result.Left;
                    outputState.AbsoluteCrop.Top = result.Top;
                    outputState.AbsoluteCrop.Width = Math.Max(1, output.Width - result.Left - result.Right);
                    outputState.AbsoluteCrop.Height = Math.Max(1, output.Height - result.Top - result.Bottom);
                    SyncControls();
                    RefreshDisplayedFrame();
                    SetStatusText(ViewModel.Texts.VapourSynthPreviewAutoCropDoneStatus(
                        left, top, right, bottom, result.ElapsedMs) + note);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SetStatusText(ViewModel.Texts.VapourSynthPreviewAutoCropFailedStatus(ex.Message));
                }
            });
    }

    private async Task ApplySmartScriptEditOrFallbackCopyAsync(
        Func<string, SmartScriptEditResult> rewriter,
        string fallbackClipboardSnippet)
    {
        if (SmartEditScriptAsync is null)
        {
            CopyTextToClipboardWithStatus(fallbackClipboardSnippet, ViewModel.Texts.VapourSynthPreviewNoBoundEditorFallbackStatus);
            return;
        }

        var result = await SmartEditScriptAsync(rewriter);
        if (result is null)
        {
            CopyTextToClipboardWithStatus(fallbackClipboardSnippet, ViewModel.Texts.VapourSynthPreviewNoBoundEditorFallbackStatus);
            return;
        }

        SetStatusText(ViewModel.Texts.VapourSynthPreviewSmartInsertedStatus(
            result.ResolvedVariableName,
            result.TargetLineNumber,
            result.UpdatedInPlace));
    }

    private async void InsertAlignSnippetButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(InsertAlignSnippetButton_Click),
            async () =>
            {
                var targetIndex = _secondaryOutputInfo?.Index ?? _selectedOutputInfo?.Index ?? 0;
                var fallback = PreviewCompareCoordinator.FormatAlignmentScriptSnippet(
                    ViewModel.SecondaryFrameOffset,
                    ViewModel.SubpixelOffsetX,
                    ViewModel.SubpixelOffsetY);
                await ApplySmartScriptEditOrFallbackCopyAsync(
                    script => VapourSynthSmartScriptEditor.ApplyAlignment(
                        script,
                        targetIndex,
                        ViewModel.SecondaryFrameOffset,
                        ViewModel.SubpixelOffsetX,
                        ViewModel.SubpixelOffsetY),
                    fallback);
            });
    }

    private async void InsertCropToEditorButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(InsertCropToEditorButton_Click),
            async () =>
            {
                if (_selectedOutputInfo is null)
                {
                    return;
                }

                var outputState = GetOrCreateOutputState(_selectedOutputInfo);
                var isAbs = ViewModel.IsAbsoluteCropMode;
                var left = (int)Math.Max(0, isAbs ? outputState.AbsoluteCrop.Left : outputState.RelativeCrop.Left);
                var top = (int)Math.Max(0, isAbs ? outputState.AbsoluteCrop.Top : outputState.RelativeCrop.Top);
                var widthOrRight = (int)Math.Max(0, isAbs ? outputState.AbsoluteCrop.Width : outputState.RelativeCrop.Right);
                var heightOrBottom = (int)Math.Max(0, isAbs ? outputState.AbsoluteCrop.Height : outputState.RelativeCrop.Bottom);
                var fallback = BuildCropCommandSnippet(useSnippetPlaceholder: false);
                if (outputState.AutoCropRepair is { } repair)
                {
                    var outputIndex = _selectedOutputInfo.Index;
                    await ApplySmartScriptEditOrFallbackCopyAsync(
                        script => VapourSynthRepairScriptBuilder.Build(script, outputIndex, [repair]), fallback);
                    return;
                }

                await ApplySmartScriptEditOrFallbackCopyAsync(
                    script => VapourSynthSmartScriptEditor.ApplyCrop(
                        script,
                        _selectedOutputInfo.Index,
                        isAbs,
                        left,
                        top,
                        widthOrRight,
                        heightOrBottom),
                    fallback);
            });
    }

    private async void CopyCropBbmodButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(CopyCropBbmodButton_Click),
            async () =>
            {
                if (_selectedOutputInfo is null)
                {
                    return;
                }

                var outputState = GetOrCreateOutputState(_selectedOutputInfo);
                if (outputState.AutoCropRepair is { } crop)
                {
                    var outputIndex = _selectedOutputInfo.Index;
                    PreviewRepairSuggestion[] repairs = [crop, new("edge", crop.Frames,
                        ["import awsmfunc as awf", "_fe_repair_clip = awf.bbmod(_fe_repair_clip, top=2, bottom=2, left=2, right=2, thresh=32, blur=20)"], [])];
                    await ApplySmartScriptEditOrFallbackCopyAsync(
                        script => VapourSynthRepairScriptBuilder.Build(script, outputIndex, repairs),
                        VapourSynthRepairScriptBuilder.FormatSnippet(repairs));
                    return;
                }

                var left = (int)Math.Max(0, outputState.RelativeCrop.Left);
                var top = (int)Math.Max(0, outputState.RelativeCrop.Top);
                var right = (int)Math.Max(0, outputState.RelativeCrop.Right);
                var bottom = (int)Math.Max(0, outputState.RelativeCrop.Bottom);
                var fallback = PreviewSceningFormatter.FormatCropAndBbmodSnippet(left, top, right, bottom);

                await ApplySmartScriptEditOrFallbackCopyAsync(
                    script => VapourSynthSmartScriptEditor.ApplyCropAndBbmod(
                        script,
                        _selectedOutputInfo.Index,
                        left,
                        top,
                        right,
                        bottom),
                    fallback);
            });
    }

    private async void InsertReplaceRangesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(InsertReplaceRangesButton_Click),
            async () =>
            {
                var ranges = GetDomainSceneRanges();
                if (ranges.Count == 0)
                {
                    return;
                }

                var targetIndex = _selectedOutputInfo?.Index ?? 0;
                var fallback = PreviewSceningFormatter.FormatReplaceRanges(ranges);
                await ApplySmartScriptEditOrFallbackCopyAsync(
                    script => VapourSynthSmartScriptEditor.ApplyReplaceRanges(script, targetIndex, ranges),
                    fallback);
            });
    }

    private void InspectorPropsTabButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveInspectorTab("props");
    }

    private void InspectorScopesTabButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveInspectorTab("scopes");
    }

    private void InspectorBasketTabButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveInspectorTab("basket");
    }

    private void OpenBasketDrawerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        UpdateFramePropsVisibility(true);
        SetActiveInspectorTab("basket");
    }

    private void ToggleBookmarkCurrentFrame()
    {
        if (_selectedOutputInfo is null)
        {
            return;
        }

        var frame = ViewModel.CurrentFrame;
        var existing = ViewModel.BookmarkedFrames.FirstOrDefault(item => item.FrameNumber == frame);
        if (existing is not null)
        {
            ViewModel.BookmarkedFrames.Remove(existing);
            ViewModel.NotifyBasketChanged();
            SetStatusText(ViewModel.Texts.VapourSynthPreviewBookmarkedRemovedStatus(frame, ViewModel.BookmarkedFrames.Count));
            return;
        }

        AddBookmarkedFrame(frame);
        SetStatusText(ViewModel.Texts.VapourSynthPreviewBookmarkedAddedStatus(frame, ViewModel.BookmarkedFrames.Count));
    }

    private void AddBookmarkedFrame(int frame)
    {
        if (_selectedOutputInfo is null)
        {
            return;
        }

        if (ViewModel.BookmarkedFrames.Any(item => item.FrameNumber == frame))
        {
            return;
        }

        var fps = _selectedOutputInfo.FpsNumerator > 0 && _selectedOutputInfo.FpsDenominator > 0
            ? _selectedOutputInfo.FpsNumerator / (double)_selectedOutputInfo.FpsDenominator
            : 24.0;
        var tc = TimeSpan.FromSeconds(frame / fps);
        var tcText = tc.TotalHours >= 1 ? tc.ToString(@"h\:mm\:ss\.fff") : tc.ToString(@"mm\:ss\.fff");
        var entry = new PreviewBasketFrameItem(frame, tcText, $"#{frame}");

        var insertIndex = 0;
        while (insertIndex < ViewModel.BookmarkedFrames.Count && ViewModel.BookmarkedFrames[insertIndex].FrameNumber < frame)
        {
            insertIndex++;
        }
        ViewModel.BookmarkedFrames.Insert(insertIndex, entry);
        ViewModel.NotifyBasketChanged();
    }

    private void BasketAddCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleBookmarkCurrentFrame();
        RequestPreviewFocus();
    }

    private void BasketSampleFramesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOutputInfo is null || _selectedOutputInfo.TotalFrames <= 0)
        {
            return;
        }

        var samples = PreviewComparisonExportCoordinator.BuildUniformSampleFrames(_selectedOutputInfo.TotalFrames, 8);
        foreach (var frame in samples)
        {
            AddBookmarkedFrame(frame);
        }

        SetStatusText(ViewModel.Texts.VapourSynthPreviewBasketModeHint(ViewModel.BookmarkedFrames.Count));
    }

    private void BasketClearButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BookmarkedFrames.Clear();
        ViewModel.BasketStatusText = string.Empty;
        ViewModel.NotifyBasketChanged();
    }

    private async void BasketFramesListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PreviewBasketFrameItem item || _selectedOutputInfo is null)
        {
            return;
        }

        await RunWindowEventHandlerAsync(
            nameof(BasketFramesListView_ItemClick),
            () => RequestFrameAsync(_selectedOutputInfo, item.FrameNumber));
    }

    private void BasketRemoveItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int frame })
        {
            var existing = ViewModel.BookmarkedFrames.FirstOrDefault(item => item.FrameNumber == frame);
            if (existing is not null)
            {
                ViewModel.BookmarkedFrames.Remove(existing);
                ViewModel.NotifyBasketChanged();
            }
        }
    }

    private IReadOnlyList<PreviewExportOutputSpec> BuildExportOutputSpecs()
    {
        if (ViewModel.IsCompareModeActive && _selectedOutputInfo is not null && _secondaryOutputInfo is not null)
        {
            return
            [
                new PreviewExportOutputSpec(_selectedOutputInfo.Index, _selectedOutputInfo.Name, 0, 0, 0),
                new PreviewExportOutputSpec(
                    _secondaryOutputInfo.Index,
                    _secondaryOutputInfo.Name,
                    ViewModel.SecondaryFrameOffset,
                    ViewModel.SubpixelOffsetX,
                    ViewModel.SubpixelOffsetY)
            ];
        }

        return ViewModel.Outputs
            .Select(opt => new PreviewExportOutputSpec(
                opt.Info.Index,
                opt.Info.Name,
                _secondaryOutputInfo is not null && opt.Info.Index == _secondaryOutputInfo.Index ? ViewModel.SecondaryFrameOffset : 0,
                _secondaryOutputInfo is not null && opt.Info.Index == _secondaryOutputInfo.Index ? ViewModel.SubpixelOffsetX : 0,
                _secondaryOutputInfo is not null && opt.Info.Index == _secondaryOutputInfo.Index ? ViewModel.SubpixelOffsetY : 0))
            .ToArray();
    }

    private (List<PreviewExportFrameRequestItem> Items, List<PreviewComparisonGroup> Groups) BuildComparisonExportPlan(
        string imagesDirectory,
        bool useRelativePathsForHtml)
    {
        var specs = BuildExportOutputSpecs();
        var items = new List<PreviewExportFrameRequestItem>();
        var groups = new List<PreviewComparisonGroup>();
        var primaryOutput = _selectedOutputInfo ?? ViewModel.Outputs.First().Info;

        foreach (var bookmark in ViewModel.BookmarkedFrames)
        {
            var groupImages = new List<PreviewComparisonImageEntry>();
            for (var s = 0; s < specs.Count; s++)
            {
                var spec = specs[s];
                var outputInfo = ViewModel.Outputs.FirstOrDefault(o => o.Info.Index == spec.OutputIndex)?.Info ?? primaryOutput;
                var targetFrame = spec.OutputIndex == primaryOutput.Index
                    ? Math.Clamp(bookmark.FrameNumber, 0, Math.Max(0, outputInfo.TotalFrames - 1))
                    : PreviewCompareCoordinator.ResolveSecondaryFrameNumber(
                        primaryOutput,
                        outputInfo,
                        bookmark.FrameNumber,
                        ViewModel.SelectedOutputSyncMode?.Value,
                        ViewModel.IsTimelineTimeMode,
                        spec.FrameOffset);

                var safeOutName = PreviewComparisonExportCoordinator.SanitizeFileNameSegment(spec.DisplayName, $"out{spec.OutputIndex}");
                var fileName = $"f{bookmark.FrameNumber:D6}_{s:D2}_{safeOutName}.png";
                var fullPath = Path.Combine(imagesDirectory, fileName);

                items.Add(new PreviewExportFrameRequestItem(
                    bookmark.FrameNumber,
                    bookmark.Label,
                    spec.OutputIndex,
                    spec.DisplayName,
                    targetFrame,
                    fullPath,
                    spec.SubpixelOffsetX,
                    spec.SubpixelOffsetY));

                groupImages.Add(new PreviewComparisonImageEntry(
                    spec.DisplayName,
                    useRelativePathsForHtml ? $"images/{fileName}" : fullPath));
            }

            groups.Add(new PreviewComparisonGroup(bookmark.Label, bookmark.FrameNumber, groupImages));
        }

        return (items, groups);
    }

    private string ResolveComparisonExportRootDirectory()
    {
        var baseDir = !string.IsNullOrWhiteSpace(_currentRequest?.WorkingDirectory) && Directory.Exists(_currentRequest.WorkingDirectory)
            ? _currentRequest.WorkingDirectory
            : _appPaths.DataRootPath;
        var root = Path.Combine(baseDir, "Comparisons");
        Directory.CreateDirectory(root);
        return root;
    }

    private async void ExportOfflineHtmlButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(ExportOfflineHtmlButton_Click),
            async () =>
            {
                if (!ViewModel.CanExportBasket || _selectedOutputInfo is null)
                {
                    return;
                }

                StopPlayback();
                ViewModel.IsExportBusy = true;
                try
                {
                    var mode = PreviewComparisonExportCoordinator.ResolveHtmlPackageMode(ViewModel.BookmarkedFrames.Count);
                    var safeTitle = PreviewComparisonExportCoordinator.SanitizeFileNameSegment(ViewModel.WindowTitleDocumentName, "comparison");
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    var exportRoot = ResolveComparisonExportRootDirectory();
                    var bundleDir = Path.Combine(exportRoot, $"{safeTitle}-{stamp}");
                    var imagesDir = Path.Combine(bundleDir, "images");
                    Directory.CreateDirectory(imagesDir);

                    var (items, groups) = BuildComparisonExportPlan(imagesDir, useRelativePathsForHtml: mode == PreviewHtmlPackageMode.FolderLinked);
                    var status = ViewModel.Texts.VapourSynthPreviewBasketExportingStatus(items.Count);
                    ViewModel.BasketStatusText = status;
                    SetStatusText(status);

                    var exportResult = await _previewService.ExportFramesAsync(items, ViewModel.CurrentRenderOptions, _previewLifetimeToken);

                    string htmlPath;
                    if (mode == PreviewHtmlPackageMode.SingleFileEmbedded)
                    {
                        var embeddedGroups = groups.Select(g => new PreviewComparisonGroup(
                            g.Name,
                            g.FrameNumber,
                            g.Images.Select(img => img with { Base64PngDataUri = PreviewOfflineHtmlBuilder.CreatePngDataUri(img.RelativeOrAbsolutePath) }).ToArray())).ToArray();

                        var html = PreviewOfflineHtmlBuilder.BuildHtml(ViewModel.WindowTitleDocumentName, embeddedGroups, PreviewHtmlPackageMode.SingleFileEmbedded);
                        htmlPath = Path.Combine(exportRoot, $"{safeTitle}-{stamp}.html");
                        await File.WriteAllTextAsync(htmlPath, html, new System.Text.UTF8Encoding(false), _previewLifetimeToken);
                        try
                        {
                            Directory.Delete(bundleDir, recursive: true);
                        }
                        catch
                        {
                        }
                    }
                    else
                    {
                        var html = PreviewOfflineHtmlBuilder.BuildHtml(ViewModel.WindowTitleDocumentName, groups, PreviewHtmlPackageMode.FolderLinked);
                        htmlPath = Path.Combine(bundleDir, "index.html");
                        await File.WriteAllTextAsync(htmlPath, html, new System.Text.UTF8Encoding(false), _previewLifetimeToken);
                    }

                    var doneMsg = ViewModel.Texts.VapourSynthPreviewHtmlExportedStatus(htmlPath, exportResult.ExportedCount, exportResult.ElapsedMs);
                    ViewModel.BasketStatusText = doneMsg;
                    SetStatusText(doneMsg);
                    try
                    {
                        Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute = true });
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    ViewModel.IsExportBusy = false;
                }
            });
    }

    private async void UploadSlowPicsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(UploadSlowPicsButton_Click),
            async () =>
            {
                if (!ViewModel.CanExportBasket || _selectedOutputInfo is null || RootLayout.XamlRoot is null)
                {
                    return;
                }

                var specs = BuildExportOutputSpecs();
                var titleBox = new TextBox
                {
                    Header = ViewModel.Texts.VapourSynthPreviewSlowPicsCollectionHeader,
                    Text = ViewModel.WindowTitleDocumentName
                };
                var publicToggle = new CheckBox
                {
                    Content = ViewModel.Texts.VapourSynthPreviewSlowPicsPublicToggle,
                    IsChecked = false
                };
                var optimizeToggle = new CheckBox
                {
                    Content = ViewModel.Texts.VapourSynthPreviewSlowPicsOptimizeToggle,
                    IsChecked = false
                };
                var summaryText = new TextBlock
                {
                    Text = ViewModel.Texts.VapourSynthPreviewSlowPicsConfirmSummary(ViewModel.BookmarkedFrames.Count, specs.Count),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85
                };

                var panel = new StackPanel { Spacing = 10 };
                panel.Children.Add(titleBox);
                panel.Children.Add(publicToggle);
                panel.Children.Add(optimizeToggle);
                panel.Children.Add(summaryText);

                var dialog = new ContentDialog
                {
                    XamlRoot = RootLayout.XamlRoot,
                    RequestedTheme = RootLayout.ActualTheme,
                    Title = ViewModel.Texts.VapourSynthPreviewSlowPicsDialogTitle,
                    Content = panel,
                    PrimaryButtonText = ViewModel.Texts.VapourSynthPreviewSlowPicsConfirmButton,
                    CloseButtonText = ViewModel.Texts.CancelButton,
                    DefaultButton = ContentDialogButton.Primary
                };

                var dialogResult = await dialog.ShowAsync();
                if (dialogResult != ContentDialogResult.Primary)
                {
                    return;
                }

                StopPlayback();
                ViewModel.IsExportBusy = true;
                try
                {
                    var safeTitle = PreviewComparisonExportCoordinator.SanitizeFileNameSegment(titleBox.Text, "slowpics");
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    var localDir = Path.Combine(ResolveComparisonExportRootDirectory(), $"slowpics-{safeTitle}-{stamp}");
                    Directory.CreateDirectory(localDir);

                    var (items, groups) = BuildComparisonExportPlan(localDir, useRelativePathsForHtml: false);
                    var exportMsg = ViewModel.Texts.VapourSynthPreviewBasketExportingStatus(items.Count);
                    ViewModel.BasketStatusText = exportMsg;
                    SetStatusText(exportMsg);

                    await _previewService.ExportFramesAsync(items, ViewModel.CurrentRenderOptions, _previewLifetimeToken);

                    ViewModel.BasketStatusText = ViewModel.Texts.VapourSynthPreviewSlowPicsUploadingStatus;
                    SetStatusText(ViewModel.Texts.VapourSynthPreviewSlowPicsUploadingStatus);

                    var uploader = new SlowPicsUploadService();
                    var uploadResult = await uploader.UploadAsync(
                        new SlowPicsUploadRequest(
                            titleBox.Text,
                            publicToggle.IsChecked == true,
                            optimizeToggle.IsChecked == true,
                            groups),
                        new Progress<string>(msg => RootLayout.DispatcherQueue.TryEnqueue(() => ViewModel.BasketStatusText = msg)),
                        _previewLifetimeToken);

                    if (uploadResult.Succeeded && !string.IsNullOrWhiteSpace(uploadResult.ComparisonUrl))
                    {
                        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                        dataPackage.SetText(uploadResult.ComparisonUrl);
                        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                        var okMsg = ViewModel.Texts.VapourSynthPreviewSlowPicsSuccessStatus(uploadResult.ComparisonUrl);
                        ViewModel.BasketStatusText = okMsg;
                        SetStatusText(okMsg);
                        try
                        {
                            if (Uri.TryCreate(uploadResult.ComparisonUrl, UriKind.Absolute, out var comparisonUri)
                                && string.Equals(comparisonUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                            {
                                Process.Start(new ProcessStartInfo(comparisonUri.AbsoluteUri) { UseShellExecute = true });
                            }
                        }
                        catch
                        {
                        }
                    }
                    else
                    {
                        var failMsg = ViewModel.Texts.VapourSynthPreviewSlowPicsFailedStatus(
                            uploadResult.ErrorMessage ?? "Unknown error",
                            localDir);
                        ViewModel.BasketStatusText = failMsg;
                        SetStatusText(failMsg);
                    }
                }
                finally
                {
                    ViewModel.IsExportBusy = false;
                }
            });
    }

    private void ScopeModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode })
        {
            ViewModel.SelectedScopeMode = mode;
            InvalidateScopeCanvasIfVisible();
        }
    }

    private void ScopeSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string slot })
        {
            var useB = string.Equals(slot, "B", StringComparison.OrdinalIgnoreCase);
            ViewModel.ScopeUseSecondary = useB;
            ScopeSourceAButton.IsChecked = !useB;
            ScopeSourceBButton.IsChecked = useB;
            InvalidateScopeCanvasIfVisible();
        }
    }

    private async void PickCompareVideoForWorstButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(PickCompareVideoForWorstButton_Click),
            async () =>
            {
                var picked = WindowInteractionHelper.PickOpenFilePath(
                    WindowNative.GetWindowHandle(this),
                    ViewModel.Texts.VapourSynthPreviewPickCompareVideoTitle,
                    _currentRequest?.SourceFilePath ?? _currentRequest?.WorkingDirectory ?? string.Empty,
                    new NativeFileDialogHelper.FileDialogFilter("Video & VapourSynth", "*.mkv;*.mp4;*.mov;*.m2ts;*.ts;*.avi;*.webm;*.flv;*.y4m;*.vpy;*.py"));
                if (string.IsNullOrWhiteSpace(picked))
                {
                    return;
                }

                await OpenQuickCompareAgainstCurrentAsync(picked);
            });
    }

    private async Task OpenQuickCompareAgainstCurrentAsync(string sourceBPath)
    {
        var fullB = Path.GetFullPath(sourceBPath);
        var labelB = Path.GetFileName(fullB);
        string sourceAPath;
        string labelA;

        if (!string.IsNullOrWhiteSpace(_currentRequest?.SourceFilePath) && File.Exists(_currentRequest.SourceFilePath))
        {
            sourceAPath = Path.GetFullPath(_currentRequest.SourceFilePath);
            labelA = Path.GetFileName(sourceAPath);
        }
        else if (!string.IsNullOrWhiteSpace(_currentRequest?.Content))
        {
            var tempDir = Path.Combine(_appPaths.DataRootPath, "vapoursynth-preview");
            Directory.CreateDirectory(tempDir);
            sourceAPath = Path.Combine(tempDir, "drop-compare-source.vpy");
            await File.WriteAllTextAsync(sourceAPath, _currentRequest.Content, new System.Text.UTF8Encoding(false));
            labelA = string.IsNullOrWhiteSpace(_currentRequest.DisplayName) ? "Current" : _currentRequest.DisplayName;
        }
        else
        {
            SetStatusText(ViewModel.Texts.VapourSynthPreviewDropCompareUnsupportedStatus);
            return;
        }

        SetStatusText(ViewModel.Texts.VapourSynthPreviewDropCompareLoadingStatus(labelA, labelB));
        var compareScript = PreviewCompareScriptBuilder.BuildTwoSourceCompareScript(sourceAPath, fullB, labelA, labelB);
        var workingDir = Path.GetDirectoryName(fullB) ?? Path.GetDirectoryName(sourceAPath) ?? AppContext.BaseDirectory;
        var displayName = ViewModel.Texts.VapourSynthPreviewQuickCompareTitle(labelA, labelB);
        var compareRequest = new VapourSynthPreviewOpenRequest(null, displayName, compareScript, workingDir);
        SmartEditScriptAsync = null;
        await OpenOrRefreshAsync(
            compareRequest,
            _lastLanguage,
            _lastThemePreference,
            initialCompareMode: PreviewCompareMode.Wipe);
    }

    private void ScopeCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateScopeCanvasIfVisible();
    }

    private void ScopeCanvas_Draw(Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl sender, Microsoft.Graphics.Canvas.UI.Xaml.CanvasDrawEventArgs args)
    {
        if (!ViewModel.IsFramePropsVisible || !ViewModel.IsScopesTabActive)
        {
            args.DrawingSession.Clear(Windows.UI.Color.FromArgb(255, 16, 18, 22));
            return;
        }

        var useSecondary = ViewModel.ScopeUseSecondary
            && ViewModel.IsCompareModeActive
            && _secondarySourceFramePixels is not null
            && _secondarySourceFrameWidth > 0
            && _secondarySourceFrameHeight > 0;
        var pixels = useSecondary ? _secondarySourceFramePixels : _sourceFramePixels;
        var frameW = useSecondary ? _secondarySourceFrameWidth : _sourceFrameWidth;
        var frameH = useSecondary ? _secondarySourceFrameHeight : _sourceFrameHeight;

        var kind = ViewModel.SelectedScopeMode switch
        {
            "waveform" => PreviewScopeKind.Waveform,
            "vectorscope" => PreviewScopeKind.Vectorscope,
            _ => PreviewScopeKind.Histogram
        };

        var canvasWidth = sender.Size.Width > 0 ? (float)sender.Size.Width : (float)sender.ActualWidth;
        var canvasHeight = sender.Size.Height > 0 ? (float)sender.Size.Height : (float)sender.ActualHeight;
        PreviewScopesRenderer.Render(
            args.DrawingSession,
            canvasWidth,
            canvasHeight,
            kind,
            pixels,
            frameW,
            frameH);
    }

    private void InvalidateScopeCanvasIfVisible()
    {
        if (!_isClosed && ViewModel.IsFramePropsVisible && ViewModel.IsScopesTabActive)
        {
            ScopeCanvas.Invalidate();
        }
    }

    private bool TryResolveSourcePixelCoordinates(ScrollViewer scrollViewer, PointerRoutedEventArgs e, out int px, out int py)
    {
        px = 0;
        py = 0;
        var isRightPane = ReferenceEquals(scrollViewer, RightCompareScrollViewer);
        var host = isRightPane
            ? RightCompareImageHost
            : (ReferenceEquals(scrollViewer, LeftCompareScrollViewer) ? LeftCompareImageHost : PreviewImageHost);
        var srcW = isRightPane ? _secondarySourceFrameWidth : _sourceFrameWidth;
        var srcH = isRightPane ? _secondarySourceFrameHeight : _sourceFrameHeight;

        if (srcW <= 0 || srcH <= 0 || host.ActualWidth <= 0 || host.ActualHeight <= 0)
        {
            return false;
        }

        var local = e.GetCurrentPoint(host).Position;
        if (local.X < 0 || local.Y < 0 || local.X > host.ActualWidth || local.Y > host.ActualHeight)
        {
            return false;
        }

        px = Math.Clamp((int)Math.Floor(local.X / host.ActualWidth * srcW), 0, srcW - 1);
        py = Math.Clamp((int)Math.Floor(local.Y / host.ActualHeight * srcH), 0, srcH - 1);
        return true;
    }

    private void UpdatePipetteFromMemory(int x, int y, bool isLocked)
    {
        if (_sourceFramePixels is null || _sourceFrameWidth <= 0 || _sourceFrameHeight <= 0)
        {
            return;
        }

        var cx = Math.Clamp(x, 0, _sourceFrameWidth - 1);
        var cy = Math.Clamp(y, 0, _sourceFrameHeight - 1);
        var idxA = (cy * _sourceFrameWidth + cx) * 4;
        if (idxA + 2 >= _sourceFramePixels.Length)
        {
            return;
        }

        var bA = _sourceFramePixels[idxA];
        var gA = _sourceFramePixels[idxA + 1];
        var rA = _sourceFramePixels[idxA + 2];
        var lockSuffix = isLocked ? $" [{ViewModel.Texts.VapourSynthPreviewPipetteLockedTag}]" : string.Empty;

        if (ViewModel.IsCompareModeActive
            && _secondarySourceFramePixels is not null
            && _secondarySourceFrameWidth > 0
            && _secondarySourceFrameHeight > 0)
        {
            var bx = Math.Clamp(cx, 0, _secondarySourceFrameWidth - 1);
            var by = Math.Clamp(cy, 0, _secondarySourceFrameHeight - 1);
            var idxB = (by * _secondarySourceFrameWidth + bx) * 4;
            if (idxB + 2 < _secondarySourceFramePixels.Length)
            {
                var bB = _secondarySourceFramePixels[idxB];
                var gB = _secondarySourceFramePixels[idxB + 1];
                var rB = _secondarySourceFramePixels[idxB + 2];
                ViewModel.PipetteReadoutText =
                    $"({cx}, {cy}){lockSuffix}  A: RGB({rA},{gA},{bA}) #{rA:X2}{gA:X2}{bA:X2}  |  B: RGB({rB},{gB},{bB})  |  Δ({rB - rA:+0;-0;0},{gB - gA:+0;-0;0},{bB - bA:+0;-0;0})";
                return;
            }
        }

        ViewModel.PipetteReadoutText = $"({cx}, {cy}){lockSuffix}  RGB({rA}, {gA}, {bA})  #{rA:X2}{gA:X2}{bA:X2}";
    }

    private async Task SampleNativePixelAsync(int x, int y, bool isLocked)
    {
        if (_selectedOutputInfo is null || _isClosed)
        {
            return;
        }

        _pipetteSampleCts?.Cancel();
        _pipetteSampleCts?.Dispose();
        _pipetteSampleCts = CancellationTokenSource.CreateLinkedTokenSource(_previewLifetimeToken);
        var token = _pipetteSampleCts.Token;

        try
        {
            var sampleA = await _previewService.SamplePixelAsync(_selectedOutputInfo.Index, ViewModel.CurrentFrame, x, y, _previewLifetimeToken);
            token.ThrowIfCancellationRequested();

            static string FormatSample(string slot, PreviewPixelSampleResult s)
            {
                var p0 = $"{s.Plane0Name}={s.Plane0Raw:0.##} ({s.Plane0Normalized:0.000})";
                var p1 = s.Plane1Name is not null && s.Plane1Raw.HasValue
                    ? $" {s.Plane1Name}={s.Plane1Raw.Value:0.##}"
                    : string.Empty;
                var p2 = s.Plane2Name is not null && s.Plane2Raw.HasValue
                    ? $" {s.Plane2Name}={s.Plane2Raw.Value:0.##}"
                    : string.Empty;
                return $"{slot} [{s.ColorFamily} {s.BitsPerSample}-bit]: {p0}{p1}{p2}";
            }

            if (ViewModel.IsCompareModeActive && _secondaryOutputInfo is not null)
            {
                var frameB = PreviewCompareCoordinator.ResolveSecondaryFrameNumber(
                    _selectedOutputInfo,
                    _secondaryOutputInfo,
                    ViewModel.CurrentFrame,
                    ViewModel.SelectedOutputSyncMode?.Value,
                    ViewModel.IsTimelineTimeMode,
                    ViewModel.SecondaryFrameOffset);
                var sampleB = await _previewService.SamplePixelAsync(_secondaryOutputInfo.Index, frameB, x, y, _previewLifetimeToken);
                token.ThrowIfCancellationRequested();

                var deltaLine = $"Δ {sampleA.Plane0Name}={sampleB.Plane0Raw - sampleA.Plane0Raw:+0.##;-0.##;0}";
                if (sampleA.Plane1Raw.HasValue && sampleB.Plane1Raw.HasValue && sampleA.Plane1Name is not null)
                {
                    deltaLine += $" {sampleA.Plane1Name}={sampleB.Plane1Raw.Value - sampleA.Plane1Raw.Value:+0.##;-0.##;0}";
                }
                if (sampleA.Plane2Raw.HasValue && sampleB.Plane2Raw.HasValue && sampleA.Plane2Name is not null)
                {
                    deltaLine += $" {sampleA.Plane2Name}={sampleB.Plane2Raw.Value - sampleA.Plane2Raw.Value:+0.##;-0.##;0}";
                }

                var lockTag = isLocked ? $" [{ViewModel.Texts.VapourSynthPreviewPipetteLockedTag}]" : string.Empty;
                ViewModel.PipetteDetailText = $"({sampleA.X}, {sampleA.Y}){lockTag}{Environment.NewLine}{FormatSample("A", sampleA)}{Environment.NewLine}{FormatSample("B", sampleB)}{Environment.NewLine}{deltaLine}";
                ViewModel.PipetteReadoutText = $"({sampleA.X}, {sampleA.Y}){lockTag}  {FormatSample("A", sampleA)}  |  {deltaLine}";
            }
            else
            {
                var lockTag = isLocked ? $" [{ViewModel.Texts.VapourSynthPreviewPipetteLockedTag}]" : string.Empty;
                ViewModel.PipetteDetailText = $"({sampleA.X}, {sampleA.Y}){lockTag}{Environment.NewLine}{FormatSample("Native", sampleA)}";
                ViewModel.PipetteReadoutText = $"({sampleA.X}, {sampleA.Y}){lockTag}  {FormatSample("Native", sampleA)}";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Keep fast memory RGB readout if native sampling is temporarily unavailable.
        }
    }

    private async Task SetSecondaryFrameOffsetAsync(int offset)
    {
        var clamped = Math.Clamp(offset, -100000, 100000);
        if (ViewModel.SecondaryFrameOffset == clamped)
        {
            return;
        }

        ViewModel.SecondaryFrameOffset = clamped;
        SyncControls();
        if (ViewModel.IsCompareModeActive && _selectedOutputInfo is not null)
        {
            await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
        }
    }

    private async void EnableHdrToneMapBannerButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(EnableHdrToneMapBannerButton_Click),
            async () =>
            {
                ViewModel.ToneMapHdr = true;
                UpdatePreviewFilterSelection();
                if (_selectedOutputInfo is not null)
                {
                    await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
                }
                RequestPreviewFocus();
            });
    }

    private void DismissHdrToneMapBannerButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsHdrBannerDismissed = true;
        RequestPreviewFocus();
    }

    private void CompareSizeAlignmentComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInternalControlUpdate
            || CompareSizeAlignmentComboBox.SelectedItem is not PreviewCompareSizeAlignmentOption option)
        {
            return;
        }

        _hasManualSizeAlignmentOverride = true;
        ViewModel.SizeAlignment = option.Alignment;
        if (ViewModel.IsDifferenceMode && _cachedBitmapA is not null && _cachedBitmapB is not null)
        {
            RedrawDifferenceSurface();
        }

        UpdatePreviewImageVisualSize();
        UpdateWipeClipAndOverlay();
    }

    private async Task SetCompareModeAsync(PreviewCompareMode targetMode)
    {
        var normalized = PreviewCompareCoordinator.NormalizeMode(targetMode, ViewModel.Outputs.Count);
        if (ViewModel.CompareMode == normalized)
        {
            UpdateCompareModeSelection();
            return;
        }

        ViewModel.CompareMode = normalized;
        if (_selectedOutputInfo is not null && normalized != PreviewCompareMode.Single)
        {
            EnsureSecondaryOutputSelection(_selectedOutputInfo);
        }

        SyncFlickerTimer();
        UpdateCompareModeSelection();
        UpdateCompareVisualPresentation();
        UpdatePreviewImageVisualSize();
        UpdateWipeClipAndOverlay();
        SyncControls();

        if (_selectedOutputInfo is null)
        {
            return;
        }

        if (normalized == PreviewCompareMode.Single)
        {
            if (_lastFrameData is not null)
            {
                RefreshDisplayedFrame();
                SyncControls();
            }

            return;
        }

        if (_secondaryOutputInfo is null)
        {
            return;
        }

        var expectedSecondaryFrame = PreviewCompareCoordinator.ResolveSecondaryFrameNumber(
            _selectedOutputInfo,
            _secondaryOutputInfo,
            ViewModel.CurrentFrame,
            ViewModel.SelectedOutputSyncMode?.Value,
            ViewModel.IsTimelineTimeMode,
            ViewModel.SecondaryFrameOffset);

        var hasMatchingCachedPair = _cachedBitmapA is not null
            && _cachedBitmapB is not null
            && _lastFrameData is not null
            && _secondaryLastFrameData is not null
            && _sourceFramePixels is not null
            && _secondarySourceFramePixels is not null
            && _lastFrameData.OutputIndex == _selectedOutputInfo.Index
            && _lastFrameData.FrameNumber == ViewModel.CurrentFrame
            && _secondaryLastFrameData.OutputIndex == _secondaryOutputInfo.Index
            && _secondaryLastFrameData.FrameNumber == expectedSecondaryFrame;

        if (hasMatchingCachedPair)
        {
            if (ViewModel.IsCropPanelVisible)
            {
                RefreshDisplayedFrame();
                SyncControls();
                return;
            }

            if (!_hasManualSizeAlignmentOverride)
            {
                ViewModel.SizeAlignment = PreviewCompareCoordinator.RecommendSizeAlignment(
                    _displayedFrameWidth,
                    _displayedFrameHeight,
                    _secondaryDisplayedFrameWidth,
                    _secondaryDisplayedFrameHeight);
            }

            ViewModel.UpdateFrame(
                _selectedOutputInfo,
                _lastFrameData!,
                _displayedFrameWidth,
                _displayedFrameHeight,
                $"{_lastFrameData!.Width} x {_lastFrameData.Height}",
                _secondaryOutputInfo,
                _secondaryLastFrameData,
                _secondaryDisplayedFrameWidth,
                _secondaryDisplayedFrameHeight);
            if (normalized == PreviewCompareMode.Difference)
            {
                RedrawDifferenceSurface();
            }

            UpdateCompareVisualPresentation();
            UpdatePreviewImageVisualSize();
            UpdateWipeClipAndOverlay();
            SyncControls();
            return;
        }

        await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
    }

    private async Task SelectSecondaryOutputAsync(VapourSynthPreviewOutputInfo secondaryOutput)
    {
        if (!ViewModel.CanCompareOutputs || _selectedOutputInfo is null)
        {
            return;
        }

        if (_secondaryOutputInfo?.Index != secondaryOutput.Index)
        {
            _hasManualSizeAlignmentOverride = false;
        }

        _secondaryOutputInfo = secondaryOutput;
        ViewModel.SelectedSecondaryOutput = ViewModel.Outputs.FirstOrDefault(option => option.Info.Index == secondaryOutput.Index);
        SyncControls();

        if (ViewModel.IsCompareModeActive)
        {
            await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
        }
    }

    private async Task SwapCompareOutputsAsync()
    {
        if (!ViewModel.IsCompareModeActive
            || _selectedOutputInfo is null
            || _secondaryOutputInfo is null
            || _selectedOutputInfo.Index == _secondaryOutputInfo.Index)
        {
            return;
        }

        var previousA = _selectedOutputInfo;
        var previousB = _secondaryOutputInfo;
        _selectedOutputInfo = previousB;
        _secondaryOutputInfo = previousA;

        ViewModel.SelectedOutput = ViewModel.Outputs.FirstOrDefault(option => option.Info.Index == _selectedOutputInfo.Index);
        ViewModel.SelectedSecondaryOutput = ViewModel.Outputs.FirstOrDefault(option => option.Info.Index == _secondaryOutputInfo.Index);
        ViewModel.UpdateForOutput(_selectedOutputInfo);

        if (_cachedBitmapA is not null
            && _cachedBitmapB is not null
            && _lastFrameData is not null
            && _secondaryLastFrameData is not null
            && _sourceFramePixels is not null
            && _secondarySourceFramePixels is not null
            && _displayedFramePixels is not null
            && _secondaryDisplayedFramePixels is not null)
        {
            (_lastFrameData, _secondaryLastFrameData) = (_secondaryLastFrameData, _lastFrameData);
            (_sourceFramePixels, _secondarySourceFramePixels) = (_secondarySourceFramePixels, _sourceFramePixels);
            (_sourceFrameWidth, _secondarySourceFrameWidth) = (_secondarySourceFrameWidth, _sourceFrameWidth);
            (_sourceFrameHeight, _secondarySourceFrameHeight) = (_secondarySourceFrameHeight, _sourceFrameHeight);

            if (ViewModel.IsCropPanelVisible)
            {
                RefreshDisplayedFrame();
                SyncControls();
                QueueFrameStatusText(ViewModel.Texts.VapourSynthPreviewCompareReadyStatus(
                    _selectedOutputInfo.Index,
                    _lastFrameData.FrameNumber,
                    _secondaryOutputInfo.Index,
                    _secondaryLastFrameData.FrameNumber));
                return;
            }

            (_displayedFramePixels, _secondaryDisplayedFramePixels) = (_secondaryDisplayedFramePixels, _displayedFramePixels);
            (_displayedFrameWidth, _secondaryDisplayedFrameWidth) = (_secondaryDisplayedFrameWidth, _displayedFrameWidth);
            (_displayedFrameHeight, _secondaryDisplayedFrameHeight) = (_secondaryDisplayedFrameHeight, _displayedFrameHeight);
            (_cachedBitmapA, _cachedBitmapB) = (_cachedBitmapB, _cachedBitmapA);

            ViewModel.UpdateFrame(
                _selectedOutputInfo,
                _lastFrameData,
                _displayedFrameWidth,
                _displayedFrameHeight,
                $"{_lastFrameData.Width} x {_lastFrameData.Height}",
                _secondaryOutputInfo,
                _secondaryLastFrameData,
                _secondaryDisplayedFrameWidth,
                _secondaryDisplayedFrameHeight);

            RedrawPrimarySurfaceFromCachedBitmap(_previewFilter);
            RedrawSecondarySurfaceFromCachedBitmap(_previewFilter);
            if (ViewModel.IsDifferenceMode)
            {
                RedrawDifferenceSurface();
            }

            UpdateCompareVisualPresentation();
            UpdatePreviewImageVisualSize();
            UpdateWipeClipAndOverlay();
            SyncControls();
            QueueFrameStatusText(ViewModel.Texts.VapourSynthPreviewCompareReadyStatus(
                _selectedOutputInfo.Index,
                _lastFrameData.FrameNumber,
                _secondaryOutputInfo.Index,
                _secondaryLastFrameData.FrameNumber));
            return;
        }

        await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
    }

    private void BuildPreviewFilterMenu()
    {
        PreviewFilterMenu.Items.Clear();
        foreach (var filter in PreviewFilterCycle)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = ViewModel.Texts.VapourSynthPreviewFilterLabel(filter),
                Tag = filter,
                GroupName = nameof(PreviewFilterMenu)
            };
            item.Click += PreviewFilterMenuItem_Click;
            PreviewFilterMenu.Items.Add(item);
        }

        PreviewFilterMenu.Items.Add(new MenuFlyoutSeparator());

        var planeSubmenu = new MenuFlyoutSubItem
        {
            Text = ViewModel.Texts.VapourSynthPreviewPlaneSubmenuHeader,
            Tag = "PlaneSubmenu"
        };
        foreach (var planeMode in Enum.GetValues<PreviewPlaneMode>())
        {
            var planeItem = new RadioMenuFlyoutItem
            {
                Text = ViewModel.Texts.VapourSynthPreviewPlaneModeLabel(planeMode),
                Tag = planeMode,
                GroupName = "PreviewPlaneModeGroup"
            };
            planeItem.Click += PreviewPlaneModeMenuItem_Click;
            planeSubmenu.Items.Add(planeItem);
        }
        PreviewFilterMenu.Items.Add(planeSubmenu);

        PreviewFilterMenu.Items.Add(new MenuFlyoutSeparator());

        var hdrToggle = new ToggleMenuFlyoutItem
        {
            Text = ViewModel.Texts.VapourSynthPreviewHdrToneMapToggle,
            Tag = "ToneMapHdr"
        };
        hdrToggle.Click += PreviewHdrToneMapToggle_Click;
        PreviewFilterMenu.Items.Add(hdrToggle);

        var nitsSubmenu = new MenuFlyoutSubItem
        {
            Text = ViewModel.Texts.VapourSynthPreviewHdrTargetNitsSubmenuHeader,
            Tag = "HdrNitsSubmenu"
        };
        foreach (var nits in new[] { 100.0, 203.0, 300.0, 500.0 })
        {
            var nitsItem = new RadioMenuFlyoutItem
            {
                Text = $"{nits:0} nits",
                Tag = nits,
                GroupName = "PreviewHdrNitsGroup"
            };
            nitsItem.Click += PreviewHdrTargetNitsMenuItem_Click;
            nitsSubmenu.Items.Add(nitsItem);
        }
        PreviewFilterMenu.Items.Add(nitsSubmenu);

        PreviewFilterMenu.Items.Add(new MenuFlyoutSeparator());

        var separateFieldsToggle = new ToggleMenuFlyoutItem
        {
            Text = ViewModel.Texts.VapourSynthPreviewSeparateFieldsToggle,
            Tag = "SeparateFields"
        };
        separateFieldsToggle.Click += PreviewSeparateFieldsToggle_Click;
        PreviewFilterMenu.Items.Add(separateFieldsToggle);

        var fftToggle = new ToggleMenuFlyoutItem
        {
            Text = ViewModel.Texts.VapourSynthPreviewFftSpectrumToggle,
            Tag = "FftSpectrum"
        };
        fftToggle.Click += PreviewFftSpectrumToggle_Click;
        PreviewFilterMenu.Items.Add(fftToggle);

        UpdatePreviewFilterSelection();
    }

    private void UpdatePreviewFilterSelection()
    {
        var activeTags = new List<string>();
        if (_previewFilter != PreviewDisplayFilter.Original)
        {
            activeTags.Add(ViewModel.Texts.VapourSynthPreviewFilterLabel(_previewFilter));
        }
        if (ViewModel.PlaneMode != PreviewPlaneMode.Rgb)
        {
            activeTags.Add(ViewModel.PlaneMode.ToString());
        }
        if (ViewModel.ToneMapHdr)
        {
            activeTags.Add("HDR");
        }
        if (ViewModel.SeparateFields)
        {
            activeTags.Add("Fields");
        }
        if (ViewModel.FftSpectrum)
        {
            activeTags.Add("FFT");
        }

        var summary = activeTags.Count == 0
            ? ViewModel.Texts.VapourSynthPreviewFilterHeader
            : string.Join(" · ", activeTags);
        PreviewFilterButton.Content = summary;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(PreviewFilterButton, summary);

        foreach (var menuItem in PreviewFilterMenu.Items)
        {
            switch (menuItem)
            {
                case RadioMenuFlyoutItem radio when radio.Tag is PreviewDisplayFilter filterTag:
                    radio.IsChecked = filterTag == _previewFilter;
                    break;
                case ToggleMenuFlyoutItem toggle when Equals(toggle.Tag, "ToneMapHdr"):
                    toggle.IsChecked = ViewModel.ToneMapHdr;
                    break;
                case ToggleMenuFlyoutItem toggle when Equals(toggle.Tag, "SeparateFields"):
                    toggle.IsChecked = ViewModel.SeparateFields;
                    break;
                case ToggleMenuFlyoutItem toggle when Equals(toggle.Tag, "FftSpectrum"):
                    toggle.IsChecked = ViewModel.FftSpectrum;
                    break;
                case MenuFlyoutSubItem sub when Equals(sub.Tag, "PlaneSubmenu"):
                    foreach (var child in sub.Items.OfType<RadioMenuFlyoutItem>())
                    {
                        child.IsChecked = Equals(child.Tag, ViewModel.PlaneMode);
                    }
                    break;
                case MenuFlyoutSubItem sub when Equals(sub.Tag, "HdrNitsSubmenu"):
                    foreach (var child in sub.Items.OfType<RadioMenuFlyoutItem>())
                    {
                        child.IsChecked = child.Tag is double nits && Math.Abs(nits - ViewModel.ToneMapTargetNits) < 0.5;
                    }
                    break;
            }
        }
    }

    private async void PreviewPlaneModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(PreviewPlaneModeMenuItem_Click),
            async () =>
            {
                if (((RadioMenuFlyoutItem)sender).Tag is PreviewPlaneMode mode && ViewModel.PlaneMode != mode)
                {
                    ViewModel.PlaneMode = mode;
                    UpdatePreviewFilterSelection();
                    if (_selectedOutputInfo is not null)
                    {
                        await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
                    }
                }
                RequestPreviewFocus();
            });
    }

    private async void PreviewHdrToneMapToggle_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(PreviewHdrToneMapToggle_Click),
            async () =>
            {
                ViewModel.ToneMapHdr = ((ToggleMenuFlyoutItem)sender).IsChecked;
                UpdatePreviewFilterSelection();
                if (_selectedOutputInfo is not null)
                {
                    await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
                }
                RequestPreviewFocus();
            });
    }

    private async void PreviewHdrTargetNitsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(PreviewHdrTargetNitsMenuItem_Click),
            async () =>
            {
                if (((RadioMenuFlyoutItem)sender).Tag is double nits)
                {
                    ViewModel.ToneMapTargetNits = nits;
                    ViewModel.ToneMapHdr = true;
                    UpdatePreviewFilterSelection();
                    if (_selectedOutputInfo is not null)
                    {
                        await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
                    }
                }
                RequestPreviewFocus();
            });
    }

    private async void PreviewSeparateFieldsToggle_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(PreviewSeparateFieldsToggle_Click),
            async () =>
            {
                ViewModel.SeparateFields = ((ToggleMenuFlyoutItem)sender).IsChecked;
                UpdatePreviewFilterSelection();
                if (_selectedOutputInfo is not null)
                {
                    await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
                }
                RequestPreviewFocus();
            });
    }

    private async void PreviewFftSpectrumToggle_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(PreviewFftSpectrumToggle_Click),
            async () =>
            {
                ViewModel.FftSpectrum = ((ToggleMenuFlyoutItem)sender).IsChecked;
                UpdatePreviewFilterSelection();
                if (_selectedOutputInfo is not null)
                {
                    await RequestFrameAsync(_selectedOutputInfo, ViewModel.CurrentFrame);
                }
                RequestPreviewFocus();
            });
    }

    private async void PreviewFilterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(PreviewFilterMenuItem_Click),
            () =>
            {
                SetPreviewFilter((PreviewDisplayFilter)((RadioMenuFlyoutItem)sender).Tag);
                RequestPreviewFocus();
                return Task.CompletedTask;
            });
    }

    private void SetPreviewFilter(PreviewDisplayFilter filter)
    {
        if (_previewFilter == filter)
        {
            return;
        }

        try
        {
            _previewFilter = filter;
            if (_cachedBitmapA is not null)
            {
                RedrawPrimarySurfaceFromCachedBitmap(filter);
            }
            else if (_displayedFramePixels is not null)
            {
                UpdatePreviewDrawingSurface(_displayedFramePixels, _displayedFrameWidth, _displayedFrameHeight, filter);
            }

            if (ViewModel.IsCompareModeActive)
            {
                if (_cachedBitmapB is not null)
                {
                    RedrawSecondarySurfaceFromCachedBitmap(filter);
                }
                else if (_secondaryDisplayedFramePixels is not null)
                {
                    UpdateSecondaryPreviewDrawingSurface(
                        _secondaryDisplayedFramePixels,
                        _secondaryDisplayedFrameWidth,
                        _secondaryDisplayedFrameHeight,
                        filter);
                }

                if (ViewModel.IsDifferenceMode && _cachedBitmapA is not null && _cachedBitmapB is not null)
                {
                    RedrawDifferenceSurface();
                }
            }
        }
        finally
        {
            UpdatePreviewFilterSelection();
        }
    }

    private void EnsurePreviewDrawingSurface(int width, int height)
    {
        if (_previewGraphicsDevice is null || _previewImageBrush is null)
        {
            return;
        }

        if (_previewDrawingSurface is not null
            && _previewDrawingSurfaceWidth == width
            && _previewDrawingSurfaceHeight == height)
        {
            return;
        }

        ClearPrimaryPreviewDrawingSurface();
        _previewDrawingSurface = _previewGraphicsDevice.CreateDrawingSurface(
            new Size(Math.Max(1, width), Math.Max(1, height)),
            Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            Microsoft.Graphics.DirectX.DirectXAlphaMode.Ignore);
        _previewDrawingSurfaceWidth = width;
        _previewDrawingSurfaceHeight = height;
        _previewImageBrush.Surface = _previewDrawingSurface;
    }

    private void EnsureSecondaryPreviewDrawingSurface(int width, int height)
    {
        if (_previewGraphicsDevice is null || _previewImageBrushB is null)
        {
            return;
        }

        if (_previewDrawingSurfaceB is not null
            && _previewDrawingSurfaceWidthB == width
            && _previewDrawingSurfaceHeightB == height)
        {
            return;
        }

        ClearSecondaryPreviewDrawingSurface();
        _previewDrawingSurfaceB = _previewGraphicsDevice.CreateDrawingSurface(
            new Size(Math.Max(1, width), Math.Max(1, height)),
            Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            Microsoft.Graphics.DirectX.DirectXAlphaMode.Ignore);
        _previewDrawingSurfaceWidthB = width;
        _previewDrawingSurfaceHeightB = height;
        _previewImageBrushB.Surface = _previewDrawingSurfaceB;
    }

    private void EnsureDifferencePreviewDrawingSurface(int width, int height)
    {
        if (_previewGraphicsDevice is null || _previewImageBrushDiff is null)
        {
            return;
        }

        if (_previewDrawingSurfaceDiff is not null
            && _previewDrawingSurfaceWidthDiff == width
            && _previewDrawingSurfaceHeightDiff == height)
        {
            return;
        }

        ClearDifferencePreviewDrawingSurface();
        _previewDrawingSurfaceDiff = _previewGraphicsDevice.CreateDrawingSurface(
            new Size(Math.Max(1, width), Math.Max(1, height)),
            Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            Microsoft.Graphics.DirectX.DirectXAlphaMode.Ignore);
        _previewDrawingSurfaceWidthDiff = width;
        _previewDrawingSurfaceHeightDiff = height;
        _previewImageBrushDiff.Surface = _previewDrawingSurfaceDiff;
    }

    private void ClearPreviewDrawingSurface()
    {
        ClearPrimaryPreviewDrawingSurface();
        ClearSecondaryPreviewDrawingSurface();
        ClearDifferencePreviewDrawingSurface();
    }

    private void ClearPrimaryPreviewDrawingSurface()
    {
        var previousSurface = _previewDrawingSurface;
        if (_previewImageBrush is not null)
        {
            _previewImageBrush.Surface = null;
        }

        _previewDrawingSurface = null;
        _previewDrawingSurfaceWidth = 0;
        _previewDrawingSurfaceHeight = 0;
        previousSurface?.Dispose();
    }

    private void ClearSecondaryPreviewDrawingSurface()
    {
        var previousSurface = _previewDrawingSurfaceB;
        if (_previewImageBrushB is not null)
        {
            _previewImageBrushB.Surface = null;
        }

        _previewDrawingSurfaceB = null;
        _previewDrawingSurfaceWidthB = 0;
        _previewDrawingSurfaceHeightB = 0;
        previousSurface?.Dispose();
    }

    private void ClearDifferencePreviewDrawingSurface()
    {
        var previousSurface = _previewDrawingSurfaceDiff;
        if (_previewImageBrushDiff is not null)
        {
            _previewImageBrushDiff.Surface = null;
        }

        _previewDrawingSurfaceDiff = null;
        _previewDrawingSurfaceWidthDiff = 0;
        _previewDrawingSurfaceHeightDiff = 0;
        previousSurface?.Dispose();
    }

    private void UpdatePreviewImageVisualSize()
    {
        var canvasDisplayWidth = Math.Max(0, ViewModel.PreviewImageWidth);
        var canvasDisplayHeight = Math.Max(0, ViewModel.PreviewImageHeight);
        var primarySize = new Vector2((float)canvasDisplayWidth, (float)canvasDisplayHeight);
        var secondarySize = new Vector2(
            (float)Math.Max(0, ViewModel.SecondaryPreviewImageWidth),
            (float)Math.Max(0, ViewModel.SecondaryPreviewImageHeight));

        if (_singlePreviewContainerVisual is not null)
        {
            _singlePreviewContainerVisual.Size = primarySize;
        }

        if (_wipeLayerAContainerVisual is not null)
        {
            _wipeLayerAContainerVisual.Size = primarySize;
            _wipeLayerAContainerVisual.Offset = Vector3.Zero;
        }

        if (_wipeLayerBContainerVisual is not null)
        {
            _wipeLayerBContainerVisual.Size = primarySize;
            _wipeLayerBContainerVisual.Offset = Vector3.Zero;
        }

        if ((ViewModel.IsWipeMode || ViewModel.IsFlickerMode || ViewModel.IsTemporaryFlipToB)
            && _displayedFrameWidth > 0
            && _displayedFrameHeight > 0
            && _secondaryDisplayedFrameWidth > 0
            && _secondaryDisplayedFrameHeight > 0
            && canvasDisplayWidth > 0
            && canvasDisplayHeight > 0)
        {
            var layout = PreviewCompareCoordinator.ComputeAlignedCanvasLayout(
                _displayedFrameWidth,
                _displayedFrameHeight,
                _secondaryDisplayedFrameWidth,
                _secondaryDisplayedFrameHeight,
                ViewModel.SizeAlignment);
            var scaleX = canvasDisplayWidth / Math.Max(1, layout.CanvasWidth);
            var scaleY = canvasDisplayHeight / Math.Max(1, layout.CanvasHeight);

            if (_previewImageVisual is not null)
            {
                _previewImageVisual.Size = new Vector2(
                    (float)(layout.RectA.Width * scaleX),
                    (float)(layout.RectA.Height * scaleY));
                _previewImageVisual.Offset = new Vector3(
                    (float)(layout.RectA.X * scaleX),
                    (float)(layout.RectA.Y * scaleY),
                    0f);
            }

            if (_previewImageVisualB is not null)
            {
                _previewImageVisualB.Size = new Vector2(
                    (float)(layout.RectB.Width * scaleX),
                    (float)(layout.RectB.Height * scaleY));
                _previewImageVisualB.Offset = new Vector3(
                    (float)(layout.RectB.X * scaleX),
                    (float)(layout.RectB.Y * scaleY),
                    0f);
            }
        }
        else
        {
            if (_previewImageVisual is not null)
            {
                _previewImageVisual.Size = primarySize;
                _previewImageVisual.Offset = Vector3.Zero;
            }

            if (_previewImageVisualB is not null)
            {
                _previewImageVisualB.Size = primarySize;
                _previewImageVisualB.Offset = Vector3.Zero;
            }
        }

        if (_previewImageVisualDiff is not null)
        {
            _previewImageVisualDiff.Size = primarySize;
            _previewImageVisualDiff.Offset = Vector3.Zero;
        }

        if (_sideBySideVisualA is not null)
        {
            _sideBySideVisualA.Size = primarySize;
            _sideBySideVisualA.Offset = Vector3.Zero;
        }

        if (_sideBySideVisualB is not null)
        {
            _sideBySideVisualB.Size = secondarySize;
            _sideBySideVisualB.Offset = Vector3.Zero;
        }
    }

    private bool IsTextInputFocusedForTab()
    {
        var focusedElement = FocusManager.GetFocusedElement(RootLayout.XamlRoot);
        return focusedElement is TextBox
            or PasswordBox
            or RichEditBox
            or NumberBox
            or ComboBox { IsDropDownOpen: true };
    }

    private void RootLayout_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab
            || IsControlKeyPressed()
            || IsKeyPressed(VirtualKey.Menu)
            || IsKeyPressed(VirtualKey.Shift))
        {
            return;
        }

        if (!ViewModel.IsCompareModeActive || IsTextInputFocusedForTab())
        {
            return;
        }

        e.Handled = true;
        if (!ViewModel.IsTemporaryFlipToB)
        {
            StartTabHoldFlip();
        }
    }

    private void RootLayout_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab && ViewModel.IsTemporaryFlipToB)
        {
            e.Handled = true;
            StopTabHoldFlip();
        }
    }

    private async void RootLayout_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(RootLayout_KeyDown),
            async () =>
            {
                if (e.Handled)
                {
                    return;
                }

                if (e.Key == VirtualKey.F11)
                {
                    e.Handled = _isFullScreenActive
                        ? TryRestoreWindowedPresentation()
                        : TryEnterFullScreen();
                    return;
                }

                if (e.Key == VirtualKey.Escape)
                {
                    e.Handled = true;
                    Close();
                    return;
                }

                if (IsEditingShortcutSuppressed())
                {
                    return;
                }

                if (e.Key == VirtualKey.F && !IsControlKeyPressed() && !IsKeyPressed(VirtualKey.Menu))
                {
                    e.Handled = true;
                    if (!e.KeyStatus.WasKeyDown)
                    {
                        var direction = IsKeyPressed(VirtualKey.Shift) ? -1 : 1;
                        var index = Array.IndexOf(PreviewFilterCycle, _previewFilter);
                        var nextIndex = (index + direction + PreviewFilterCycle.Length) % PreviewFilterCycle.Length;
                        SetPreviewFilter(PreviewFilterCycle[nextIndex]);
                    }

                    return;
                }

                if (e.Key == VirtualKey.C && !IsControlKeyPressed() && !IsKeyPressed(VirtualKey.Menu))
                {
                    if (ViewModel.CanCompareOutputs)
                    {
                        e.Handled = true;
                        if (!e.KeyStatus.WasKeyDown)
                        {
                            var nextMode = PreviewCompareCoordinator.CycleMode(
                                ViewModel.CompareMode,
                                ViewModel.Outputs.Count,
                                reverse: IsKeyPressed(VirtualKey.Shift));
                            await SetCompareModeAsync(nextMode);
                        }
                    }

                    return;
                }

                if (e.Key == VirtualKey.Tab && !IsControlKeyPressed() && !IsKeyPressed(VirtualKey.Menu) && !IsKeyPressed(VirtualKey.Shift))
                {
                    if (ViewModel.IsCompareModeActive)
                    {
                        e.Handled = true;
                        StartTabHoldFlip();
                        return;
                    }
                }

                if (IsKeyPressed(VirtualKey.Menu) && !IsControlKeyPressed() && ViewModel.IsCompareModeActive)
                {
                    if ((int)e.Key == 188)
                    {
                        e.Handled = true;
                        await SetSecondaryFrameOffsetAsync(ViewModel.SecondaryFrameOffset - 1);
                        return;
                    }
                    if ((int)e.Key == 190)
                    {
                        e.Handled = true;
                        await SetSecondaryFrameOffsetAsync(ViewModel.SecondaryFrameOffset + 1);
                        return;
                    }
                }

                if (e.Key == VirtualKey.I && !IsControlKeyPressed() && !IsKeyPressed(VirtualKey.Menu) && !IsKeyPressed(VirtualKey.Shift))
                {
                    e.Handled = true;
                    if (!e.KeyStatus.WasKeyDown)
                    {
                        MarkSceneInAtCurrentFrame();
                    }
                    return;
                }

                if (e.Key == VirtualKey.O && !IsControlKeyPressed() && !IsKeyPressed(VirtualKey.Menu) && !IsKeyPressed(VirtualKey.Shift))
                {
                    e.Handled = true;
                    if (!e.KeyStatus.WasKeyDown)
                    {
                        MarkSceneOutAtCurrentFrame();
                    }
                    return;
                }

                if (e.Key == VirtualKey.B && !IsControlKeyPressed() && !IsKeyPressed(VirtualKey.Menu) && !IsKeyPressed(VirtualKey.Shift))
                {
                    e.Handled = true;
                    if (!e.KeyStatus.WasKeyDown)
                    {
                        ToggleBookmarkCurrentFrame();
                    }
                    return;
                }

                if (e.Key == VirtualKey.X && !IsControlKeyPressed() && !IsKeyPressed(VirtualKey.Menu))
                {
                    if (ViewModel.IsCompareModeActive)
                    {
                        e.Handled = true;
                        if (!e.KeyStatus.WasKeyDown)
                        {
                            await SwapCompareOutputsAsync();
                        }
                    }

                    return;
                }

                if (TryGetOutputIndexFromKey(e.Key, out var outputIndex))
                {
                    var target = ViewModel.Outputs.FirstOrDefault(option => option.Info.Index == outputIndex);
                    if (target is not null)
                    {
                        e.Handled = true;
                        if (ViewModel.IsCompareModeActive && IsKeyPressed(VirtualKey.Shift))
                        {
                            await SelectSecondaryOutputAsync(target.Info);
                        }
                        else
                        {
                            await SelectOutputAsync(target.Info);
                        }
                    }

                    return;
                }

                if (TryGetFrameNavigationDelta(e.Key, out var frameDelta))
                {
                    e.Handled = true;
                    await RequestRelativeFrameAsync(frameDelta);
                    return;
                }

                if (e.Key == VirtualKey.S)
                {
                    e.Handled = true;
                    if (IsControlKeyPressed())
                    {
                        await SaveCurrentSnapshotAsync();
                    }
                    else
                    {
                        await QuickSaveSnapshotAsync();
                    }
                }
            });
    }

    private async void PreviewImageHost_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        await RunWindowEventHandlerAsync(
            nameof(PreviewImageHost_KeyDown),
            async () =>
            {
                if (e.Handled || !TryGetFrameNavigationDelta(e.Key, out var frameDelta))
                {
                    return;
                }

                e.Handled = true;
                await RequestRelativeFrameAsync(frameDelta);
            });
    }

    private void RootLayout_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab && ViewModel.IsTemporaryFlipToB)
        {
            e.Handled = true;
            StopTabHoldFlip();
            return;
        }

        if (e.Handled || e.Key != VirtualKey.Enter)
        {
            return;
        }

        if (TryFindAncestor<NumberBox>(e.OriginalSource as DependencyObject) is null)
        {
            return;
        }

        e.Handled = true;
        RequestPreviewFocus();
    }

    private void NumberBoxEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != VirtualKey.Enter)
        {
            return;
        }

        RootLayout.DispatcherQueue.TryEnqueue(() =>
        {
            RequestPreviewFocus();
        });
    }

    private bool IsEditingShortcutSuppressed()
    {
        var focusedElement = FocusManager.GetFocusedElement(RootLayout.XamlRoot);
        return focusedElement is TextBox
            or PasswordBox
            or RichEditBox
            or ComboBox
            or NumberBox;
    }

    private bool FocusPreviewSurface()
    {
        if (_isClosed)
        {
            return false;
        }

        var host = ViewModel.IsSideBySideMode ? LeftCompareImageHost : PreviewImageHost;
        if (host.Focus(FocusState.Programmatic))
        {
            return true;
        }

        return ReloadPreviewButton.Focus(FocusState.Programmatic);
    }

    private void RequestPreviewFocus()
    {
        _ = FocusPreviewSurface();
        QueueFocusPreviewSurface();
    }

    private void QueueFocusPreviewSurface()
    {
        if (_isClosed)
        {
            return;
        }

        RootLayout.DispatcherQueue.TryEnqueue(() => _ = FocusPreviewSurface());
    }

    private Task RequestRelativeFrameAsync(int frameDelta)
    {
        if (_selectedOutputInfo is null)
        {
            return Task.CompletedTask;
        }

        var targetFrame = Math.Clamp(
            (_pendingNavigationFrame ?? ViewModel.CurrentFrame) + frameDelta,
            0,
            Math.Max(0, _selectedOutputInfo.TotalFrames - 1));
        _pendingNavigationFrame = targetFrame;
        return RequestFrameAsync(_selectedOutputInfo, targetFrame, preservePendingNavigationFrame: true);
    }

    private void SetPreviewScrollOffset(double? horizontalOffset, double? verticalOffset)
    {
        SetPreviewScrollOffset(
            ViewModel.IsSideBySideMode ? LeftCompareScrollViewer : PreviewScrollViewer,
            horizontalOffset,
            verticalOffset);
    }

    private void SetPreviewScrollOffset(
        ScrollViewer scrollViewer,
        double? horizontalOffset,
        double? verticalOffset)
    {
        scrollViewer.ChangeView(horizontalOffset, verticalOffset, null, true);
        if (ViewModel.IsSideBySideMode)
        {
            var other = ReferenceEquals(scrollViewer, LeftCompareScrollViewer)
                ? RightCompareScrollViewer
                : LeftCompareScrollViewer;
            SyncCompareScrollViewer(scrollViewer, other);
        }
        else if (ReferenceEquals(scrollViewer, PreviewScrollViewer))
        {
            UpdateWipeClipAndOverlay();
        }
    }

    private static TControl? TryFindAncestor<TControl>(DependencyObject? source)
        where TControl : DependencyObject
    {
        while (source is not null)
        {
            if (source is TControl control)
            {
                return control;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void AttachFrameSliderInteractionHandlers()
    {
        DetachFrameSliderInteractionHandlers();

        FrameSlider.AddHandler(UIElement.PointerPressedEvent, _frameSliderPointerPressedHandler, true);
        FrameSlider.AddHandler(UIElement.PointerReleasedEvent, _frameSliderPointerReleasedHandler, true);
        FrameSlider.AddHandler(UIElement.PointerCanceledEvent, _frameSliderPointerCanceledHandler, true);
        FrameSlider.AddHandler(UIElement.PointerCaptureLostEvent, _frameSliderPointerCaptureLostHandler, true);
    }

    private void DetachFrameSliderInteractionHandlers()
    {
        FrameSlider.RemoveHandler(UIElement.PointerPressedEvent, _frameSliderPointerPressedHandler);
        FrameSlider.RemoveHandler(UIElement.PointerReleasedEvent, _frameSliderPointerReleasedHandler);
        FrameSlider.RemoveHandler(UIElement.PointerCanceledEvent, _frameSliderPointerCanceledHandler);
        FrameSlider.RemoveHandler(UIElement.PointerCaptureLostEvent, _frameSliderPointerCaptureLostHandler);

        _isFrameSliderInteracting = false;
    }

    private void FrameSliderInteraction_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_selectedOutputInfo is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(FrameSlider);
        if (point.Properties.IsLeftButtonPressed)
        {
            _isFrameSliderInteracting = true;
        }
    }

    private void FrameSliderInteraction_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndFrameSliderInteraction(commitFrame: true);
    }

    private void FrameSliderInteraction_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndFrameSliderInteraction(commitFrame: false);
    }

    private void FrameSliderInteraction_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndFrameSliderInteraction(commitFrame: true);
    }

    private void EndFrameSliderInteraction(bool commitFrame)
    {
        if (!_isFrameSliderInteracting)
        {
            return;
        }

        _isFrameSliderInteracting = false;

        if (commitFrame && _selectedOutputInfo is not null)
        {
            var frameNumber = Math.Clamp(
                (int)Math.Round(FrameSlider.Value),
                0,
                Math.Max(0, _selectedOutputInfo.TotalFrames - 1));
            UpdateFrameNumberDisplay(frameNumber);
            UpdateActiveChapter(frameNumber);
            _ = RequestFrameAsync(frameNumber);
        }
    }

    private void UpdateFrameNumberDisplay(int frameNumber)
    {
        if (_isClosed)
        {
            return;
        }

        var wasInternalControlUpdate = _isInternalControlUpdate;
        _isInternalControlUpdate = true;

        try
        {
            FrameNumberBox.Value = Math.Clamp(frameNumber, 0, Math.Max(0, ViewModel.TotalFrames - 1));
        }
        finally
        {
            _isInternalControlUpdate = wasInternalControlUpdate;
        }
    }

    private void AttachPreviewNumberBoxEditorHandlers()
    {
        DetachPreviewNumberBoxEditorHandlers();

        foreach (var numberBox in EnumeratePreviewNumberBoxes())
        {
            numberBox.ApplyTemplate();

            var editor = FindDescendant<TextBox>(numberBox);
            if (editor is null)
            {
                continue;
            }

            editor.AddHandler(UIElement.KeyDownEvent, _numberBoxEditorKeyDownHandler, true);
            _attachedPreviewNumberBoxEditors.Add(editor);
        }
    }

    private void DetachPreviewNumberBoxEditorHandlers()
    {
        foreach (var editor in _attachedPreviewNumberBoxEditors)
        {
            editor.RemoveHandler(UIElement.KeyDownEvent, _numberBoxEditorKeyDownHandler);
        }

        _attachedPreviewNumberBoxEditors.Clear();
    }

    private void QueueAttachPreviewNumberBoxEditorHandlers()
    {
        if (_isClosed)
        {
            return;
        }

        RootLayout.DispatcherQueue.TryEnqueue(AttachPreviewNumberBoxEditorHandlers);
    }

    private IEnumerable<NumberBox> EnumeratePreviewNumberBoxes()
    {
        yield return FrameNumberBox;
        yield return ZoomRatioBox;
        yield return StepSizeBox;
        yield return TimeStepSecondsBox;
        yield return CropLeftBox;
        yield return CropTopBox;
        yield return CropWidthBox;
        yield return CropHeightBox;
        yield return CropRightBox;
        yield return CropBottomBox;
        yield return CropZoomBox;
    }

    private static TElement? FindDescendant<TElement>(DependencyObject root)
        where TElement : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TElement element)
            {
                return element;
            }

            var descendant = FindDescendant<TElement>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static bool TryGetFrameNavigationDelta(VirtualKey key, out int frameDelta)
    {
        frameDelta = key switch
        {
            VirtualKey.Left or VirtualKey.Up => -1,
            VirtualKey.Right or VirtualKey.Down => 1,
            _ => 0
        };

        return frameDelta != 0;
    }

    private static bool TryGetOutputIndexFromKey(VirtualKey key, out int outputIndex)
    {
        outputIndex = key switch
        {
            VirtualKey.Number0 or VirtualKey.NumberPad0 => 0,
            VirtualKey.Number1 or VirtualKey.NumberPad1 => 1,
            VirtualKey.Number2 or VirtualKey.NumberPad2 => 2,
            VirtualKey.Number3 or VirtualKey.NumberPad3 => 3,
            VirtualKey.Number4 or VirtualKey.NumberPad4 => 4,
            VirtualKey.Number5 or VirtualKey.NumberPad5 => 5,
            VirtualKey.Number6 or VirtualKey.NumberPad6 => 6,
            VirtualKey.Number7 or VirtualKey.NumberPad7 => 7,
            VirtualKey.Number8 or VirtualKey.NumberPad8 => 8,
            VirtualKey.Number9 or VirtualKey.NumberPad9 => 9,
            _ => -1
        };

        return outputIndex >= 0;
    }

    private async Task ScrollToCropBoundaryAsync(CropField? field)
    {
        if (!ViewModel.IsCropPanelVisible)
        {
            return;
        }

        await Task.Yield();
        PreviewScrollViewer.UpdateLayout();

        double? horizontalOffset = null;
        double? verticalOffset = null;

        switch (field)
        {
            case null:
            case CropField.Left:
                horizontalOffset = 0;
                break;
            case CropField.Right:
            case CropField.Width:
                horizontalOffset = PreviewScrollViewer.ScrollableWidth;
                break;
        }

        switch (field)
        {
            case null:
            case CropField.Top:
                verticalOffset = 0;
                break;
            case CropField.Bottom:
            case CropField.Height:
                verticalOffset = PreviewScrollViewer.ScrollableHeight;
                break;
        }

        SetPreviewScrollOffset(horizontalOffset, verticalOffset);
    }

    private sealed record DisplayFramePayload(
        byte[] Pixels,
        int Width,
        int Height,
        string ResolutionText,
        TimeSpan ComposeElapsed);

    private sealed record PreviewFrameRequest(
        long SessionRevision,
        VapourSynthPreviewOutputInfo OutputInfo,
        int FrameNumber,
        PreviewCompareMode CompareMode,
        VapourSynthPreviewOutputInfo? SecondaryOutputInfo,
        int? SecondaryFrameNumber,
        PreviewRenderOptions RenderOptions,
        PreviewRenderOptions SecondaryRenderOptions);

    private sealed record ChapterEntry(
        TimeSpan Timecode,
        string Title);

    private sealed class PreviewOutputState
    {
        public bool HasVisited { get; set; }

        public int CurrentFrame { get; set; }

        public string ZoomMode { get; set; } = "actual";

        public double ZoomRatio { get; set; } = 1.0;

        public string CropMode { get; set; } = "relative";

        public AbsoluteCropState AbsoluteCrop { get; init; } = new();

        public RelativeCropState RelativeCrop { get; init; } = new();

        public PreviewRepairSuggestion? AutoCropRepair { get; set; }

        public static PreviewOutputState CreateDefault(int width, int height)
        {
            return new PreviewOutputState
            {
                AbsoluteCrop = AbsoluteCropState.CreateDefault(width, height),
                RelativeCrop = RelativeCropState.CreateDefault()
            };
        }
    }

    private sealed record PreviewZoomState(
        string ZoomMode,
        double ZoomRatio);

    private sealed class AbsoluteCropState
    {
        public int Left { get; set; }

        public int Top { get; set; }

        public int Width { get; set; } = 1;

        public int Height { get; set; } = 1;

        public double ZoomPercentage { get; set; } = 100;

        public static AbsoluteCropState CreateDefault(int width, int height)
        {
            return new AbsoluteCropState
            {
                Width = Math.Max(1, width),
                Height = Math.Max(1, height),
                ZoomPercentage = 200
            };
        }
    }

    private sealed class RelativeCropState
    {
        public int Left { get; set; }

        public int Top { get; set; }

        public int Right { get; set; }

        public int Bottom { get; set; }

        public double ZoomPercentage { get; set; } = 100;

        public static RelativeCropState CreateDefault()
        {
            return new RelativeCropState
            {
                ZoomPercentage = 200
            };
        }
    }

    private enum CropField
    {
        Left,
        Top,
        Width,
        Height,
        Right,
        Bottom
    }

}
