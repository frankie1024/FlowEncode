using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FlowEncode.Application;
using FlowEncode.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class VapourSynthPreviewWindowViewModel : ObservableObject
{
    private readonly IAppSettingsService _settingsService;
    private AppText _texts;
    private string _windowTitle;
    private string _statusText;
    private string _currentTimeText;
    private string _totalTimeText;
    private string _resolutionText;
    private string _formatText;
    private string _bitDepthText;
    private string _fpsText;
    private string _frameTypeText;
    private string _framePropsText;
    private double _previewImageWidth;
    private double _previewImageHeight;
    private double _secondaryPreviewImageWidth;
    private double _secondaryPreviewImageHeight;
    private int _currentFrame;
    private int _totalFrames;
    private int _stepSize;
    private double _frameSliderMaximum;
    private bool _isFramePropsVisible;
    private bool _isCropPanelVisible;
    private bool _silentSnapshotEnabled;
    private double _zoomRatio;
    private double _cropZoomPercentage;
    private double _displayScale = 1.0;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _timeStepSeconds;
    private int _sourceFrameWidth;
    private int _sourceFrameHeight;
    private int _secondaryFrameWidth;
    private int _secondaryFrameHeight;
    private string _snapshotTemplate;
    private string _scalingAlgorithm;
    private PreviewCompareMode _compareMode = PreviewCompareMode.Single;
    private PreviewDifferenceGain _differenceGain = PreviewDifferenceGain.Boost;
    private PreviewCompareSizeAlignment _sizeAlignment = PreviewCompareSizeAlignment.Center1To1;
    private double _splitRatio = 0.5;
    private string _compareInfoText = string.Empty;
    private string _compareOutputABadgeText = string.Empty;
    private string _compareOutputBBadgeText = string.Empty;
    private bool _hasCompareDimensionMismatch;
    private VapourSynthPreviewOutputOption? _selectedOutput;
    private VapourSynthPreviewOutputOption? _selectedSecondaryOutput;
    private PreviewCompareModeOption? _selectedCompareModeOption;
    private PreviewDifferenceGainOption? _selectedDifferenceGainOption;
    private PreviewCompareSizeAlignmentOption? _selectedSizeAlignmentOption;
    private StringChoiceOption? _selectedZoomMode;
    private StringChoiceOption? _selectedTimelineMode;
    private StringChoiceOption? _selectedCropMode;
    private StringChoiceOption? _selectedOutputSyncMode;
    private StringChoiceOption? _selectedScalingAlgorithmMode;

    public VapourSynthPreviewWindowViewModel(IAppSettingsService settingsService)
    {
        _settingsService = settingsService;
        var settings = settingsService.Load();
        _texts = new AppText(settings.Language);
        _scalingAlgorithm = settings.PreviewScalingAlgorithm;
        _windowTitle = _texts.VapourSynthPreviewWindowTitle(_texts.VapourSynthWorkspaceTitle);
        _statusText = _texts.VapourSynthPreviewIdleStatus;
        _currentTimeText = _texts.VapourSynthPreviewUnknownTime;
        _totalTimeText = _texts.VapourSynthPreviewUnknownTime;
        _resolutionText = "-";
        _formatText = "-";
        _bitDepthText = "-";
        _fpsText = "-";
        _frameTypeText = "-";
        _framePropsText = _texts.VapourSynthPreviewFramePropsPlaceholder;
        _stepSize = 1;
        _zoomRatio = 1.0;
        _cropZoomPercentage = 200;
        _timeStepSeconds = 1.0;
        _snapshotTemplate = "{scriptName}-out{output}-frame{frame}";
        Outputs = [];
        ZoomModes = [];
        TimelineModes = [];
        CropModes = [];
        OutputSyncModes = [];
        RebuildChoiceLists();
    }

    public AppText Texts
    {
        get => _texts;
        private set => SetProperty(ref _texts, value);
    }

    public ObservableCollection<VapourSynthPreviewOutputOption> Outputs { get; }

    public ObservableCollection<PreviewCompareModeOption> CompareModes { get; } = [];

    public ObservableCollection<PreviewDifferenceGainOption> DifferenceGains { get; } = [];

    public ObservableCollection<PreviewCompareSizeAlignmentOption> SizeAlignments { get; } = [];

    public ObservableCollection<StringChoiceOption> ZoomModes { get; }

    public ObservableCollection<StringChoiceOption> TimelineModes { get; }

    public ObservableCollection<StringChoiceOption> CropModes { get; }

    public ObservableCollection<StringChoiceOption> OutputSyncModes { get; }

    public ObservableCollection<StringChoiceOption> ScalingAlgorithmModes { get; } = [];

    public ObservableCollection<VapourSynthPreviewChapterOption> Chapters { get; } = [];

    public string WindowTitle
    {
        get => _windowTitle;
        private set => SetProperty(ref _windowTitle, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentTimeText
    {
        get => _currentTimeText;
        private set
        {
            if (SetProperty(ref _currentTimeText, value))
            {
                RaiseTimelinePropertyChanges();
            }
        }
    }

    public string TotalTimeText
    {
        get => _totalTimeText;
        private set
        {
            if (SetProperty(ref _totalTimeText, value))
            {
                RaiseTimelinePropertyChanges();
            }
        }
    }

    public string ResolutionText
    {
        get => _resolutionText;
        private set => SetProperty(ref _resolutionText, value);
    }

    public string FormatText
    {
        get => _formatText;
        private set => SetProperty(ref _formatText, value);
    }

    public string BitDepthText
    {
        get => _bitDepthText;
        private set => SetProperty(ref _bitDepthText, value);
    }

    public string FpsText
    {
        get => _fpsText;
        private set => SetProperty(ref _fpsText, value);
    }

    public string FrameTypeText
    {
        get => _frameTypeText;
        private set => SetProperty(ref _frameTypeText, value);
    }

    public string FramePropsText
    {
        get => _framePropsText;
        private set => SetProperty(ref _framePropsText, value);
    }

    public double PreviewImageWidth
    {
        get => _previewImageWidth;
        private set => SetProperty(ref _previewImageWidth, value);
    }

    public double PreviewImageHeight
    {
        get => _previewImageHeight;
        private set => SetProperty(ref _previewImageHeight, value);
    }

    public double SecondaryPreviewImageWidth
    {
        get => _secondaryPreviewImageWidth;
        private set => SetProperty(ref _secondaryPreviewImageWidth, value);
    }

    public double SecondaryPreviewImageHeight
    {
        get => _secondaryPreviewImageHeight;
        private set => SetProperty(ref _secondaryPreviewImageHeight, value);
    }

    public int CurrentFrame
    {
        get => _currentFrame;
        private set
        {
            if (SetProperty(ref _currentFrame, value))
            {
                RaiseTimelinePropertyChanges();
            }
        }
    }

    public int TotalFrames
    {
        get => _totalFrames;
        private set
        {
            if (SetProperty(ref _totalFrames, value))
            {
                RaiseTimelinePropertyChanges();
            }
        }
    }

    public int StepSize
    {
        get => _stepSize;
        set => SetProperty(ref _stepSize, Math.Max(1, value));
    }

    public double FrameSliderMaximum
    {
        get => _frameSliderMaximum;
        private set => SetProperty(ref _frameSliderMaximum, value);
    }

    public bool IsFramePropsVisible
    {
        get => _isFramePropsVisible;
        set
        {
            if (SetProperty(ref _isFramePropsVisible, value))
            {
                OnPropertyChanged(nameof(FramePropsVisibility));
            }
        }
    }

    public bool IsCropPanelVisible
    {
        get => _isCropPanelVisible;
        set
        {
            if (SetProperty(ref _isCropPanelVisible, value))
            {
                RecomputeImageLayout();
                OnPropertyChanged(nameof(CropPanelVisibility));
            }
        }
    }

    public Visibility FramePropsVisibility => _isFramePropsVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CropPanelVisibility => _isCropPanelVisible ? Visibility.Visible : Visibility.Collapsed;

    public double ZoomRatio
    {
        get => _zoomRatio;
        set
        {
            var normalized = Math.Clamp(value, 0.05, 16.0);
            if (SetProperty(ref _zoomRatio, normalized))
            {
                RecomputeImageLayout();
            }
        }
    }

    public double CropZoomPercentage
    {
        get => _cropZoomPercentage;
        set
        {
            var normalized = Math.Clamp(value, 10, 800);
            if (SetProperty(ref _cropZoomPercentage, normalized))
            {
                RecomputeImageLayout();
            }
        }
    }

    public double TimeStepSeconds
    {
        get => _timeStepSeconds;
        set => SetProperty(ref _timeStepSeconds, Math.Clamp(value, 0.001, 3600));
    }

    public bool SilentSnapshotEnabled
    {
        get => _silentSnapshotEnabled;
        set => SetProperty(ref _silentSnapshotEnabled, value);
    }

    public string SnapshotTemplate
    {
        get => _snapshotTemplate;
        set => SetProperty(ref _snapshotTemplate, value ?? string.Empty);
    }

    public VapourSynthPreviewOutputOption? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (SetProperty(ref _selectedOutput, value))
            {
                UpdateCompareBadges();
            }
        }
    }

    public VapourSynthPreviewOutputOption? SelectedSecondaryOutput
    {
        get => _selectedSecondaryOutput;
        set
        {
            if (SetProperty(ref _selectedSecondaryOutput, value))
            {
                UpdateCompareBadges();
            }
        }
    }

    public PreviewCompareMode CompareMode
    {
        get => _compareMode;
        set
        {
            var normalized = PreviewCompareCoordinator.NormalizeMode(value, Outputs.Count);
            if (SetProperty(ref _compareMode, normalized))
            {
                _selectedCompareModeOption = CompareModes.FirstOrDefault(option => option.Mode == normalized)
                    ?? CompareModes.FirstOrDefault();
                OnPropertyChanged(nameof(SelectedCompareModeOption));
                RaiseCompareStatePropertyChanges();
                RecomputeImageLayout();
            }
        }
    }

    public PreviewCompareModeOption? SelectedCompareModeOption
    {
        get => _selectedCompareModeOption;
        set
        {
            if (value is null)
            {
                return;
            }

            CompareMode = value.Mode;
        }
    }

    public PreviewDifferenceGain DifferenceGain
    {
        get => _differenceGain;
        set
        {
            if (SetProperty(ref _differenceGain, value))
            {
                _selectedDifferenceGainOption = DifferenceGains.FirstOrDefault(option => option.Gain == value)
                    ?? DifferenceGains.FirstOrDefault();
                OnPropertyChanged(nameof(SelectedDifferenceGainOption));
            }
        }
    }

    public PreviewDifferenceGainOption? SelectedDifferenceGainOption
    {
        get => _selectedDifferenceGainOption;
        set
        {
            if (value is null)
            {
                return;
            }

            DifferenceGain = value.Gain;
        }
    }

    public PreviewCompareSizeAlignment SizeAlignment
    {
        get => _sizeAlignment;
        set
        {
            if (SetProperty(ref _sizeAlignment, value))
            {
                _selectedSizeAlignmentOption = SizeAlignments.FirstOrDefault(option => option.Alignment == value)
                    ?? SizeAlignments.FirstOrDefault();
                OnPropertyChanged(nameof(SelectedSizeAlignmentOption));
                RecomputeImageLayout();
            }
        }
    }

    public PreviewCompareSizeAlignmentOption? SelectedSizeAlignmentOption
    {
        get => _selectedSizeAlignmentOption;
        set
        {
            if (value is null)
            {
                return;
            }

            SizeAlignment = value.Alignment;
        }
    }

    public bool HasCompareDimensionMismatch
    {
        get => _hasCompareDimensionMismatch;
        private set
        {
            if (SetProperty(ref _hasCompareDimensionMismatch, value))
            {
                OnPropertyChanged(nameof(SizeAlignmentVisibility));
            }
        }
    }

    public double SplitRatio
    {
        get => _splitRatio;
        set
        {
            var clamped = PreviewCompareCoordinator.ClampSplitRatio(value);
            if (SetProperty(ref _splitRatio, clamped))
            {
                OnPropertyChanged(nameof(SplitPercentageText));
            }
        }
    }

    public string SplitPercentageText => $"{Math.Round(_splitRatio * 100)}%";

    public string CompareInfoText
    {
        get => _compareInfoText;
        private set => SetProperty(ref _compareInfoText, value);
    }

    public string CompareOutputABadgeText
    {
        get => _compareOutputABadgeText;
        private set => SetProperty(ref _compareOutputABadgeText, value);
    }

    public string CompareOutputBBadgeText
    {
        get => _compareOutputBBadgeText;
        private set => SetProperty(ref _compareOutputBBadgeText, value);
    }

    public bool CanCompareOutputs => PreviewCompareCoordinator.CanCompare(Outputs.Count);

    public bool IsCompareModeActive => CanCompareOutputs && _compareMode != PreviewCompareMode.Single;

    public bool IsWipeMode => CanCompareOutputs && _compareMode == PreviewCompareMode.Wipe;

    public bool IsSideBySideMode => CanCompareOutputs && _compareMode == PreviewCompareMode.SideBySide;

    public bool IsDifferenceMode => CanCompareOutputs && _compareMode == PreviewCompareMode.Difference;

    public Visibility CompareBarVisibility => IsCompareModeActive ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SingleViewportVisibility => IsSideBySideMode ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SideBySideViewportVisibility => IsSideBySideMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WipeOverlayVisibility => IsWipeMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DifferenceGainVisibility => IsDifferenceMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SizeAlignmentVisibility => (IsWipeMode || IsDifferenceMode) && HasCompareDimensionMismatch
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string CompareButtonTooltip => CanCompareOutputs
        ? Texts.VapourSynthPreviewCompareHint
        : Texts.VapourSynthPreviewCompareNeedsTwoOutputsHint;

    public StringChoiceOption? SelectedZoomMode
    {
        get => _selectedZoomMode;
        set
        {
            if (SetProperty(ref _selectedZoomMode, value))
            {
                RecomputeImageLayout();
                OnPropertyChanged(nameof(IsCustomZoomVisible));
                OnPropertyChanged(nameof(CustomZoomVisibility));
            }
        }
    }

    public StringChoiceOption? SelectedTimelineMode
    {
        get => _selectedTimelineMode;
        set
        {
            if (SetProperty(ref _selectedTimelineMode, value))
            {
                RaiseTimelinePropertyChanges();
            }
        }
    }

    public StringChoiceOption? SelectedCropMode
    {
        get => _selectedCropMode;
        set
        {
            if (SetProperty(ref _selectedCropMode, value))
            {
                OnPropertyChanged(nameof(IsAbsoluteCropMode));
            }
        }
    }

    public StringChoiceOption? SelectedOutputSyncMode
    {
        get => _selectedOutputSyncMode;
        set => SetProperty(ref _selectedOutputSyncMode, value);
    }

    public StringChoiceOption? SelectedScalingAlgorithmMode
    {
        get => _selectedScalingAlgorithmMode;
        set
        {
            if (SetProperty(ref _selectedScalingAlgorithmMode, value))
            {
                _scalingAlgorithm = _selectedScalingAlgorithmMode?.Value ?? "nearest";
            }
        }
    }

    public string ScalingAlgorithm => _selectedScalingAlgorithmMode?.Value ?? "nearest";

    public bool IsAbsoluteCropMode => string.Equals(_selectedCropMode?.Value, "absolute", StringComparison.OrdinalIgnoreCase);

    public Visibility CustomZoomVisibility => IsCustomZoomVisible ? Visibility.Visible : Visibility.Collapsed;

    public bool IsCustomZoomVisible => string.Equals(_selectedZoomMode?.Value, "custom", StringComparison.OrdinalIgnoreCase);

    public bool IsTimelineTimeMode => string.Equals(_selectedTimelineMode?.Value, "time", StringComparison.OrdinalIgnoreCase);

    public string FrameProgressText => _totalFrames <= 0
        ? "0 / 0"
        : $"{_currentFrame} / {Math.Max(0, _totalFrames - 1)}";

    public string TimelinePositionText => IsTimelineTimeMode
        ? _currentTimeText
        : _currentFrame.ToString();

    public string TimelineTotalText => IsTimelineTimeMode
        ? _totalTimeText
        : Math.Max(0, _totalFrames - 1).ToString();

    public string TimelineSummaryText => IsTimelineTimeMode
        ? $"{_currentTimeText} / {_totalTimeText}"
        : FrameProgressText;

    public bool HasChapters => Chapters.Count > 0;

    public void ApplyLanguage(AppLanguage language)
    {
        if (Texts.Language == language)
        {
            return;
        }

        Texts = new AppText(language);
        RebuildChoiceLists();
        WindowTitle = Texts.VapourSynthPreviewWindowTitle(WindowTitleDocumentName);

        if (string.IsNullOrWhiteSpace(FramePropsText))
        {
            FramePropsText = Texts.VapourSynthPreviewFramePropsPlaceholder;
        }

        RaiseTimelinePropertyChanges();
        OnPropertyChanged(nameof(IsCustomZoomVisible));
        OnPropertyChanged(nameof(CustomZoomVisibility));
        OnPropertyChanged(nameof(IsAbsoluteCropMode));
        RaiseCompareStatePropertyChanges();
        UpdateCompareBadges();
        RebuildChapterLabels();
    }

    public string WindowTitleDocumentName { get; private set; } = "VapourSynth";

    public void ResetForSession(string documentName, string statusText, IReadOnlyList<VapourSynthPreviewOutputInfo> outputs)
    {
        var previousSecondaryIndex = SelectedSecondaryOutput?.Info.Index;

        WindowTitleDocumentName = string.IsNullOrWhiteSpace(documentName)
            ? Texts.VapourSynthWorkspaceTitle
            : documentName;
        WindowTitle = Texts.VapourSynthPreviewWindowTitle(WindowTitleDocumentName);
        StatusText = statusText;

        Outputs.Clear();
        foreach (var output in outputs.OrderBy(static item => item.Index))
        {
            Outputs.Add(new VapourSynthPreviewOutputOption(
                output,
                Texts.VapourSynthPreviewOutputLabel(output.Index, output.Name)));
        }

        SelectedOutput = Outputs.FirstOrDefault();
        var resolvedSecondaryIndex = SelectedOutput is null
            ? null
            : PreviewCompareCoordinator.ResolveSecondaryOutputIndex(
                Outputs.Select(static item => item.Info).ToArray(),
                SelectedOutput.Info.Index,
                previousSecondaryIndex);
        SelectedSecondaryOutput = resolvedSecondaryIndex is int secondaryIndex
            ? Outputs.FirstOrDefault(option => option.Info.Index == secondaryIndex)
            : null;
        if (!CanCompareOutputs && _compareMode != PreviewCompareMode.Single)
        {
            _compareMode = PreviewCompareMode.Single;
            _selectedCompareModeOption = CompareModes.FirstOrDefault(option => option.Mode == PreviewCompareMode.Single);
            OnPropertyChanged(nameof(CompareMode));
            OnPropertyChanged(nameof(SelectedCompareModeOption));
        }

        CurrentFrame = 0;
        TotalFrames = SelectedOutput?.Info.TotalFrames ?? 0;
        FrameSliderMaximum = Math.Max(0, TotalFrames - 1);
        FramePropsText = Texts.VapourSynthPreviewFramePropsPlaceholder;
        CurrentTimeText = Texts.VapourSynthPreviewUnknownTime;
        TotalTimeText = Texts.VapourSynthPreviewUnknownTime;
        ResolutionText = "-";
        FormatText = "-";
        BitDepthText = "-";
        FpsText = "-";
        FrameTypeText = "-";
        CompareInfoText = string.Empty;
        _sourceFrameWidth = 0;
        _sourceFrameHeight = 0;
        _secondaryFrameWidth = 0;
        _secondaryFrameHeight = 0;
        RaiseCompareStatePropertyChanges();
        UpdateCompareBadges();
        RecomputeImageLayout();
        RaiseTimelinePropertyChanges();
    }

    public void ReplaceChapters(IEnumerable<VapourSynthPreviewChapterOption> chapters)
    {
        Chapters.Clear();
        foreach (var chapter in chapters.OrderBy(static item => item.Timecode))
        {
            Chapters.Add(chapter);
        }

        OnPropertyChanged(nameof(HasChapters));
    }

    public void NotifyChaptersChanged()
    {
        OnPropertyChanged(nameof(HasChapters));
    }

    public void UpdateForOutput(VapourSynthPreviewOutputInfo outputInfo)
    {
        TotalFrames = outputInfo.TotalFrames;
        FrameSliderMaximum = Math.Max(0, outputInfo.TotalFrames - 1);
        ApplyOutputMetadata(outputInfo, $"{outputInfo.Width} x {outputInfo.Height}");
        RaiseTimelinePropertyChanges();
    }

    public void UpdateFrame(
        VapourSynthPreviewOutputInfo outputInfo,
        VapourSynthPreviewFrameData frameData,
        int displayWidth,
        int displayHeight,
        string resolutionText,
        VapourSynthPreviewOutputInfo? secondaryOutputInfo = null,
        VapourSynthPreviewFrameData? secondaryFrameData = null,
        int secondaryDisplayWidth = 0,
        int secondaryDisplayHeight = 0)
    {
        CurrentFrame = frameData.FrameNumber;
        ApplyOutputMetadata(outputInfo, resolutionText);
        CurrentTimeText = FormatTimestamp(frameData.FrameNumber, outputInfo.FpsNumerator, outputInfo.FpsDenominator);
        FrameTypeText = string.IsNullOrWhiteSpace(frameData.FrameType) ? "-" : frameData.FrameType;

        if (IsCompareModeActive && secondaryOutputInfo is not null && secondaryFrameData is not null)
        {
            var propsA = FormatFrameProperties(frameData.Properties);
            var propsB = FormatFrameProperties(secondaryFrameData.Properties);
            FramePropsText = string.Join(
                Environment.NewLine,
                Texts.VapourSynthPreviewComparePropsSectionHeader("A", outputInfo.Index, frameData.FrameNumber),
                propsA,
                string.Empty,
                Texts.VapourSynthPreviewComparePropsSectionHeader("B", secondaryOutputInfo.Index, secondaryFrameData.FrameNumber),
                propsB);

            var frameHint = Texts.VapourSynthPreviewCompareSecondaryFrameHint(secondaryOutputInfo.Index, secondaryFrameData.FrameNumber);
            var secondaryDisplayW = secondaryDisplayWidth > 0 ? secondaryDisplayWidth : secondaryFrameData.Width;
            var secondaryDisplayH = secondaryDisplayHeight > 0 ? secondaryDisplayHeight : secondaryFrameData.Height;
            HasCompareDimensionMismatch = displayWidth != secondaryDisplayW || displayHeight != secondaryDisplayH;
            CompareInfoText = !HasCompareDimensionMismatch
                ? frameHint
                : $"{frameHint} · {Texts.VapourSynthPreviewCompareDimensionMismatchHint(displayWidth, displayHeight, secondaryDisplayW, secondaryDisplayH)}";

            _secondaryFrameWidth = secondaryDisplayW;
            _secondaryFrameHeight = secondaryDisplayH;
        }
        else
        {
            FramePropsText = FormatFrameProperties(frameData.Properties);
            HasCompareDimensionMismatch = false;
            CompareInfoText = string.Empty;
            _secondaryFrameWidth = 0;
            _secondaryFrameHeight = 0;
        }

        _sourceFrameWidth = displayWidth;
        _sourceFrameHeight = displayHeight;
        UpdateCompareBadges(frameData.FrameNumber, secondaryFrameData?.FrameNumber);
        RecomputeImageLayout();
        RaiseTimelinePropertyChanges();
    }

    private string FormatFrameProperties(IReadOnlyList<VapourSynthPreviewFrameProperty> properties)
    {
        return properties.Count == 0
            ? Texts.VapourSynthPreviewFramePropsEmpty
            : string.Join(Environment.NewLine, properties.Select(static property => $"{property.Key} = {property.Value}"));
    }

    private void ApplyOutputMetadata(VapourSynthPreviewOutputInfo outputInfo, string resolutionText)
    {
        ResolutionText = resolutionText;
        FormatText = outputInfo.FormatName;
        BitDepthText = FormatBitDepth(outputInfo.BitsPerSample);
        FpsText = FormatFps(outputInfo.FpsNumerator, outputInfo.FpsDenominator);
        TotalTimeText = FormatTimestamp(outputInfo.TotalFrames - 1, outputInfo.FpsNumerator, outputInfo.FpsDenominator);
    }

    public void SaveScalingAlgorithmPreference()
    {
        var currentSettings = _settingsService.Load();
        if (string.Equals(currentSettings.PreviewScalingAlgorithm, ScalingAlgorithm, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settingsService.Save(currentSettings with
        {
            PreviewScalingAlgorithm = ScalingAlgorithm
        });
    }

    public void UpdateStatus(string statusText)
    {
        StatusText = statusText;
    }

    public void UpdateViewport(double width, double height)
    {
        _viewportWidth = Math.Max(0, width);
        _viewportHeight = Math.Max(0, height);
        RecomputeImageLayout();
    }

    public void UpdateDisplayScale(double scale)
    {
        var normalized = scale > 0 ? scale : 1.0;
        if (Math.Abs(_displayScale - normalized) < 0.001)
        {
            return;
        }

        _displayScale = normalized;
        RecomputeImageLayout();
    }

    private void RebuildChoiceLists()
    {
        var previousZoomValue = _selectedZoomMode?.Value ?? "actual";
        var previousTimelineValue = _selectedTimelineMode?.Value ?? "frames";
        var previousCropModeValue = _selectedCropMode?.Value ?? "relative";
        var previousOutputSyncValue = _selectedOutputSyncMode?.Value ?? "timeline";

        ZoomModes.Clear();
        ZoomModes.Add(new StringChoiceOption("fit", Texts.VapourSynthPreviewZoomFitLabel));
        ZoomModes.Add(new StringChoiceOption("actual", Texts.VapourSynthPreviewZoomActualLabel));
        ZoomModes.Add(new StringChoiceOption("custom", Texts.VapourSynthPreviewZoomCustomLabel));
        SelectedZoomMode = ZoomModes.FirstOrDefault(option => option.Value == previousZoomValue) ?? ZoomModes[0];

        TimelineModes.Clear();
        TimelineModes.Add(new StringChoiceOption("frames", Texts.VapourSynthPreviewTimelineFramesMode));
        TimelineModes.Add(new StringChoiceOption("time", Texts.VapourSynthPreviewTimelineTimeMode));
        SelectedTimelineMode = TimelineModes.FirstOrDefault(option => option.Value == previousTimelineValue) ?? TimelineModes[0];

        CropModes.Clear();
        CropModes.Add(new StringChoiceOption("relative", Texts.VapourSynthPreviewCropRelativeMode));
        CropModes.Add(new StringChoiceOption("absolute", Texts.VapourSynthPreviewCropAbsoluteMode));
        SelectedCropMode = CropModes.FirstOrDefault(option => option.Value == previousCropModeValue) ?? CropModes[0];

        OutputSyncModes.Clear();
        OutputSyncModes.Add(new StringChoiceOption("remember", Texts.VapourSynthPreviewOutputSyncRemember));
        OutputSyncModes.Add(new StringChoiceOption("frame", Texts.VapourSynthPreviewOutputSyncFrame));
        OutputSyncModes.Add(new StringChoiceOption("time", Texts.VapourSynthPreviewOutputSyncTimestamp));
        OutputSyncModes.Add(new StringChoiceOption("timeline", Texts.VapourSynthPreviewOutputSyncTimeline));
        SelectedOutputSyncMode = OutputSyncModes.FirstOrDefault(option => option.Value == previousOutputSyncValue) ?? OutputSyncModes[0];

        var previousScalingAlgoValue = _selectedScalingAlgorithmMode?.Value ?? _scalingAlgorithm;
        ScalingAlgorithmModes.Clear();
        ScalingAlgorithmModes.Add(new StringChoiceOption("nearest", Texts.VapourSynthPreviewScalingAlgorithmNearest));
        ScalingAlgorithmModes.Add(new StringChoiceOption("bilinear", Texts.VapourSynthPreviewScalingAlgorithmBilinear));
        SelectedScalingAlgorithmMode = ScalingAlgorithmModes.FirstOrDefault(option => option.Value == previousScalingAlgoValue) ?? ScalingAlgorithmModes[0];

        CompareModes.Clear();
        CompareModes.Add(new PreviewCompareModeOption(PreviewCompareMode.Single, Texts.VapourSynthPreviewCompareModeLabel(PreviewCompareMode.Single)));
        CompareModes.Add(new PreviewCompareModeOption(PreviewCompareMode.Wipe, Texts.VapourSynthPreviewCompareModeLabel(PreviewCompareMode.Wipe)));
        CompareModes.Add(new PreviewCompareModeOption(PreviewCompareMode.SideBySide, Texts.VapourSynthPreviewCompareModeLabel(PreviewCompareMode.SideBySide)));
        CompareModes.Add(new PreviewCompareModeOption(PreviewCompareMode.Difference, Texts.VapourSynthPreviewCompareModeLabel(PreviewCompareMode.Difference)));
        _selectedCompareModeOption = CompareModes.FirstOrDefault(option => option.Mode == _compareMode) ?? CompareModes[0];
        OnPropertyChanged(nameof(SelectedCompareModeOption));

        DifferenceGains.Clear();
        DifferenceGains.Add(new PreviewDifferenceGainOption(PreviewDifferenceGain.X1, Texts.VapourSynthPreviewCompareDiffGainLabel(PreviewDifferenceGain.X1)));
        DifferenceGains.Add(new PreviewDifferenceGainOption(PreviewDifferenceGain.X4, Texts.VapourSynthPreviewCompareDiffGainLabel(PreviewDifferenceGain.X4)));
        DifferenceGains.Add(new PreviewDifferenceGainOption(PreviewDifferenceGain.X16, Texts.VapourSynthPreviewCompareDiffGainLabel(PreviewDifferenceGain.X16)));
        DifferenceGains.Add(new PreviewDifferenceGainOption(PreviewDifferenceGain.Boost, Texts.VapourSynthPreviewCompareDiffGainLabel(PreviewDifferenceGain.Boost)));
        _selectedDifferenceGainOption = DifferenceGains.FirstOrDefault(option => option.Gain == _differenceGain) ?? DifferenceGains[0];
        OnPropertyChanged(nameof(SelectedDifferenceGainOption));

        SizeAlignments.Clear();
        SizeAlignments.Add(new PreviewCompareSizeAlignmentOption(PreviewCompareSizeAlignment.Center1To1, Texts.VapourSynthPreviewCompareSizeAlignmentLabel(PreviewCompareSizeAlignment.Center1To1)));
        SizeAlignments.Add(new PreviewCompareSizeAlignmentOption(PreviewCompareSizeAlignment.UniformFit, Texts.VapourSynthPreviewCompareSizeAlignmentLabel(PreviewCompareSizeAlignment.UniformFit)));
        _selectedSizeAlignmentOption = SizeAlignments.FirstOrDefault(option => option.Alignment == _sizeAlignment) ?? SizeAlignments[0];
        OnPropertyChanged(nameof(SelectedSizeAlignmentOption));

        for (var i = 0; i < Outputs.Count; i++)
        {
            var info = Outputs[i].Info;
            Outputs[i] = new VapourSynthPreviewOutputOption(info, Texts.VapourSynthPreviewOutputLabel(info.Index, info.Name));
        }

        if (_selectedOutput is not null)
        {
            _selectedOutput = Outputs.FirstOrDefault(option => option.Info.Index == _selectedOutput.Info.Index);
            OnPropertyChanged(nameof(SelectedOutput));
        }

        if (_selectedSecondaryOutput is not null)
        {
            _selectedSecondaryOutput = Outputs.FirstOrDefault(option => option.Info.Index == _selectedSecondaryOutput.Info.Index);
            OnPropertyChanged(nameof(SelectedSecondaryOutput));
        }
    }

    private void RaiseCompareStatePropertyChanges()
    {
        OnPropertyChanged(nameof(CanCompareOutputs));
        OnPropertyChanged(nameof(IsCompareModeActive));
        OnPropertyChanged(nameof(IsWipeMode));
        OnPropertyChanged(nameof(IsSideBySideMode));
        OnPropertyChanged(nameof(IsDifferenceMode));
        OnPropertyChanged(nameof(CompareBarVisibility));
        OnPropertyChanged(nameof(SingleViewportVisibility));
        OnPropertyChanged(nameof(SideBySideViewportVisibility));
        OnPropertyChanged(nameof(WipeOverlayVisibility));
        OnPropertyChanged(nameof(DifferenceGainVisibility));
        OnPropertyChanged(nameof(SizeAlignmentVisibility));
        OnPropertyChanged(nameof(CompareButtonTooltip));
    }

    private void UpdateCompareBadges(int? primaryFrame = null, int? secondaryFrame = null)
    {
        if (_selectedOutput is not null)
        {
            var frameSuffix = primaryFrame.HasValue ? $" · #{primaryFrame.Value}" : string.Empty;
            CompareOutputABadgeText = $"A · {_selectedOutput.Label}{frameSuffix}";
        }
        else
        {
            CompareOutputABadgeText = "A";
        }

        if (_selectedSecondaryOutput is not null)
        {
            var frameSuffix = secondaryFrame.HasValue ? $" · #{secondaryFrame.Value}" : string.Empty;
            CompareOutputBBadgeText = $"B · {_selectedSecondaryOutput.Label}{frameSuffix}";
        }
        else
        {
            CompareOutputBBadgeText = "B";
        }
    }

    private string FormatTimestamp(int frameNumber, int fpsNumerator, int fpsDenominator)
    {
        if (frameNumber < 0 || fpsNumerator <= 0 || fpsDenominator <= 0)
        {
            return Texts.VapourSynthPreviewUnknownTime;
        }

        var seconds = frameNumber / (fpsNumerator / (double)fpsDenominator);
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss\.fff")
            : time.ToString(@"mm\:ss\.fff");
    }

    private string FormatFps(int fpsNumerator, int fpsDenominator)
    {
        return fpsNumerator > 0 && fpsDenominator > 0
            ? $"{fpsNumerator}/{fpsDenominator} = {(fpsNumerator / (double)fpsDenominator):0.###}"
            : Texts.VapourSynthPreviewUnknownTime;
    }

    private static string FormatBitDepth(int bitsPerSample)
    {
        return bitsPerSample > 0 ? $"{bitsPerSample}-bit" : "-";
    }

    private void RaiseTimelinePropertyChanges()
    {
        OnPropertyChanged(nameof(FrameProgressText));
        OnPropertyChanged(nameof(IsTimelineTimeMode));
        OnPropertyChanged(nameof(TimelinePositionText));
        OnPropertyChanged(nameof(TimelineTotalText));
        OnPropertyChanged(nameof(TimelineSummaryText));
    }

    private void RecomputeImageLayout()
    {
        if (_sourceFrameWidth <= 0 || _sourceFrameHeight <= 0)
        {
            PreviewImageWidth = 0;
            PreviewImageHeight = 0;
            SecondaryPreviewImageWidth = 0;
            SecondaryPreviewImageHeight = 0;
            return;
        }

        var useAlignedCanvas = (IsWipeMode || IsDifferenceMode)
            && _secondaryFrameWidth > 0
            && _secondaryFrameHeight > 0;
        var primaryCanvasWidth = useAlignedCanvas
            ? Math.Max(_sourceFrameWidth, _secondaryFrameWidth)
            : _sourceFrameWidth;
        var primaryCanvasHeight = useAlignedCanvas
            ? Math.Max(_sourceFrameHeight, _secondaryFrameHeight)
            : _sourceFrameHeight;

        var mode = _selectedZoomMode?.Value ?? "actual";
        var displayScale = _displayScale > 0 ? _displayScale : 1.0;
        var effectiveViewportWidth = PreviewCompareCoordinator.ComputeEffectiveViewportWidth(
            _viewportWidth,
            IsSideBySideMode ? PreviewCompareMode.SideBySide : PreviewCompareMode.Single);
        double ratio;
        if (string.Equals(mode, "actual", StringComparison.OrdinalIgnoreCase))
        {
            ratio = 1.0 / displayScale;
        }
        else if (string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase))
        {
            ratio = _zoomRatio / displayScale;
        }
        else
        {
            if (effectiveViewportWidth <= 1 || _viewportHeight <= 1)
            {
                ratio = 1.0 / displayScale;
            }
            else
            {
                var scaleX = effectiveViewportWidth / primaryCanvasWidth;
                var scaleY = _viewportHeight / primaryCanvasHeight;
                ratio = Math.Min(scaleX, scaleY);
            }
        }

        if (_isCropPanelVisible)
        {
            ratio *= _cropZoomPercentage / 100.0;
        }

        if (ratio <= 0 || double.IsNaN(ratio) || double.IsInfinity(ratio))
        {
            ratio = 1.0 / displayScale;
        }

        PreviewImageWidth = Math.Max(1, Math.Round(primaryCanvasWidth * ratio));
        PreviewImageHeight = Math.Max(1, Math.Round(primaryCanvasHeight * ratio));

        if (IsSideBySideMode && _secondaryFrameWidth > 0 && _secondaryFrameHeight > 0)
        {
            double secondaryRatio;
            if (string.Equals(mode, "fit", StringComparison.OrdinalIgnoreCase)
                && effectiveViewportWidth > 1
                && _viewportHeight > 1)
            {
                var scaleX = effectiveViewportWidth / _secondaryFrameWidth;
                var scaleY = _viewportHeight / _secondaryFrameHeight;
                secondaryRatio = Math.Min(scaleX, scaleY);
                if (_isCropPanelVisible)
                {
                    secondaryRatio *= _cropZoomPercentage / 100.0;
                }
            }
            else
            {
                secondaryRatio = ratio;
            }

            SecondaryPreviewImageWidth = Math.Max(1, Math.Round(_secondaryFrameWidth * secondaryRatio));
            SecondaryPreviewImageHeight = Math.Max(1, Math.Round(_secondaryFrameHeight * secondaryRatio));
        }
        else
        {
            SecondaryPreviewImageWidth = PreviewImageWidth;
            SecondaryPreviewImageHeight = PreviewImageHeight;
        }
    }

    private void RebuildChapterLabels()
    {
        for (var index = 0; index < Chapters.Count; index++)
        {
            Chapters[index] = Chapters[index] with { Label = FormatChapterLabel(index + 1, Chapters[index].Timecode, Chapters[index].Title) };
        }
    }

    public string FormatChapterLabel(int index, TimeSpan timecode, string title)
    {
        var name = string.IsNullOrWhiteSpace(title)
            ? Texts.VapourSynthPreviewChapterFallbackTitle(index)
            : title.Trim();
        return $"{FormatChapterTimecode(timecode)} · {name}";
    }

    public static string FormatChapterTimecode(TimeSpan timecode)
    {
        return timecode.ToString(@"hh\:mm\:ss\.fff");
    }
}

public sealed record VapourSynthPreviewOutputOption(
    VapourSynthPreviewOutputInfo Info,
    string Label);

public sealed record PreviewCompareModeOption(
    PreviewCompareMode Mode,
    string Label);

public sealed record PreviewDifferenceGainOption(
    PreviewDifferenceGain Gain,
    string Label);

public sealed record PreviewCompareSizeAlignmentOption(
    PreviewCompareSizeAlignment Alignment,
    string Label);

public sealed record VapourSynthPreviewChapterOption(
    TimeSpan Timecode,
    string Title,
    string Label);
