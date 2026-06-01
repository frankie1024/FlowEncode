using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FlowEncode.ViewModels;

public partial class MainWindowViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IDisposable, ISetupGuideHost
{
    private const string AppReleasePageUrl = "https://github.com/frankie1024/FlowEncode/releases";
    private const int MinConcurrentEncodingJobs = 1;
    private const int MaxConcurrentEncodingJobsLimit = 5;
    private static readonly TimeSpan InputPathRefreshDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan QueueCompletionActionIdleRequirement = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QueueCompletionActionIdlePollInterval = TimeSpan.FromSeconds(5);
    private readonly IEncoderToolchainService _toolchainService;
    private readonly IProfileLibraryService _profileLibraryService;
    private readonly IEncodingJobRunner _jobRunner;
    private readonly IQueueCompletionActionService _queueCompletionActionService;
    private readonly ISystemIdleService _systemIdleService;
    private readonly IAutoCompressionRunner _autoCompressionRunner;
    private readonly IAudioProcessingRunner _audioProcessingRunner;
    private readonly IAudioSourceInfoService _audioSourceInfoService;
    private readonly IBluRayDiscProbeService _bluRayDiscProbeService;
    private readonly IBluRayDemuxRunner _bluRayDemuxRunner;
    private readonly IAppSettingsService _settingsService;
    private readonly ISetupGuideCacheService _setupGuideCacheService;
    private readonly IToolRegistryService _toolRegistryService;
    private readonly IToolProbeService _toolProbeService;
    private readonly IEncoderDiscoveryService _encoderDiscoveryService;
    private readonly IEnvironmentReadinessService _environmentReadinessService;
    private readonly ISetupBootstrapService _setupBootstrapService;
    private readonly IAppUpdateService _appUpdateService;

    private readonly LocalAppPaths _appPaths;

    private EncodingProfile? _activeProfile;
    private EnvironmentReadinessReport? _environmentReadinessReport;
    private bool _isShuttingDown;
    private bool _isRefreshingCatalog;
    private bool _isCheckingUpdates;
    private bool _isDownloadingAppUpdateInstaller;
    private int? _appUpdateDownloadProgressPercent;
    private string _statusText = "环境已准备完成，等待首次刷新。";
    private string _previewTitle = "选择一个预设以生成命令预览";
    private string _previewCommandLine = string.Empty;
    private string _previewNotes = "预览命令会围绕后续的作业队列和滤镜管线展开。";
    private string _selectedProfileCaption = "尚未选择预设";
    private string _draftTemplateName = string.Empty;
    private string _draftTemplateNotes = string.Empty;
    private string _sourcePath = string.Empty;
    private string _outputPath = string.Empty;
    private AppText _texts = new(AppLanguage.Chinese);
    private ThemeOption? _selectedTheme;
    private LanguageOption? _selectedLanguage;
    private EncoderOption? _selectedEncoder;
    private AutoCompressionMetricOption? _selectedAutoCompressionMetricOption;
    private StringChoiceOption? _selectedAutoCompressionInterpolationMethodOption;
    private StringChoiceOption? _selectedAutoCompressionProbingStatisticOption;
    private RateControlOption? _selectedRateControl;
    private StringChoiceOption? _selectedPreset;
    private StringChoiceOption? _selectedTune;
    private StringChoiceOption? _selectedProfileOption;
    private StringChoiceOption? _selectedOutputFormat;
    private StringChoiceOption? _selectedConcurrentEncodingJobOption;
    private StringChoiceOption? _selectedQueueCompletionActionOption;
    private bool _preferSystemEncoders;
    private bool _autoCheckUpdatesOnStartup;
    private double _maxConcurrentEncodingJobs = MinConcurrentEncodingJobs;
    private QueueCompletionAction _queueCompletionAction = QueueCompletionAction.None;
    private IReadOnlyDictionary<string, string> _manualToolPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private bool _hasRunInitialVsPluginDependencyUpdate;
    private string _workspaceRootPath = string.Empty;
    private string _savedWorkspaceRootPath = string.Empty;
    private AppUpdateCheckResult? _lastAppUpdateResult;
    private string? _lastAppUpdateErrorMessage;
    private EncodingJobItemViewModel? _selectedJob;
    private readonly List<EncodingJobItemViewModel> _selectedQueueJobs = [];
    private string _draftAdditionalArguments = string.Empty;
    private string _draftUhdParameters = string.Empty;
    private double _draftQuality = 18.0;
    private double _draftBitrate = 3500.0;
    private string _draftProfileName = "x264 草稿";
    private string _draftProfileDescription = "先选择输入源和编码器，再微调当前作业的编码参数。";
    private string? _lastAutoOutputPath;
    private bool _isSynchronizingDraft;
    private bool _isUpdatingOutputPath;
    private bool _isQueueProcessing;
    private bool _isQueueCompletionActionArmed;
    private bool _queueCompletionActionBatchHadNonSuccessfulCompletion;
    private bool _isExecutingQueueCompletionAction;
    private CancellationTokenSource? _queueCompletionActionWaitCancellationTokenSource;
    private bool _isDisposed;
    private CancellationTokenSource? _previewRefreshCancellationTokenSource;
    private CancellationTokenSource? _draftInputRefreshCancellationTokenSource;
    private int _previewRefreshVersion;
    private int _draftInputRefreshVersion;
    private bool _isApplyingDeferredDraftInputRefresh;
    private bool _isDraftInputRefreshPending;

    public MainWindowViewModel(
        IEncoderToolchainService toolchainService,
        IProfileLibraryService profileLibraryService,
        IEncodingJobRunner jobRunner,
        IQueueCompletionActionService queueCompletionActionService,
        ISystemIdleService systemIdleService,
        IAutoCompressionRunner autoCompressionRunner,
        IAudioProcessingRunner audioProcessingRunner,
        IAudioSourceInfoService audioSourceInfoService,
        IBluRayDiscProbeService bluRayDiscProbeService,
        IBluRayDemuxRunner bluRayDemuxRunner,
        LocalAppPaths appPaths,
        IAppSettingsService settingsService,
        ISetupGuideCacheService setupGuideCacheService,
        IToolRegistryService toolRegistryService,
        IToolProbeService toolProbeService,
        IEncoderDiscoveryService encoderDiscoveryService,
        IEnvironmentReadinessService environmentReadinessService,
        ISetupBootstrapService setupBootstrapService,
        IAppUpdateService appUpdateService)
    {
        _toolchainService = toolchainService;
        _profileLibraryService = profileLibraryService;
        _jobRunner = jobRunner;
        _queueCompletionActionService = queueCompletionActionService;
        _systemIdleService = systemIdleService;
        _autoCompressionRunner = autoCompressionRunner;
        _audioProcessingRunner = audioProcessingRunner;
        _audioSourceInfoService = audioSourceInfoService;
        _bluRayDiscProbeService = bluRayDiscProbeService;
        _bluRayDemuxRunner = bluRayDemuxRunner;
        _appPaths = appPaths;
        _settingsService = settingsService;
        _setupGuideCacheService = setupGuideCacheService;
        _toolRegistryService = toolRegistryService;
        _toolProbeService = toolProbeService;
        _encoderDiscoveryService = encoderDiscoveryService;
        _environmentReadinessService = environmentReadinessService;
        _setupBootstrapService = setupBootstrapService;
        _appUpdateService = appUpdateService;

        ReplaceItems(ThemeOptions, BuildThemeOptions());
        ReplaceItems(
            LanguageOptions,
            [
                new LanguageOption(AppLanguage.Chinese, "中文"),
                new LanguageOption(AppLanguage.English, "English")
            ]);
        ReplaceItems(
            EncoderOptions,
            [
                new EncoderOption(EncoderKind.X264, EncoderKind.X264.ToDisplayName()),
                new EncoderOption(EncoderKind.X265, EncoderKind.X265.ToDisplayName()),
                new EncoderOption(EncoderKind.SvtAv1, EncoderKind.SvtAv1.ToDisplayName())
            ]);
        ReplaceItems(AutoCompressionMetricOptions, BuildAutoCompressionMetricOptions());
        ReplaceItems(AutoCompressionInterpolationMethodOptions, BuildAutoCompressionInterpolationMethodOptions());
        ReplaceItems(AutoCompressionProbingStatisticOptions, BuildAutoCompressionProbingStatisticOptions());
        ReplaceItems(ConcurrentEncodingJobOptions, BuildConcurrentEncodingJobOptions());
        ReplaceItems(QueueCompletionActionOptions, BuildQueueCompletionActionOptions());

        _selectedTheme = ThemeOptions[0];
        _selectedLanguage = LanguageOptions[0];
        _selectedEncoder = EncoderOptions[0];
        _selectedAutoEncoder = EncoderOptions[0];
        _selectedAutoCompressionMetricOption = AutoCompressionMetricOptions[0];
        _selectedAutoCompressionInterpolationMethodOption = AutoCompressionInterpolationMethodOptions.FirstOrDefault();
        _selectedAutoCompressionProbingStatisticOption = AutoCompressionProbingStatisticOptions[0];
        _selectedConcurrentEncodingJobOption = ConcurrentEncodingJobOptions[0];
        _selectedQueueCompletionActionOption = QueueCompletionActionOptions[0];
        _autoCompressionStatusText = _texts.AutoCompressionIdleStatus;
        InitializeModuleViewModels();
        InitializeAudioProcessingState();
        InitializeBluRayDemuxState();
    }

    internal ObservableCollection<EncoderCatalogItem> Encoders { get; } = [];

    internal ObservableCollection<EncodingJobItemViewModel> Jobs { get; } = [];

    internal ObservableCollection<DiscoveredEncoderBinary> DetectedSystemBinaries { get; } = [];

    internal ObservableCollection<ThemeOption> ThemeOptions { get; } = [];

    internal ObservableCollection<LanguageOption> LanguageOptions { get; } = [];

    internal ObservableCollection<EncoderOption> EncoderOptions { get; } = [];

    internal ObservableCollection<AutoCompressionMetricOption> AutoCompressionMetricOptions { get; } = [];

    internal ObservableCollection<StringChoiceOption> AutoCompressionInterpolationMethodOptions { get; } = [];

    internal ObservableCollection<StringChoiceOption> AutoCompressionProbingStatisticOptions { get; } = [];

    internal ObservableCollection<RateControlOption> AvailableRateControlModes { get; } = [];

    internal ObservableCollection<StringChoiceOption> AvailablePresets { get; } = [];

    internal ObservableCollection<StringChoiceOption> AvailableTunes { get; } = [];

    internal ObservableCollection<StringChoiceOption> AvailableProfiles { get; } = [];

    internal ObservableCollection<StringChoiceOption> AvailableOutputFormats { get; } = [];

    internal ObservableCollection<StringChoiceOption> ConcurrentEncodingJobOptions { get; } = [];

    internal ObservableCollection<StringChoiceOption> QueueCompletionActionOptions { get; } = [];

    internal bool IsBusy => _isRefreshingCatalog
        || _isCheckingUpdates
        || _isDownloadingAppUpdateInstaller
        || SetupGuideModule.IsSetupGuideInstallRunning
        || SetupGuideModule.IsRefreshingSetupGuide
        || SetupGuideModule.IsCheckingSetupDependencyUpdates;

    internal bool IsCheckingAppUpdates => _isCheckingUpdates;

    internal bool IsDownloadingAppUpdateInstaller => _isDownloadingAppUpdateInstaller;

    internal bool IsAppUpdateActionInProgress => _isCheckingUpdates || _isDownloadingAppUpdateInstaller;

    internal bool IsAppUpdateAvailable => _lastAppUpdateResult?.UpdateAvailable == true;

    internal bool CanDownloadAppUpdateInstaller => _lastAppUpdateResult?.CanDownloadInstaller == true;

    internal bool HasAppUpdateError => !string.IsNullOrWhiteSpace(_lastAppUpdateErrorMessage);

    internal string AppUpdateActionText => IsCheckingAppUpdates
        ? Texts.CheckingUpdatesButton
        : IsDownloadingAppUpdateInstaller
            ? _appUpdateDownloadProgressPercent.HasValue
                ? Texts.DownloadingUpdateButtonWithProgress(_appUpdateDownloadProgressPercent.Value)
                : Texts.DownloadingUpdateButton
        : IsAppUpdateAvailable
            ? CanDownloadAppUpdateInstaller
                ? Texts.UpdateButton
                : Texts.ReleasePageButton
            : Texts.CheckUpdatesButton;

    internal Symbol AppUpdateActionIcon => IsCheckingAppUpdates
        ? Symbol.Refresh
        : IsDownloadingAppUpdateInstaller
            ? Symbol.Download
            : IsAppUpdateAvailable
                ? CanDownloadAppUpdateInstaller
                    ? Symbol.Download
                    : Symbol.Link
                : Symbol.Refresh;

    internal bool CanExecuteAppUpdateAction => !IsCheckingAppUpdates && !IsDownloadingAppUpdateInstaller;

    internal Visibility AppUpdateProgressVisibility => IsAppUpdateActionInProgress
        ? Visibility.Visible
        : Visibility.Collapsed;

    internal string AppUpdateReleaseUrl => string.IsNullOrWhiteSpace(_lastAppUpdateResult?.ReleaseUrl)
        ? AppReleasePageUrl
        : _lastAppUpdateResult.ReleaseUrl;

    internal string AppCurrentVersionText => Texts.AppCurrentVersionLabel(GetKnownCurrentAppVersion());

    internal string AppLatestVersionText => string.IsNullOrWhiteSpace(_lastAppUpdateResult?.LatestVersion)
        ? string.Empty
        : Texts.AppLatestVersionLabel(_lastAppUpdateResult.LatestVersion);

    internal Visibility AppLatestVersionVisibility => string.IsNullOrWhiteSpace(_lastAppUpdateResult?.LatestVersion)
        ? Visibility.Collapsed
        : Visibility.Visible;

    internal string AppUpdateStatusText => GetAppUpdateStatusText();

    internal AppText Texts
    {
        get => _texts;
        private set => SetProperty(ref _texts, value);
    }

    internal string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    AppText ISetupGuideHost.Texts => Texts;

    string ISetupGuideHost.StatusText
    {
        get => StatusText;
        set => StatusText = value;
    }

    bool ISetupGuideHost.IsBusy => IsBusy;

    EnvironmentReadinessReport? ISetupGuideHost.EnvironmentReadinessReport => _environmentReadinessReport;

    IReadOnlyDictionary<string, string> ISetupGuideHost.ManualToolPaths
    {
        get => _manualToolPaths;
        set => _manualToolPaths = value;
    }

    bool ISetupGuideHost.HasCompletedSetupGuide
    {
        get => _hasCompletedSetupGuide;
        set => _hasCompletedSetupGuide = value;
    }

    string? ISetupGuideHost.SaveSettings(bool updateStatusText)
    {
        return SaveSettings(updateStatusText);
    }

    void ISetupGuideHost.NotifyEnvironmentReadinessChanged()
    {
        HandleAudioEnvironmentReadinessApplied();
        HandleBluRayEnvironmentReadinessApplied();
    }

    void ISetupGuideHost.NotifyBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
    }

    void ISetupGuideHost.InvalidateToolProbeCache()
    {
        _toolProbeService.InvalidateCache();
        _encoderDiscoveryService.InvalidateCache();
    }

    internal bool HasRunningJobs => Jobs.Any(static job => job.State == EncodingJobState.Running);

    internal bool HasRunningAppWork => HasRunningJobs
        || IsAutoCompressionRunning
        || IsAudioProcessingRunning
        || IsBluRayDemuxRunning;

    internal Visibility DashboardBluRayDemuxActivityVisibility => IsDashboardBluRayDemuxActive()
        ? Visibility.Visible
        : Visibility.Collapsed;

    internal double DashboardBluRayDemuxProgressValue => IsBluRayDemuxRunning
        ? BluRayDemuxProgressValue
        : 0.0;

    internal bool DashboardBluRayDemuxProgressIsIndeterminate => IsBluRayDiscScanning
        || IsBluRayPlaylistLoading
        || (IsBluRayDemuxRunning && BluRayDemuxProgressIsIndeterminate);

    internal Visibility DashboardOverviewActivityVisibility => GetDashboardRunningOverviewJob() is not null
        ? Visibility.Visible
        : Visibility.Collapsed;

    internal double DashboardOverviewProgressValue => GetDashboardRunningOverviewJob()?.ProgressValue ?? 0.0;

    internal bool DashboardOverviewProgressIsIndeterminate => GetDashboardRunningOverviewJob()?.IsProgressIndeterminate ?? false;

    internal Visibility DashboardAudioProcessingActivityVisibility => IsAudioProcessingRunning
        ? Visibility.Visible
        : Visibility.Collapsed;

    internal double DashboardAudioProcessingProgressValue => AudioProcessingProgressValue;

    internal bool DashboardAudioProcessingProgressIsIndeterminate => IsAudioProcessingRunning && AudioProcessingProgressIsIndeterminate;

    internal Visibility DashboardAutoCompressionActivityVisibility => IsAutoCompressionRunning
        ? Visibility.Visible
        : Visibility.Collapsed;

    internal double DashboardAutoCompressionProgressValue => AutoCompressionProgressPercent / 100.0;

    internal bool DashboardAutoCompressionProgressIsIndeterminate => IsAutoCompressionRunning && AutoCompressionProgressIsIndeterminate;

    internal string PreviewTitle
    {
        get => _previewTitle;
        private set => SetProperty(ref _previewTitle, value);
    }

    internal string PreviewCommandLine
    {
        get => _previewCommandLine;
        private set => SetProperty(ref _previewCommandLine, value);
    }

    internal string PreviewNotes
    {
        get => _previewNotes;
        private set => SetProperty(ref _previewNotes, value);
    }

    internal string SelectedProfileCaption
    {
        get => _selectedProfileCaption;
        private set => SetProperty(ref _selectedProfileCaption, value);
    }

    internal string DraftTemplateName
    {
        get => _draftTemplateName;
        set
        {
            if (SetProperty(ref _draftTemplateName, value))
            {
                TemplatesModule?.Library.NotifyDraftChanged();
            }
        }
    }

    internal string DraftTemplateNotes
    {
        get => _draftTemplateNotes;
        set
        {
            if (SetProperty(ref _draftTemplateNotes, value))
            {
                TemplatesModule?.Library.NotifyDraftChanged();
            }
        }
    }

    internal string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (SetProperty(ref _sourcePath, value))
            {
                ScheduleDraftInputRefresh();
            }
        }
    }

    internal string OutputPath
    {
        get => _outputPath;
        set
        {
            if (SetProperty(ref _outputPath, value))
            {
                if (!_isUpdatingOutputPath)
                {
                    _lastAutoOutputPath = null;
                }

                if (_isApplyingDeferredDraftInputRefresh)
                {
                    return;
                }

                ScheduleDraftInputRefresh();
            }
        }
    }

    internal ThemeOption? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            SetProperty(ref _selectedTheme, value);
        }
    }

    internal LanguageOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value))
            {
                ApplyLanguage(CurrentLanguagePreference);
            }
        }
    }

    internal EncoderOption? SelectedEncoder
    {
        get => _selectedEncoder;
        set
        {
            if (SetProperty(ref _selectedEncoder, value) && !_isSynchronizingDraft)
            {
                ApplyCapabilityDefaults();
                FinalizeDraftChange(syncOutputPath: true, markAsCustomized: true);
            }
        }
    }

    internal RateControlOption? SelectedRateControl
    {
        get => _selectedRateControl;
        set
        {
            if (SetProperty(ref _selectedRateControl, value) && !_isSynchronizingDraft)
            {
                OnPropertyChanged(nameof(IsQualityControlVisible));
                OnPropertyChanged(nameof(IsBitrateControlVisible));
                OnPropertyChanged(nameof(DraftQualityVisibility));
                OnPropertyChanged(nameof(DraftBitrateVisibility));
                OnPropertyChanged(nameof(QualityInputLabel));
                OnPropertyChanged(nameof(BitrateInputLabel));
                FinalizeDraftChange(syncOutputPath: false, markAsCustomized: true);
            }
        }
    }

    internal StringChoiceOption? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value) && !_isSynchronizingDraft)
            {
                FinalizeDraftChange(syncOutputPath: false, markAsCustomized: true);
            }
        }
    }

    internal StringChoiceOption? SelectedTune
    {
        get => _selectedTune;
        set
        {
            if (SetProperty(ref _selectedTune, value) && !_isSynchronizingDraft)
            {
                FinalizeDraftChange(syncOutputPath: false, markAsCustomized: true);
            }
        }
    }

    internal StringChoiceOption? SelectedProfileOption
    {
        get => _selectedProfileOption;
        set
        {
            if (SetProperty(ref _selectedProfileOption, value) && !_isSynchronizingDraft)
            {
                FinalizeDraftChange(syncOutputPath: false, markAsCustomized: true);
            }
        }
    }

    internal StringChoiceOption? SelectedOutputFormat
    {
        get => _selectedOutputFormat;
        set
        {
            if (SetProperty(ref _selectedOutputFormat, value) && !_isSynchronizingDraft)
            {
                FinalizeDraftChange(syncOutputPath: true, markAsCustomized: true);
            }
        }
    }

    internal StringChoiceOption? SelectedConcurrentEncodingJobOption
    {
        get => _selectedConcurrentEncodingJobOption;
        set
        {
            if (!SetProperty(ref _selectedConcurrentEncodingJobOption, value) || value is null)
            {
                return;
            }

            if (int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var concurrentJobCount))
            {
                MaxConcurrentEncodingJobs = concurrentJobCount;
            }
        }
    }

    internal StringChoiceOption? SelectedQueueCompletionActionOption
    {
        get => _selectedQueueCompletionActionOption;
        set
        {
            if (!SetProperty(ref _selectedQueueCompletionActionOption, value) || value is null)
            {
                return;
            }

            if (Enum.TryParse<QueueCompletionAction>(value.Value, ignoreCase: false, out var action))
            {
                QueueCompletionAction = action;
            }
        }
    }

    internal double DraftQuality
    {
        get => _draftQuality;
        set
        {
            var normalized = NormalizeBoundedDouble(value, 0, double.MaxValue);
            if (SetProperty(ref _draftQuality, normalized) && !_isSynchronizingDraft)
            {
                FinalizeDraftChange(syncOutputPath: false, markAsCustomized: true);
            }
        }
    }

    internal double DraftBitrate
    {
        get => _draftBitrate;
        set
        {
            var normalized = NormalizeBoundedDouble(value, 1, int.MaxValue);
            if (SetProperty(ref _draftBitrate, normalized) && !_isSynchronizingDraft)
            {
                FinalizeDraftChange(syncOutputPath: false, markAsCustomized: true);
            }
        }
    }

    internal string DraftAdditionalArguments
    {
        get => _draftAdditionalArguments;
        set
        {
            if (SetProperty(ref _draftAdditionalArguments, value) && !_isSynchronizingDraft)
            {
                ApplyManualArgumentOverrides(value);
                FinalizeDraftChange(syncOutputPath: false, markAsCustomized: true);
            }
        }
    }

    internal string DraftUhdParameters
    {
        get => _draftUhdParameters;
        set
        {
            if (SetProperty(ref _draftUhdParameters, value) && !_isSynchronizingDraft)
            {
                FinalizeDraftChange(syncOutputPath: false, markAsCustomized: true);
            }
        }
    }

    internal bool PreferSystemEncoders
    {
        get => _preferSystemEncoders;
        set
        {
            if (SetProperty(ref _preferSystemEncoders, value))
            {
                SchedulePreviewRefresh();
            }
        }
    }

    internal bool AutoCheckUpdatesOnStartup
    {
        get => _autoCheckUpdatesOnStartup;
        set
        {
            SetProperty(ref _autoCheckUpdatesOnStartup, value);
        }
    }

    internal double MaxConcurrentEncodingJobs
    {
        get => _maxConcurrentEncodingJobs;
        set
        {
            var normalized = NormalizeConcurrentEncodingJobs(value);
            if (SetProperty(ref _maxConcurrentEncodingJobs, normalized))
            {
                SyncSelectedConcurrentEncodingJobOption(normalized);
                _ = ProcessQueueAsync();
            }
        }
    }

    public QueueCompletionAction QueueCompletionAction
    {
        get => _queueCompletionAction;
        set
        {
            if (!SetProperty(ref _queueCompletionAction, value))
            {
                return;
            }

            SyncSelectedQueueCompletionActionOption(value);

            if (value == QueueCompletionAction.None)
            {
                CancelPendingQueueCompletionActionWait();
                ResetQueueCompletionActionBatch();
            }
            else if (Jobs.Any(static job => job.State == EncodingJobState.Running))
            {
                _isQueueCompletionActionArmed = true;
            }
            else if (_queueCompletionActionWaitCancellationTokenSource is not null && !HasActiveAppWork())
            {
                StatusText = Texts.QueueCompletionActionPendingIdleStatus(value, QueueCompletionActionIdleRequirement);
            }
        }
    }

    internal string WorkspaceRootPath
    {
        get => _workspaceRootPath;
        set => SetProperty(ref _workspaceRootPath, value);
    }

    internal string TemplateFilesRootPath => _appPaths.WorkspaceTemplatesRootPath;

    internal EncodingJobItemViewModel? SelectedJob
    {
        get => _selectedJob;
        private set
        {
            if (ReferenceEquals(_selectedJob, value))
            {
                return;
            }

            if (_selectedJob is not null)
            {
                _selectedJob.PropertyChanged -= SelectedJob_PropertyChanged;
            }

            if (SetProperty(ref _selectedJob, value))
            {
                if (_selectedJob is not null)
                {
                    _selectedJob.PropertyChanged += SelectedJob_PropertyChanged;
                }

                RaiseSelectedJobPropertyChanges();
            }
        }
    }

    internal string SuggestedOutputExtension => _activeProfile?.OutputContainer ?? "264";

    internal string QualityInputLabel => SelectedRateControl?.Value switch
    {
        RateControlMode.Cq => "CQ",
        RateControlMode.Qp => "QP",
        _ => "CRF"
    };

    internal string BitrateInputLabel => SelectedRateControl?.Value == RateControlMode.TwoPass
        ? Texts.Pick("目标码率 (2-Pass)", "Target Bitrate (2-Pass)")
        : Texts.Pick("目标码率", "Target Bitrate");

    internal bool IsQualityControlVisible => SelectedRateControl?.Value is RateControlMode.Crf or RateControlMode.Cq or RateControlMode.Qp;

    internal bool IsBitrateControlVisible => SelectedRateControl?.Value is RateControlMode.Abr or RateControlMode.Vbr or RateControlMode.TwoPass;

    internal bool IsX265Selected => SelectedEncoder?.Value == EncoderKind.X265;

    internal Visibility DraftQualityVisibility => IsQualityControlVisible ? Visibility.Visible : Visibility.Collapsed;

    internal Visibility DraftBitrateVisibility => IsBitrateControlVisible ? Visibility.Visible : Visibility.Collapsed;

    internal Visibility X265UhdVisibility => IsX265Selected ? Visibility.Visible : Visibility.Collapsed;

    internal string DraftConstraintWarningText => GetProfileConstraintError(_activeProfile) ?? string.Empty;

    internal Visibility DraftConstraintWarningVisibility =>
        string.IsNullOrWhiteSpace(DraftConstraintWarningText) ? Visibility.Collapsed : Visibility.Visible;

    internal string SuggestedOutputFileName
    {
        get
        {
            var preview = TryResolveDraftOutputPreview();
            var outputPath = preview.RunningOutputConflict is not null
                ? preview.BaseOutputPath
                : preview.FinalOutputPath;
            return string.IsNullOrWhiteSpace(outputPath)
                ? Texts.SuggestedOutputName
                : Path.GetFileNameWithoutExtension(outputPath);
        }
    }

    internal string DraftOutputPreviewText => _isDraftInputRefreshPending
        ? Texts.OutputPreviewUpdating
        : BuildDraftOutputPreviewText();

    internal bool CanQueueJob =>
        _activeProfile is not null
        && !string.IsNullOrWhiteSpace(SourcePath)
        && !string.IsNullOrWhiteSpace(OutputPath);

    internal string QueueSummary
    {
        get
        {
            if (Jobs.Count == 0)
            {
                return Texts.NoQueueJobs;
            }

            var running = Jobs.Count(static job => job.State == EncodingJobState.Running);
            var queued = Jobs.Count(static job => job.State == EncodingJobState.Queued);
            var completed = Jobs.Count(static job => job.State == EncodingJobState.Completed);
            var failed = Jobs.Count(static job => job.State == EncodingJobState.Failed);
            var cancelled = Jobs.Count(static job => job.State == EncodingJobState.Cancelled);

            return Texts.QueueSummary(running, queued, completed, failed, cancelled);
        }
    }

    internal bool HasJobs => Jobs.Count > 0;

    internal Visibility EmptyQueueVisibility => HasJobs ? Visibility.Collapsed : Visibility.Visible;

    internal Visibility QueueSelectionCommandBarVisibility => HasJobs ? Visibility.Visible : Visibility.Collapsed;

    internal int SelectedQueueJobCount => _selectedQueueJobs.Count;

    internal int SelectedQueuedJobCount => _selectedQueueJobs.Count(static job => job.State == EncodingJobState.Queued);

    internal int SelectedRunningJobCount => _selectedQueueJobs.Count(static job => job.State == EncodingJobState.Running);

    internal int SelectedCancelableQueueJobCount => _selectedQueueJobs.Count(static job => job.CanCancel);

    internal int SelectedRemovableQueueJobCount => _selectedQueueJobs.Count(static job => job.CanRemove);

    internal string QueueSelectionStatusText => Texts.QueueSelectionStatus(SelectedQueueJobCount, Jobs.Count);

    internal bool CanSelectAllQueueJobs => Jobs.Count > 0 && SelectedQueueJobCount < Jobs.Count;

    internal bool CanInvertQueueSelection => Jobs.Count > 0;

    internal bool CanClearQueueSelection => SelectedQueueJobCount > 0;

    internal bool CanStartSelectedJobs => _selectedQueueJobs.Any(static job => job.CanStart);

    internal bool CanCancelSelectedJobs => _selectedQueueJobs.Any(static job => job.CanCancel);

    internal bool CanDeleteSelectedJobs => _selectedQueueJobs.Any(static job => job.CanRemove);

    private bool IsQueueBatchSelectionActive => SelectedQueueJobCount > 1;

    internal double SelectedJobProgressValue => IsQueueBatchSelectionActive ? 0.0 : SelectedJob?.ProgressValue ?? 0.0;

    internal string SelectedJobProgressPrimaryText => IsQueueBatchSelectionActive
        ? Texts.QueueBatchSelectionSummary(
            SelectedQueueJobCount,
            SelectedRunningJobCount,
            SelectedQueuedJobCount,
            GetSelectedQueueJobStateCount(EncodingJobState.Completed),
            GetSelectedQueueJobStateCount(EncodingJobState.Failed),
            GetSelectedQueueJobStateCount(EncodingJobState.Cancelled))
        : SelectedJob?.ProgressTelemetryPrimaryLine ?? Texts.DefaultProgressPrimary;

    internal string SelectedJobProgressSecondaryText => IsQueueBatchSelectionActive
        ? Texts.QueueSelectionStatus(SelectedQueueJobCount, Jobs.Count)
        : SelectedJob?.ProgressTelemetrySecondaryLine ?? Texts.DefaultProgressSecondary;

    internal string SelectedJobProgressPercentText => IsQueueBatchSelectionActive
        ? Texts.QueueBatchSelectionProgressLabel(SelectedQueueJobCount)
        : SelectedJob?.ProgressPercentLabel ?? "0%";

    internal Visibility SelectedJobSourcePreparationVisibility => !IsQueueBatchSelectionActive && SelectedJob?.HasSourcePreparationText == true
        ? Visibility.Visible
        : Visibility.Collapsed;

    internal string SelectedJobSourcePreparationText => IsQueueBatchSelectionActive
        ? string.Empty
        : SelectedJob?.SourcePreparationText ?? string.Empty;

    internal string SelectedJobFramesText => IsQueueBatchSelectionActive
        ? Texts.QueueBatchSelectionQueuedMetric(SelectedQueuedJobCount)
        : BuildSelectedJobFramesText();

    internal string SelectedJobFpsText => IsQueueBatchSelectionActive
        ? Texts.QueueBatchSelectionRunningMetric(SelectedRunningJobCount)
        : SelectedJob?.FramesPerSecond is > 0
            ? $"{SelectedJob.FramesPerSecond.Value:0.00} fps"
            : "--.-- fps";

    internal string SelectedJobBitrateText => IsQueueBatchSelectionActive
        ? Texts.QueueBatchSelectionCancelableMetric(SelectedCancelableQueueJobCount)
        : SelectedJob?.BitrateKbps is > 0
            ? $"{SelectedJob.BitrateKbps.Value:0.00} kb/s"
            : "--.-- kb/s";

    internal string SelectedJobEtaText => IsQueueBatchSelectionActive
        ? Texts.QueueBatchSelectionRemovableMetric(SelectedRemovableQueueJobCount)
        : $"{Texts.EtaPrefix} {FormatSelectedJobEta(SelectedJob?.Eta)}";

    internal string SelectedJobEstimatedSizeText => IsQueueBatchSelectionActive
        ? Texts.QueueSelectionStatus(SelectedQueueJobCount, Jobs.Count)
        : $"{Texts.EstimatedSizePrefix} {FormatSelectedJobSize(SelectedJob?.EstimatedFileSizeBytes)}";

    internal string SelectedJobCommandText => IsQueueBatchSelectionActive
        ? string.Empty
        : SelectedJob?.DisplayCommand ?? string.Empty;

    internal string SelectedJobCommandPlaceholderText => IsQueueBatchSelectionActive
        ? Texts.QueueBatchSelectionCommandText
        : Texts.SelectJobForCommandText;

    internal bool CanCopySelectedJobCommand => !IsQueueBatchSelectionActive
        && SelectedJob?.IsDisplayCommandResolved == true
        && !string.IsNullOrWhiteSpace(SelectedJob.DisplayCommand);

    internal string SelectedJobLogText => IsQueueBatchSelectionActive
        ? Texts.QueueBatchSelectionLogText
        : SelectedJob is null
            ? Texts.SelectJobForLogText
            : string.IsNullOrWhiteSpace(SelectedJob.Log)
                ? Texts.NoSelectedJobLogText
                : SelectedJob.Log;

    internal string SelectedJobSummary => IsQueueBatchSelectionActive
        ? Texts.QueueBatchSelectionSummary(
            SelectedQueueJobCount,
            SelectedRunningJobCount,
            SelectedQueuedJobCount,
            GetSelectedQueueJobStateCount(EncodingJobState.Completed),
            GetSelectedQueueJobStateCount(EncodingJobState.Failed),
            GetSelectedQueueJobStateCount(EncodingJobState.Cancelled))
        : SelectedJob is null
            ? Texts.SelectedJobSummaryPlaceholder
            : Texts.QueueSelectionSummary(SelectedJob.StateLabel, SelectedJob.Summary);

    internal AppThemePreference CurrentThemePreference => SelectedTheme?.Value ?? AppThemePreference.Default;

    internal AppLanguage CurrentLanguagePreference => SelectedLanguage?.Value ?? AppLanguage.Chinese;

    public async Task InitializeAsync()
    {
        LoadSettings();
        var restoredSetupGuideSnapshot = SetupGuideModule.TryRestoreCachedSnapshot();
        await RefreshAsync(
            Texts.InitializationStatus,
            includeUpdates: AutoCheckUpdatesOnStartup,
            refreshEnvironmentReadiness: !restoredSetupGuideSnapshot);

        RunInitialVsPluginDependencyUpdateIfNeeded();

        if (!_hasCompletedSetupGuide)
        {
            await SetupGuideModule.RefreshSetupGuideAsync(openWhenFinished: false);
            SetupGuideModule.OpenSetupGuide();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancelPendingQueueCompletionActionWait();
        CancelPendingPreviewRefresh();
        CancelPendingDraftInputRefresh();
        CancelPendingAutoCompressionInputRefresh();
        CancelAutoCompression();
        DisposeAutoCompressionCancellation();
        DisposeAudioProcessingState();
        DisposeBluRayDemuxState();
        DisposeModuleViewModels();

        if (_selectedJob is not null)
        {
            _selectedJob.PropertyChanged -= SelectedJob_PropertyChanged;
        }
    }

    public async Task RefreshAsync(
        string? statusOverride = null,
        bool includeUpdates = false,
        bool refreshEnvironmentReadiness = true)
    {
        if (_isRefreshingCatalog)
        {
            return;
        }

        _isRefreshingCatalog = true;
        OnPropertyChanged(nameof(IsBusy));

        try
        {
            var encoderCatalogTask = _toolchainService.GetCatalogAsync();
            var userTemplatesTask = _profileLibraryService.GetUserTemplatesAsync();
            var environmentReadinessTask = refreshEnvironmentReadiness || _environmentReadinessReport is null
                ? _environmentReadinessService.CheckAsync()
                : null;

            if (environmentReadinessTask is not null)
            {
                await Task.WhenAll(
                    encoderCatalogTask,
                    userTemplatesTask,
                    environmentReadinessTask);
            }
            else
            {
                await Task.WhenAll(
                    encoderCatalogTask,
                    userTemplatesTask);
            }

            var encoderCatalog = await encoderCatalogTask;
            var userTemplates = await userTemplatesTask;

            ReplaceItems(Encoders, encoderCatalog);
            TemplatesModule.Library.ApplyLoadedTemplates(userTemplates);
            RefreshEncoderOptions();

            if (environmentReadinessTask is not null)
            {
                ApplyEnvironmentReadiness(await environmentReadinessTask);
            }

            await RefreshSystemBinariesAsync();

            RaiseSummaryPropertyChanges();

            if (_activeProfile is null)
            {
                BeginNewTemplateDraft();
            }
            else
            {
                ApplyProfileToDraft(_activeProfile, SelectedProfileCaption, DraftTemplateName, DraftTemplateNotes);
                await RefreshPreviewNowAsync(_activeProfile);
            }

            if (includeUpdates)
            {
                await RefreshAvailableUpdatesAsync(reportStatus: false);
            }

            StatusText = statusOverride ?? Texts.RefreshCompletedStatus(DateTime.Now);
        }
        catch (Exception ex)
        {
            StatusText = Texts.RefreshFailedStatus(ex.Message);
        }
        finally
        {
            _isRefreshingCatalog = false;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    internal async Task<AppUpdateCheckResult?> RefreshAvailableUpdatesAsync(bool reportStatus = true)
    {
        if (_isCheckingUpdates)
        {
            return null;
        }

        _isCheckingUpdates = true;
        _lastAppUpdateErrorMessage = null;
        RaiseAppUpdatePropertyChanges();

        try
        {
            var result = await _appUpdateService.CheckForUpdatesAsync();
            _lastAppUpdateResult = result;
            _lastAppUpdateErrorMessage = null;
            RaiseAppUpdatePropertyChanges();

            if (reportStatus)
            {
                StatusText = AppUpdateStatusText;
            }

            return result;
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"App update check failed. {ex.GetType().Name}: {ex.Message}");
            _lastAppUpdateErrorMessage = Texts.UpdatesCheckFailedStatus(ex.Message);
            RaiseAppUpdatePropertyChanges();
            if (reportStatus)
            {
                StatusText = AppUpdateStatusText;
            }

            return null;
        }
        finally
        {
            _isCheckingUpdates = false;
            RaiseAppUpdatePropertyChanges();
        }
    }

    internal async Task<string?> DownloadLatestAppInstallerAsync(bool reportStatus = true)
    {
        if (_isCheckingUpdates || _isDownloadingAppUpdateInstaller)
        {
            return null;
        }

        if (_lastAppUpdateResult is not { UpdateAvailable: true })
        {
            return null;
        }

        _isDownloadingAppUpdateInstaller = true;
        _appUpdateDownloadProgressPercent = null;
        _lastAppUpdateErrorMessage = null;
        RaiseAppUpdatePropertyChanges();

        if (reportStatus)
        {
            StatusText = Texts.AppUpdateDownloadingStatus(_lastAppUpdateResult.LatestVersion, _appUpdateDownloadProgressPercent);
        }

        try
        {
            var progress = new Progress<double>(value =>
            {
                var percent = (int)Math.Round(Math.Clamp(value, 0.0, 1.0) * 100.0);
                if (_appUpdateDownloadProgressPercent == percent)
                {
                    return;
                }

                _appUpdateDownloadProgressPercent = percent;
                RaiseAppUpdatePropertyChanges();

                if (reportStatus)
                {
                    StatusText = AppUpdateStatusText;
                }
            });

            var installerPath = await _appUpdateService.DownloadInstallerAsync(_lastAppUpdateResult, progress);
            _lastAppUpdateErrorMessage = null;
            RaiseAppUpdatePropertyChanges();

            if (reportStatus)
            {
                StatusText = Texts.AppUpdateInstallerReadyStatus;
            }

            return installerPath;
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"App update installer download failed for version '{_lastAppUpdateResult?.LatestVersion ?? "unknown"}'. {ex.GetType().Name}: {ex.Message}");
            _lastAppUpdateErrorMessage = Texts.AppUpdateDownloadFailedStatus(ex.Message);
            RaiseAppUpdatePropertyChanges();
            if (reportStatus)
            {
                StatusText = AppUpdateStatusText;
            }

            return null;
        }
        finally
        {
            _isDownloadingAppUpdateInstaller = false;
            _appUpdateDownloadProgressPercent = null;
            RaiseAppUpdatePropertyChanges();
        }
    }

    internal string? SaveSettings(bool updateStatusText = true)
    {
        try
        {
            var workspaceRootPathToSave = string.IsNullOrWhiteSpace(WorkspaceRootPath)
                ? _appPaths.RootPath
                : WorkspaceRootPath;
            var workspaceRootChanged = !string.Equals(
                workspaceRootPathToSave,
                _savedWorkspaceRootPath,
                StringComparison.OrdinalIgnoreCase);
            var currentSettings = _settingsService.Load();
            var settings = RequestValidation.NormalizeAppSettings(new AppSettings(
                PreferSystemEncoders,
                AutoCheckUpdatesOnStartup,
                CurrentThemePreference,
                CurrentLanguagePreference,
                _hasCompletedSetupGuide,
                workspaceRootPathToSave,
                new Dictionary<string, string>(_manualToolPaths, StringComparer.OrdinalIgnoreCase),
                _hasRunInitialVsPluginDependencyUpdate,
                GetMaxConcurrentEncodingJobCount(),
                QueueCompletionAction,
                currentSettings.PreviewScalingAlgorithm,
                currentSettings.LastFileDialogDirectory));

            _settingsService.Save(settings);
            _encoderDiscoveryService.InvalidateCache();
            _toolProbeService.InvalidateCache();
            _savedWorkspaceRootPath = workspaceRootPathToSave;
            WorkspaceRootPath = workspaceRootPathToSave;
            if (updateStatusText)
            {
                StatusText = workspaceRootChanged
                    && !string.Equals(workspaceRootPathToSave, _appPaths.RootPath, StringComparison.OrdinalIgnoreCase)
                    ? Texts.WorkspaceDirectorySavedStatus
                    : Texts.SettingsSavedStatus;
            }

            return null;
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"SaveSettings failed. {ex.GetType().Name}: {ex.Message}");
            return ex.Message;
        }
    }

    internal void SelectJob(EncodingJobItemViewModel? job)
    {
        SelectedJob = job;
    }

    internal void UpdateSelectedQueueJobs(IEnumerable<EncodingJobItemViewModel> selectedJobs)
    {
        var normalizedSelection = NormalizeSelectedQueueJobs(selectedJobs).ToList();
        if (_selectedQueueJobs.Count == normalizedSelection.Count
            && _selectedQueueJobs.SequenceEqual(normalizedSelection))
        {
            return;
        }

        _selectedQueueJobs.Clear();
        _selectedQueueJobs.AddRange(normalizedSelection);
        RaiseQueueSelectionPropertyChanges();
    }

    public void BeginNewTemplateDraft()
    {
        var targetKind = SelectedEncoder?.Value ?? _activeProfile?.Kind ?? EncoderKind.X264;

        _isSynchronizingDraft = true;

        try
        {
            SelectedEncoder = EncoderOptions.FirstOrDefault(option => option.Value == targetKind)
                ?? EncoderOptions.FirstOrDefault();
            ApplyCapabilityDefaults();
            DraftTemplateName = string.Empty;
            DraftTemplateNotes = string.Empty;
        }
        finally
        {
            _isSynchronizingDraft = false;
        }

        FinalizeDraftChange(syncOutputPath: false, markAsCustomized: false);
        SelectedProfileCaption = Texts.NewTemplateCaption;
        TemplatesModule.Library.CaptureTemplateEditingBaseline(
            null,
            null,
            DraftTemplateName,
            DraftTemplateNotes,
            _activeProfile);
    }

    internal Task<string?> QueueCurrentJobAsync(bool startImmediately = false, QueueJobPreflightResult? preflight = null)
    {
        try
        {
            var hasRunningJob = GetRunningEncodingJobCount() >= GetMaxConcurrentEncodingJobCount();
            preflight ??= AnalyzeCurrentJobForQueue();
            if (!string.IsNullOrWhiteSpace(preflight.ValidationError))
            {
                return Task.FromResult<string?>(preflight.ValidationError);
            }

            if (preflight.RunningOutputConflict is not null)
            {
                return Task.FromResult<string?>(Texts.QueueOutputPathRunningConflictMessage(preflight.RunningOutputConflict.SourceFileName, preflight.BaseOutputPath));
            }

            var request = CreateDraftRequest(finalOutputPathOverride: preflight.FinalOutputPath);
            var job = new EncodingJobItemViewModel(
                request,
                Texts.Pick("正在生成实际执行命令...", "Resolving the actual command..."),
                CurrentLanguagePreference);

            Jobs.Add(job);
            SelectedJob = job;
            RaiseJobSummaryPropertyChanges();

            StatusText = preflight.IsOutputPathAutoRenamed
                ? Texts.JobQueuedWithAutoOutputNameStatus(
                    Path.GetFileName(request.SourcePath),
                    Path.GetFileName(request.OutputPath),
                    startImmediately,
                    hasRunningJob,
                    preflight.QueuedOutputConflictCount,
                    preflight.DiskOutputPathExists)
                : Texts.JobQueuedStatus(Path.GetFileName(request.SourcePath), startImmediately, hasRunningJob);
            _ = ResolveJobDisplayCommandAsync(job, request);

            if (startImmediately)
            {
                _ = ProcessQueueAfterUiRefreshAsync();
            }

            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>(ex.Message);
        }
    }

    public string? ValidateCurrentJobForQueue(out string? existingOutputPath)
    {
        existingOutputPath = null;

        try
        {
            var preflight = AnalyzeCurrentJobForQueue();
            if (!string.IsNullOrWhiteSpace(preflight.ValidationError))
            {
                return preflight.ValidationError;
            }

            if (preflight.RunningOutputConflict is not null)
            {
                return Texts.QueueOutputPathRunningConflictMessage(preflight.RunningOutputConflict.SourceFileName, preflight.BaseOutputPath);
            }

            existingOutputPath = preflight.DiskOutputPathExists ? preflight.BaseOutputPath : null;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    internal QueueJobPreflightResult AnalyzeCurrentJobForQueue(bool requireSourceExists = true)
    {
        try
        {
            var baseRequest = CreateDraftRequest(requireSourceExists: requireSourceExists, uniquifyOutputPath: false);
            var finalOutputPath = ResolveUniqueOutputPath(baseRequest.OutputPath);
            var duplicateJob = Jobs.FirstOrDefault(job => IsDuplicateJobRequest(job.Request, baseRequest));
            var runningOutputConflict = Jobs.FirstOrDefault(job =>
                job.State == EncodingJobState.Running
                && AreSamePath(job.Request.OutputPath, baseRequest.OutputPath));
            var queuedOutputConflictCount = Jobs.Count(job =>
                job.State != EncodingJobState.Running
                && AreSamePath(job.Request.OutputPath, baseRequest.OutputPath));
            var diskOutputPathExists = File.Exists(baseRequest.OutputPath) || Directory.Exists(baseRequest.OutputPath);

            return new QueueJobPreflightResult(
                baseRequest.OutputPath,
                finalOutputPath,
                duplicateJob,
                runningOutputConflict,
                queuedOutputConflictCount,
                diskOutputPathExists,
                ValidationError: null);
        }
        catch (Exception ex)
        {
            return new QueueJobPreflightResult(
                string.Empty,
                string.Empty,
                DuplicateJob: null,
                RunningOutputConflict: null,
                QueuedOutputConflictCount: 0,
                DiskOutputPathExists: false,
                ex.Message);
        }
    }

    internal Task CancelJobAsync(EncodingJobItemViewModel? job)
    {
        if (job is null)
        {
            return Task.CompletedTask;
        }

        if (job.State == EncodingJobState.Queued)
        {
            job.MarkCancelled(
                Texts.Pick("队列中的作业已取消", "Queued job cancelled"),
                Texts.Pick("作业尚未启动，已从执行队列中撤回。", "The job had not started and was removed from the execution queue."));
            RaiseJobStatePropertyChanges();
            StatusText = Texts.QueuedJobCancelledStatus(job.SourceFileName);
        }
        else if (job.State == EncodingJobState.Running)
        {
            job.RequestCancellation();
            _jobRunner.AbortJob(job.Request.JobId);
            StatusText = Texts.RunningJobCancellingStatus(job.SourceFileName);
        }

        return Task.CompletedTask;
    }

    internal string? StartSelectedJobsNow()
    {
        var selectedJobs = NormalizeSelectedQueueJobs(_selectedQueueJobs).ToList();
        if (selectedJobs.Count == 0)
        {
            return Texts.NoSelectedJobsError;
        }

        var startableJobs = selectedJobs
            .Where(static job => job.CanStart)
            .ToList();
        if (startableJobs.Count == 0)
        {
            return Texts.BatchStartNoQueuedJobsError;
        }

        MoveQueuedJobsToFront(startableJobs);

        var limit = GetMaxConcurrentEncodingJobCount();
        var availableSlots = Math.Max(0, limit - GetRunningEncodingJobCount());
        if (availableSlots == 0)
        {
            StatusText = Texts.BatchJobsPrioritizedStatus(startableJobs.Count, limit);
            RaiseJobStatePropertyChanges();
            return null;
        }

        var startedCount = 0;
        foreach (var job in startableJobs)
        {
            if (startedCount >= availableSlots)
            {
                break;
            }

            SelectedJob = job;
            _ = RunJobAsync(job);
            startedCount++;
        }

        StatusText = startedCount == startableJobs.Count
            ? Texts.BatchJobsStartedStatus(startedCount)
            : Texts.BatchJobsStartedPartialStatus(startedCount, startableJobs.Count, limit);
        RaiseJobStatePropertyChanges();
        return null;
    }

    internal string? CancelSelectedJobs()
    {
        var selectedJobs = NormalizeSelectedQueueJobs(_selectedQueueJobs).ToList();
        if (selectedJobs.Count == 0)
        {
            return Texts.NoSelectedJobsError;
        }

        var cancelableJobs = selectedJobs
            .Where(static job => job.CanCancel)
            .ToList();
        if (cancelableJobs.Count == 0)
        {
            return Texts.BatchCancelNoCancelableJobsError;
        }

        var queuedCount = 0;
        var runningCount = 0;
        foreach (var job in cancelableJobs)
        {
            if (job.State == EncodingJobState.Queued)
            {
                queuedCount++;
            }
            else if (job.State == EncodingJobState.Running)
            {
                runningCount++;
            }

            _ = CancelJobAsync(job);
        }

        StatusText = Texts.BatchJobsCancelRequestedStatus(cancelableJobs.Count, runningCount, queuedCount);
        RaiseJobStatePropertyChanges();
        return null;
    }

    internal string? RemoveSelectedJobs()
    {
        var selectedJobs = NormalizeSelectedQueueJobs(_selectedQueueJobs).ToList();
        if (selectedJobs.Count == 0)
        {
            return Texts.NoSelectedJobsError;
        }

        var removableJobs = selectedJobs
            .Where(static job => job.CanRemove)
            .ToList();
        if (removableJobs.Count == 0)
        {
            return Texts.BatchDeleteNoRemovableJobsError;
        }

        var removedCount = 0;
        foreach (var job in removableJobs)
        {
            if (Jobs.Remove(job))
            {
                removedCount++;
            }
        }

        PruneSelectedQueueJobs();
        if (SelectedJob is not null && !Jobs.Contains(SelectedJob))
        {
            SelectedJob = _selectedQueueJobs.FirstOrDefault()
                ?? Jobs.FirstOrDefault();
        }

        var skippedRunningCount = selectedJobs.Count - removableJobs.Count;
        RaiseJobSummaryPropertyChanges();
        StatusText = Texts.BatchJobsDeletedStatus(removedCount, skippedRunningCount);
        return null;
    }

    internal Task<string?> RestartJobAsync(EncodingJobItemViewModel? job)
    {
        if (job is null)
        {
            return Task.FromResult<string?>(Texts.Pick("未找到要重启的任务。", "The job to restart was not found."));
        }

        if (!job.CanRestart)
        {
            return Task.FromResult<string?>(Texts.Pick("只有已完成、失败或已取消的任务才能重启。", "Only completed, failed, or cancelled jobs can be restarted."));
        }

        try
        {
            var request = job.Request with { JobId = Guid.NewGuid() };
            EnsureRequestConstraintsSatisfied(request);
            var restartedJob = new EncodingJobItemViewModel(
                request,
                Texts.Pick("正在生成实际执行命令...", "Resolving the actual command..."),
                CurrentLanguagePreference);

            Jobs.Add(restartedJob);
            SelectedJob = restartedJob;
            RaiseJobSummaryPropertyChanges();

            StatusText = Texts.JobRestartedStatus(restartedJob.SourceFileName);
            _ = ResolveJobDisplayCommandAsync(restartedJob, request);
            _ = ProcessQueueAfterUiRefreshAsync();
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>(ex.Message);
        }
    }

    internal string? RemoveJob(EncodingJobItemViewModel? job)
    {
        if (job is null)
        {
            return Texts.RemoveJobMissingError;
        }

        if (!job.CanRemove)
        {
            return Texts.RemoveRunningJobError;
        }

        if (!Jobs.Remove(job))
        {
            return Texts.RemoveJobFailedError;
        }

        if (ReferenceEquals(SelectedJob, job))
        {
            SelectedJob = Jobs.FirstOrDefault();
        }

        PruneSelectedQueueJobs();
        RaiseJobSummaryPropertyChanges();
        StatusText = Texts.JobDeletedStatus(job.SourceFileName);
        return null;
    }

    internal string? PrioritizeJob(EncodingJobItemViewModel? job)
    {
        var error = MoveQueuedJob(job, MoveQueuedJobMode.Next);
        if (string.IsNullOrWhiteSpace(error))
        {
            _ = ProcessQueueAsync();
        }

        return error;
    }

    internal string? StartJobNow(EncodingJobItemViewModel? job)
    {
        if (job is null)
        {
            return Texts.StartJobMissingError;
        }

        if (!job.CanStart)
        {
            return Texts.StartJobInvalidError;
        }

        if (GetRunningEncodingJobCount() >= GetMaxConcurrentEncodingJobCount())
        {
            return Texts.ConcurrentEncodingLimitReached(GetMaxConcurrentEncodingJobCount());
        }

        SelectedJob = job;
        StatusText = Texts.JobStartedManuallyStatus(job.SourceFileName);
        _ = RunJobAsync(job);
        return null;
    }

    internal string? MoveJobUp(EncodingJobItemViewModel? job)
    {
        return MoveQueuedJob(job, MoveQueuedJobMode.Up);
    }

    internal string? MoveJobDown(EncodingJobItemViewModel? job)
    {
        return MoveQueuedJob(job, MoveQueuedJobMode.Down);
    }

    internal string? MoveJobToTop(EncodingJobItemViewModel? job)
    {
        return MoveQueuedJob(job, MoveQueuedJobMode.Top);
    }

    internal string? MoveJobToBottom(EncodingJobItemViewModel? job)
    {
        return MoveQueuedJob(job, MoveQueuedJobMode.Bottom);
    }

    private Task ProcessQueueAsync()
    {
        if (_isQueueProcessing || _isShuttingDown)
        {
            return Task.CompletedTask;
        }

        _isQueueProcessing = true;

        try
        {
            while (true)
            {
                if (_isShuttingDown)
                {
                    break;
                }

                if (GetRunningEncodingJobCount() >= GetMaxConcurrentEncodingJobCount())
                {
                    break;
                }

                var nextJob = Jobs.FirstOrDefault(static job => job.State == EncodingJobState.Queued);
                if (nextJob is null)
                {
                    break;
                }

                _ = RunJobAsync(nextJob);
            }
        }
        finally
        {
            _isQueueProcessing = false;
        }

        return Task.CompletedTask;
    }

    private async Task ProcessQueueAfterUiRefreshAsync()
    {
        await Task.Yield();
        await Task.Delay(50);
        await ProcessQueueAsync();
    }

    private int GetRunningEncodingJobCount()
    {
        return Jobs.Count(static job => job.State == EncodingJobState.Running);
    }

    private int GetMaxConcurrentEncodingJobCount()
    {
        return NormalizeConcurrentEncodingJobs(MaxConcurrentEncodingJobs);
    }

    private static int NormalizeConcurrentEncodingJobs(double value)
    {
        return RequestValidation.NormalizeConcurrentEncodingJobs(value);
    }

    private void SyncSelectedConcurrentEncodingJobOption(int normalizedValue)
    {
        var targetValue = normalizedValue.ToString(CultureInfo.InvariantCulture);
        var matchedOption = ConcurrentEncodingJobOptions.FirstOrDefault(option =>
            string.Equals(option.Value, targetValue, StringComparison.Ordinal));

        if (ReferenceEquals(_selectedConcurrentEncodingJobOption, matchedOption))
        {
            return;
        }

        _selectedConcurrentEncodingJobOption = matchedOption;
        OnPropertyChanged(nameof(SelectedConcurrentEncodingJobOption));
    }

    private void SyncSelectedQueueCompletionActionOption(QueueCompletionAction action)
    {
        var targetValue = action.ToString();
        var matchedOption = QueueCompletionActionOptions.FirstOrDefault(option =>
            string.Equals(option.Value, targetValue, StringComparison.Ordinal));

        if (ReferenceEquals(_selectedQueueCompletionActionOption, matchedOption))
        {
            return;
        }

        _selectedQueueCompletionActionOption = matchedOption;
        OnPropertyChanged(nameof(SelectedQueueCompletionActionOption));
    }

    private static bool IsPendingQueueWork(EncodingJobItemViewModel job)
    {
        return job.State is EncodingJobState.Queued or EncodingJobState.Running;
    }

    private bool HasPendingQueueWork()
    {
        return Jobs.Any(IsPendingQueueWork);
    }

    internal bool HasActiveAppWork()
    {
        return HasPendingQueueWork()
            || IsAutoCompressionRunning
            || IsAudioProcessingRunning
            || IsBluRayDemuxRunning;
    }

    private void ResetQueueCompletionActionBatch()
    {
        _isQueueCompletionActionArmed = false;
        _queueCompletionActionBatchHadNonSuccessfulCompletion = false;
    }

    private void BeginQueueCompletionActionBatch()
    {
        CancelPendingQueueCompletionActionWait();
        _isQueueCompletionActionArmed = QueueCompletionAction != QueueCompletionAction.None;
        _queueCompletionActionBatchHadNonSuccessfulCompletion = false;
    }

    private void MarkQueueCompletionActionBatchNonSuccessful()
    {
        if (!_isQueueCompletionActionArmed)
        {
            return;
        }

        _queueCompletionActionBatchHadNonSuccessfulCompletion = true;
        CancelPendingQueueCompletionActionWait();
    }

    private bool ShouldTreatNonSuccessfulJobAsUnattendedFailure()
    {
        return _isQueueCompletionActionArmed
            && _systemIdleService.GetIdleDuration() >= QueueCompletionActionIdleRequirement;
    }

    private void CancelPendingQueueCompletionActionWait()
    {
        _queueCompletionActionWaitCancellationTokenSource?.Cancel();
        _queueCompletionActionWaitCancellationTokenSource?.Dispose();
        _queueCompletionActionWaitCancellationTokenSource = null;
    }

    private void TryScheduleQueueCompletionActionAfterSuccessfulQueueDrain()
    {
        if (_isShuttingDown || _isExecutingQueueCompletionAction || !_isQueueCompletionActionArmed)
        {
            return;
        }

        if (_queueCompletionActionBatchHadNonSuccessfulCompletion)
        {
            ResetQueueCompletionActionBatch();
            return;
        }

        if (HasActiveAppWork())
        {
            return;
        }

        var action = QueueCompletionAction;
        if (action == QueueCompletionAction.None)
        {
            ResetQueueCompletionActionBatch();
            return;
        }

        CancelPendingQueueCompletionActionWait();
        var cancellationTokenSource = new CancellationTokenSource();
        _queueCompletionActionWaitCancellationTokenSource = cancellationTokenSource;
        ResetQueueCompletionActionBatch();
        StatusText = Texts.QueueCompletionActionPendingIdleStatus(action, QueueCompletionActionIdleRequirement);
        _ = WaitForQueueCompletionActionIdleAndExecuteAsync(cancellationTokenSource);
    }

    private async Task WaitForQueueCompletionActionIdleAndExecuteAsync(CancellationTokenSource cancellationTokenSource)
    {
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_isShuttingDown || _isExecutingQueueCompletionAction || HasActiveAppWork())
                {
                    return;
                }

                var action = QueueCompletionAction;
                if (action == QueueCompletionAction.None)
                {
                    return;
                }

                if (_systemIdleService.GetIdleDuration() >= QueueCompletionActionIdleRequirement)
                {
                    await ExecuteQueueCompletionActionAsync(action);
                    return;
                }

                await Task.Delay(QueueCompletionActionIdlePollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_queueCompletionActionWaitCancellationTokenSource, cancellationTokenSource))
            {
                _queueCompletionActionWaitCancellationTokenSource = null;
            }

            cancellationTokenSource.Dispose();
        }
    }

    private async Task ExecuteQueueCompletionActionAsync(QueueCompletionAction action)
    {
        _isExecutingQueueCompletionAction = true;
        try
        {
            StatusText = Texts.QueueCompletionActionExecutingStatus(action);
            var error = await _queueCompletionActionService.ExecuteAsync(action);
            if (!string.IsNullOrWhiteSpace(error))
            {
                StatusText = Texts.QueueCompletionActionFailedStatus(action, error);
            }
        }
        finally
        {
            _isExecutingQueueCompletionAction = false;
        }
    }

    private async Task RunJobAsync(EncodingJobItemViewModel job)
    {
        if (job.State != EncodingJobState.Queued || _isShuttingDown)
        {
            return;
        }

        using var cancellationSource = new CancellationTokenSource();
        if (QueueCompletionAction != QueueCompletionAction.None && GetRunningEncodingJobCount() == 0)
        {
            BeginQueueCompletionActionBatch();
        }

        job.AttachCancellation(cancellationSource);
        job.MarkRunning();
        RaiseJobStatePropertyChanges();

        try
        {
            StatusText = Texts.EncodingStartedStatus(job.SourceFileName);
            if (SelectedJob is null)
            {
                SelectedJob = job;
            }

            var progress = new Progress<EncodingJobProgress>(update =>
            {
                var previousState = job.State;
                job.ApplyProgress(update);
                RaiseDashboardCardActivityPropertyChanges();
                if (previousState != job.State)
                {
                    RaiseJobStatePropertyChanges();
                }
            });

            var result = await Task.Run(
                () => _jobRunner.RunAsync(job.Request, progress, cancellationSource.Token),
                cancellationSource.Token);
            var previousState = job.State;
            job.ApplyResult(result);
            RaiseDashboardCardActivityPropertyChanges();
            if (previousState != job.State)
            {
                RaiseJobStatePropertyChanges();
            }

            if (result.State is EncodingJobState.Failed or EncodingJobState.Cancelled)
            {
                if (ShouldTreatNonSuccessfulJobAsUnattendedFailure())
                {
                    MarkQueueCompletionActionBatchNonSuccessful();
                }
            }

            StatusText = Texts.EncodingFinishedStatus(job.SourceFileName, result.Summary);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            if (ShouldTreatNonSuccessfulJobAsUnattendedFailure())
            {
                MarkQueueCompletionActionBatchNonSuccessful();
            }

            job.MarkCancelled(
                Texts.Pick("编码已取消", "Encoding cancelled"),
                Texts.Pick("作业被用户中断。", "The job was cancelled by the user."));
            RaiseJobStatePropertyChanges();
            StatusText = Texts.EncodingCancelledStatus(job.SourceFileName);
        }
        catch (Exception ex)
        {
            if (ShouldTreatNonSuccessfulJobAsUnattendedFailure())
            {
                MarkQueueCompletionActionBatchNonSuccessful();
            }

            job.MarkFailed(Texts.Pick($"编码失败：{ex.Message}", $"Encoding failed: {ex.Message}"), ex.ToString());
            RaiseJobStatePropertyChanges();
            StatusText = Texts.EncodingFailedStatus(job.SourceFileName);
        }
        finally
        {
            job.DetachCancellation();
            _ = ProcessQueueAsync();
            TryScheduleQueueCompletionActionAfterSuccessfulQueueDrain();
        }
    }

    public async Task CancelRunningJobsForShutdownAsync()
    {
        _isShuttingDown = true;
        CancelPendingQueueCompletionActionWait();
        ResetQueueCompletionActionBatch();
        CancelAutoCompression();
        CancelAudioProcessing();
        CancelBluRayDemux();

        var runningJobs = Jobs
            .Where(static job => job.State == EncodingJobState.Running)
            .ToList();

        if (runningJobs.Count == 0 && !IsAutoCompressionRunning && !IsAudioProcessingRunning && !IsBluRayDemuxRunning)
        {
            return;
        }

        StatusText = Texts.ShuttingDownStatus(runningJobs.Count, IsAutoCompressionRunning, IsAudioProcessingRunning, IsBluRayDemuxRunning);

        foreach (var job in runningJobs)
        {
            job.RequestCancellation();
            _jobRunner.AbortJob(job.Request.JobId);
        }

        var timeoutAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while ((runningJobs.Any(static job => job.State == EncodingJobState.Running)
                || IsAutoCompressionRunning
                || IsAudioProcessingRunning
                || IsBluRayDemuxRunning)
               && DateTimeOffset.UtcNow < timeoutAt)
        {
            await Task.Delay(100);
        }
    }

    private string? MoveQueuedJob(EncodingJobItemViewModel? job, MoveQueuedJobMode mode)
    {
        if (job is null)
        {
            return Texts.MoveJobMissingError;
        }

        if (job.State != EncodingJobState.Queued)
        {
            return Texts.MoveJobInvalidError;
        }

        var currentIndex = Jobs.IndexOf(job);
        if (currentIndex < 0)
        {
            return Texts.MoveJobNotInQueueError;
        }

        var minimumIndex = GetQueuedMoveFloorIndex();
        var maximumIndex = Jobs.Count - 1;
        var targetIndex = mode switch
        {
            MoveQueuedJobMode.Next or MoveQueuedJobMode.Top => minimumIndex,
            MoveQueuedJobMode.Up => Math.Max(minimumIndex, currentIndex - 1),
            MoveQueuedJobMode.Down => Math.Min(maximumIndex, currentIndex + 1),
            MoveQueuedJobMode.Bottom => maximumIndex,
            _ => currentIndex
        };

        if (targetIndex == currentIndex)
        {
            StatusText = Texts.MoveJobEdgeStatus(mode, job.SourceFileName);

            return null;
        }

        Jobs.Move(currentIndex, targetIndex);
        SelectedJob = job;
        RaiseJobSummaryPropertyChanges();

        StatusText = Texts.MoveJobCompletedStatus(mode, job.SourceFileName);

        return null;
    }

    private void MoveQueuedJobsToFront(IReadOnlyList<EncodingJobItemViewModel> jobs)
    {
        var insertionIndex = GetQueuedMoveFloorIndex();
        foreach (var job in jobs)
        {
            if (job.State != EncodingJobState.Queued)
            {
                continue;
            }

            var currentIndex = Jobs.IndexOf(job);
            if (currentIndex < 0)
            {
                continue;
            }

            if (currentIndex != insertionIndex)
            {
                Jobs.Move(currentIndex, insertionIndex);
            }

            insertionIndex++;
        }
    }

    private int GetQueuedMoveFloorIndex()
    {
        var runningIndex = Jobs
            .Select(static (job, index) => job.State == EncodingJobState.Running ? (int?)index : null)
            .Max();

        return runningIndex.HasValue ? runningIndex.Value + 1 : 0;
    }

    internal void CorrectQueueOrderAfterDrop()
    {
        var floorIndex = GetQueuedMoveFloorIndex();
        for (var i = 0; i < floorIndex; i++)
        {
            if (Jobs[i].State == EncodingJobState.Queued)
            {
                Jobs.Move(i, floorIndex);
                break;
            }
        }

        RefreshJobPositionFlags();
        RaiseJobSummaryPropertyChanges();
    }

    private EncodingJobRequest CreateDraftRequest(
        bool requireSourceExists = true,
        bool uniquifyOutputPath = true,
        string? finalOutputPathOverride = null)
    {
        if (_activeProfile is null)
        {
            throw new InvalidOperationException(Texts.MissingEncoderError);
        }

        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            throw new InvalidOperationException(Texts.MissingSourceError);
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            throw new InvalidOperationException(Texts.MissingOutputError);
        }

        var normalizedSource = Path.GetFullPath(SourcePath.Trim());
        var normalizedOutputDirectory = Path.GetFullPath(OutputPath.Trim());

        if (requireSourceExists && !File.Exists(normalizedSource))
        {
            throw new FileNotFoundException(Texts.SourceFileMissingError, normalizedSource);
        }

        if (requireSourceExists && File.Exists(normalizedOutputDirectory))
        {
            throw new InvalidOperationException(Texts.OutputDirectoryInvalidError);
        }

        var normalizedOutput = string.IsNullOrWhiteSpace(finalOutputPathOverride)
            ? ResolveDraftOutputPath(
                normalizedSource,
                normalizedOutputDirectory,
                _activeProfile)
            : Path.GetFullPath(finalOutputPathOverride.Trim());
        if (uniquifyOutputPath && string.IsNullOrWhiteSpace(finalOutputPathOverride))
        {
            normalizedOutput = ResolveUniqueOutputPath(normalizedOutput);
        }

        if (string.Equals(normalizedSource, normalizedOutput, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Texts.SourceOutputPathConflictError);
        }

        var request = new EncodingJobRequest(
            Guid.NewGuid(),
            _activeProfile,
            normalizedSource,
            normalizedOutput,
            InputSourceSupport.ResolvePipelineKind(normalizedSource),
            EncoderArchitecture.X64);

        RequestValidation.ValidateEncodingJobRequest(request);
        EnsureRequestConstraintsSatisfied(request);
        return request;
    }

    private static string ResolveDraftOutputPath(string sourcePath, string outputDirectory, EncodingProfile? profile)
    {
        return EncodingOutputPathPlanner.BuildDefaultOutputPath(sourcePath, outputDirectory, profile);
    }

    private string ResolveUniqueOutputPath(string outputPath)
    {
        var normalizedOutputPath = Path.GetFullPath(outputPath.Trim());
        if (!IsOutputPathOccupied(normalizedOutputPath))
        {
            return normalizedOutputPath;
        }

        var directory = Path.GetDirectoryName(normalizedOutputPath) ?? Environment.CurrentDirectory;
        var extension = Path.GetExtension(normalizedOutputPath);
        var baseName = Path.GetFileNameWithoutExtension(normalizedOutputPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "encode";
        }

        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{baseName} ({index.ToString(CultureInfo.InvariantCulture)}){extension}");
            if (!IsOutputPathOccupied(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{baseName} ({Guid.NewGuid():N}){extension}");
    }

    private bool IsOutputPathOccupied(string outputPath)
    {
        return File.Exists(outputPath)
            || Directory.Exists(outputPath)
            || Jobs.Any(job => AreSamePath(job.Request.OutputPath, outputPath));
    }

    private static bool IsDuplicateJobRequest(EncodingJobRequest left, EncodingJobRequest right)
    {
        return AreSamePath(left.SourcePath, right.SourcePath)
            && AreSamePath(left.OutputPath, right.OutputPath)
            && left.PipelineKind == right.PipelineKind
            && left.PreferredArchitecture == right.PreferredArchitecture
            && AreSameEncodingParameters(left.Profile, right.Profile);
    }

    private static bool AreSameEncodingParameters(EncodingProfile left, EncodingProfile right)
    {
        return left.Kind == right.Kind
            && left.RateControl == right.RateControl
            && AreSameNumber(left.Quality, right.Quality)
            && left.Bitrate == right.Bitrate
            && AreSameText(left.Preset, right.Preset, StringComparison.OrdinalIgnoreCase)
            && AreSameText(left.Tune, right.Tune, StringComparison.OrdinalIgnoreCase)
            && AreSameText(left.Profile, right.Profile, StringComparison.OrdinalIgnoreCase)
            && AreSameText(left.OutputContainer, right.OutputContainer, StringComparison.OrdinalIgnoreCase)
            && AreSameText(left.AdditionalArguments, right.AdditionalArguments, StringComparison.Ordinal)
            && AreSameText(left.UhdParameters, right.UhdParameters, StringComparison.Ordinal);
    }

    private static bool AreSameNumber(double left, double right)
    {
        return Math.Abs(left - right) < 0.0001;
    }

    private static bool AreSameText(string? left, string? right, StringComparison comparison)
    {
        return string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, comparison);
    }

    private static bool AreSamePath(string leftPath, string rightPath)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(leftPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(rightPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string BuildDraftOutputPreviewText()
    {
        var preview = TryResolveDraftOutputPreview();
        if (preview.RunningOutputConflict is not null)
        {
            return Texts.OutputPreviewRunningConflict(
                preview.RunningOutputConflict.SourceFileName,
                preview.BaseOutputPath);
        }

        return BuildOutputPreviewText(preview.FinalOutputPath);
    }

    private QueueJobPreflightResult TryResolveDraftOutputPreview()
    {
        var preflight = AnalyzeCurrentJobForQueue(requireSourceExists: false);
        if (string.IsNullOrWhiteSpace(preflight.ValidationError))
        {
            return preflight;
        }

        return new QueueJobPreflightResult(
            string.Empty,
            TryResolveDraftOutputPreviewPath() ?? string.Empty,
            DuplicateJob: null,
            RunningOutputConflict: null,
            QueuedOutputConflictCount: 0,
            DiskOutputPathExists: false,
            ValidationError: null);
    }

    private string? TryResolveDraftOutputPreviewPath()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SourcePath))
            {
                return null;
            }

            var normalizedSource = Path.GetFullPath(SourcePath.Trim());
            var outputDirectory = !string.IsNullOrWhiteSpace(OutputPath)
                ? Path.GetFullPath(OutputPath.Trim())
                : Path.GetDirectoryName(normalizedSource) ?? Environment.CurrentDirectory;
            return ResolveUniqueOutputPath(ResolveDraftOutputPath(normalizedSource, outputDirectory, _activeProfile));
        }
        catch
        {
            return null;
        }
    }

    private string BuildOutputPreviewText(string? outputPath)
    {
        return string.IsNullOrWhiteSpace(outputPath)
            ? Texts.OutputPreviewPlaceholder
            : Texts.OutputPreviewText(outputPath);
    }

    private string? GetProfileConstraintError(EncodingProfile? profile)
    {
        if (profile is null)
        {
            return null;
        }

        if (SvtAv1ProfileConstraints.HasTwoPassOverlayConflict(profile))
        {
            return Texts.SvtAv1TwoPassOverlayConflict;
        }

        return GetArgumentConflictError(profile.Kind, profile.AdditionalArguments, profile.UhdParameters);
    }

    private string? GetRequestConstraintError(EncodingJobRequest request)
    {
        if (SvtAv1ProfileConstraints.HasTwoPassOverlayConflict(request.Profile))
        {
            return Texts.SvtAv1TwoPassOverlayConflict;
        }

        return GetArgumentConflictError(
            request.Profile.Kind,
            request.Profile.AdditionalArguments,
            request.Profile.UhdParameters);
    }

    private string? GetArgumentConflictError(
        EncoderKind kind,
        string? additionalArguments,
        string? uhdParameters)
    {
        var argumentConflict = EncoderArgumentConflictValidator.FindFirstConflict(
            kind,
            additionalArguments,
            uhdParameters);
        if (argumentConflict is not null)
        {
            return Texts.DescribeArgumentConflict(argumentConflict);
        }

        return null;
    }

    private void EnsureRequestConstraintsSatisfied(EncodingJobRequest request)
    {
        var error = GetRequestConstraintError(request);
        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException(error);
        }
    }

    private async Task RefreshPreviewNowAsync(EncodingProfile profile)
    {
        CancelPendingPreviewRefresh();
        var requestVersion = Interlocked.Increment(ref _previewRefreshVersion);
        await UpdatePreviewAsync(profile, requestVersion, CancellationToken.None);
    }

    private async Task UpdatePreviewAsync(
        EncodingProfile profile,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        var preview = await _profileLibraryService.BuildPreviewAsync(profile, cancellationToken);
        if (!IsPreviewRequestCurrent(requestVersion, cancellationToken))
        {
            return;
        }

        PreviewTitle = Texts.PipelinePreviewTitle(profile.Name, profile.Kind);
        PreviewCommandLine = preview.CommandLine;
        PreviewNotes = Texts.PipelinePreviewNotes(profile.Kind);

        if (string.IsNullOrWhiteSpace(SourcePath) || string.IsNullOrWhiteSpace(OutputPath))
        {
            return;
        }

        try
        {
            var request = CreateDraftRequest(requireSourceExists: false);
            if (!IsPreviewRequestCurrent(requestVersion, cancellationToken))
            {
                return;
            }

            var displayCommand = await BuildDisplayCommandAsync(request, cancellationToken);
            var resolvedNotes = BuildResolvedPreviewNotes(request);
            if (!IsPreviewRequestCurrent(requestVersion, cancellationToken))
            {
                return;
            }

            PreviewTitle = Texts.ActualCommandTitle(profile.Name);
            PreviewCommandLine = displayCommand;
            PreviewNotes = resolvedNotes;
        }
        catch (Exception ex)
        {
            if (!IsPreviewRequestCurrent(requestVersion, cancellationToken))
            {
                return;
            }

            PreviewNotes = $"{Texts.PipelinePreviewNotes(profile.Kind)}{Environment.NewLine}{Environment.NewLine}{Texts.ActualDraftNotReadyMessage(ex.Message)}";
        }
    }

    private string BuildResolvedPreviewNotes(EncodingJobRequest request)
    {
        var resolvedBinary = ResolveEncoderFromCachedSources(
            request.Profile.Kind,
            request.PreferredArchitecture);

        var binarySummary = resolvedBinary is null
            ? Texts.ResolvedBinaryMissing
            : Texts.ResolvedBinarySummary(
                BuildBinarySourceSummary(resolvedBinary),
                Path.GetFileName(resolvedBinary.ExecutablePath));

        return Texts.ResolvedPreviewNotes(request.OutputPath, binarySummary);
    }

    private void RefreshEncoderOptions()
    {
        var currentKind = SelectedEncoder?.Value ?? _activeProfile?.Kind ?? EncoderKind.X264;
        var autoKind = SelectedAutoEncoder?.Value ?? currentKind;
        var source = Encoders.Count == 0
            ? Enum.GetValues<EncoderKind>().Select(kind => new EncoderOption(kind, kind.ToDisplayName()))
            : Encoders.Select(item => new EncoderOption(item.Capability.Kind, item.Capability.DisplayName));

        _isSynchronizingDraft = true;

        try
        {
            ReplaceItems(EncoderOptions, source);
            SelectedEncoder = EncoderOptions.FirstOrDefault(option => option.Value == currentKind) ?? EncoderOptions.FirstOrDefault();
            SelectedAutoEncoder = EncoderOptions.FirstOrDefault(option => option.Value == autoKind) ?? EncoderOptions.FirstOrDefault();
        }
        finally
        {
            _isSynchronizingDraft = false;
        }
    }

    private static Brush ResolveBrush(string key)
    {
        return Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
            ? brush
            : new SolidColorBrush(Colors.Transparent);
    }

    private static Brush ResolveTaskStatusPanelBorderBrush(EncodingJobState? state)
    {
        return state switch
        {
            EncodingJobState.Failed => ResolveBrush("AppErrorBrush"),
            EncodingJobState.Cancelled => ResolveBrush("AppNeutralBrush"),
            _ => ResolveBrush("CardBorderBrush")
        };
    }

    private void ApplyEnvironmentReadiness(EnvironmentReadinessReport report)
    {
        _environmentReadinessReport = report;
        RefreshAutoCompressionMetricOptionsFromEnvironment(report);
        SetupGuideModule.ApplyEnvironmentReadiness(report);
        HandleAudioEnvironmentReadinessApplied();
        HandleBluRayEnvironmentReadinessApplied();
    }

    private void RefreshAutoCompressionMetricOptionsFromEnvironment(EnvironmentReadinessReport report)
    {
        var av1an = report.Tools.FirstOrDefault(tool => tool.Kind == RegisteredToolKind.Av1an);
        var capabilityOptions = av1an?.IsProtocolCompatible == true
            && av1an.Av1anCapabilities is { SupportedMetrics.Count: > 0 } capabilities
                ? BuildAutoCompressionMetricOptions(capabilities.SupportedMetrics)
                : BuildAutoCompressionMetricOptions();
        ReplaceItems(AutoCompressionMetricOptions, capabilityOptions);
        var resolvedMetric = AutoCompressionMetricSelection.ResolvePreferredMetric(
            AutoCompressionMetric,
            AutoCompressionMetricOptions.Select(static option => option.Value));
        if (resolvedMetric != AutoCompressionMetric)
        {
            AutoCompressionMetric = resolvedMetric;
        }

        _selectedAutoCompressionMetricOption = AutoCompressionMetricOptions.FirstOrDefault(option => option.Value == AutoCompressionMetric)
            ?? AutoCompressionMetricOptions.FirstOrDefault();
        OnPropertyChanged(nameof(AutoCompressionMetricOptions));
        OnPropertyChanged(nameof(SelectedAutoCompressionMetricOption));

        var interpolationMethodOptions = av1an?.IsProtocolCompatible == true
            && av1an.Av1anCapabilities is { InterpolationMethods.Count: > 0 } interpolationCapabilities
                ? BuildAutoCompressionInterpolationMethodOptions(interpolationCapabilities.InterpolationMethods)
                : BuildAutoCompressionInterpolationMethodOptions();
        ReplaceItems(AutoCompressionInterpolationMethodOptions, interpolationMethodOptions);
        _selectedAutoCompressionInterpolationMethodOption = AutoCompressionInterpolationMethodOptions.FirstOrDefault(option =>
                string.Equals(option.Value, AutoCompressionInterpolationMethod, StringComparison.OrdinalIgnoreCase))
            ?? AutoCompressionInterpolationMethodOptions.FirstOrDefault();
        OnPropertyChanged(nameof(AutoCompressionInterpolationMethodOptions));
        OnPropertyChanged(nameof(SelectedAutoCompressionInterpolationMethodOption));

        var probingStatisticOptions = av1an?.IsProtocolCompatible == true
            && av1an.Av1anCapabilities is { ProbingStatistics.Count: > 0 } probingCapabilities
                ? BuildAutoCompressionProbingStatisticOptions(probingCapabilities.ProbingStatistics)
                : BuildAutoCompressionProbingStatisticOptions();
        ReplaceItems(AutoCompressionProbingStatisticOptions, probingStatisticOptions);
        _selectedAutoCompressionProbingStatisticOption = AutoCompressionProbingStatisticOptions.FirstOrDefault(option =>
                string.Equals(option.Value, AutoCompressionProbingStatistic, StringComparison.OrdinalIgnoreCase))
            ?? AutoCompressionProbingStatisticOptions.FirstOrDefault();
        OnPropertyChanged(nameof(AutoCompressionProbingStatisticOptions));
        OnPropertyChanged(nameof(SelectedAutoCompressionProbingStatisticOption));
    }

    private string BuildRequirementLabel(CapabilityRequirementReadiness requirement)
    {
        return string.Join(
            " / ",
            requirement.CandidateResults.Select(static result => result.DisplayName));
    }

    private string BuildRequirementDetail(CapabilityRequirementReadiness requirement)
    {
        var label = BuildRequirementLabel(requirement);
        var preferredCandidate = requirement.CandidateResults.FirstOrDefault(static candidate => candidate.IsReady)
            ?? requirement.CandidateResults.FirstOrDefault(static candidate => candidate.State == ReadinessState.Misconfigured)
            ?? requirement.CandidateResults.First();

        var detail = preferredCandidate.State switch
        {
            ReadinessState.Missing => Texts.ToolMissingDetail(label),
            ReadinessState.Unknown => Texts.ToolUnknownDetail(label),
            _ => BuildToolProbeDetail(preferredCandidate)
        };

        return $"{label} · {Texts.ReadinessStateLabel(preferredCandidate.State)} · {detail}";
    }

    private string BuildToolProbeDetail(ToolProbeResult result)
    {
        if (result.Kind == RegisteredToolKind.Av1an && !string.IsNullOrWhiteSpace(result.BackendCompatibilityDetail))
        {
            var sourceLabel = Texts.ToolDetectionSourceLabel(result.Source, result.SourceLabel);
            var versionLabel = string.IsNullOrWhiteSpace(result.DetectedVersion)
                ? sourceLabel
                : $"{sourceLabel} · {result.DetectedVersion}";
            return string.IsNullOrWhiteSpace(versionLabel)
                ? result.BackendCompatibilityDetail
                : $"{versionLabel} · {result.BackendCompatibilityDetail}";
        }

        return result.State switch
        {
            ReadinessState.Ready when !string.IsNullOrWhiteSpace(result.DetectedVersion) =>
                $"{Texts.ToolDetectionSourceLabel(result.Source, result.SourceLabel)} · {result.DetectedVersion}",
            ReadinessState.Ready => Texts.ToolDetectionSourceLabel(result.Source, result.SourceLabel),
            ReadinessState.Misconfigured when !string.IsNullOrWhiteSpace(result.FailureReason) => result.FailureReason,
            ReadinessState.Missing => Texts.ToolMissingDetail(result.DisplayName),
            ReadinessState.Unknown when !string.IsNullOrWhiteSpace(result.FailureReason) => result.FailureReason,
            _ => Texts.ToolUnknownDetail(result.DisplayName)
        };
    }

    private void ApplyProfileToDraft(
        EncodingProfile profile,
        string sourceCaption,
        string templateName,
        string templateNotes)
    {
        _isSynchronizingDraft = true;

        try
        {
            _draftProfileName = profile.Name;
            _draftProfileDescription = profile.Description;
            DraftTemplateName = templateName;
            DraftTemplateNotes = templateNotes;
            SelectedProfileCaption = sourceCaption;
            SelectedEncoder = EncoderOptions.FirstOrDefault(option => option.Value == profile.Kind) ?? EncoderOptions.FirstOrDefault();
            ApplyCapabilityDefaults(profile);
        }
        finally
        {
            _isSynchronizingDraft = false;
        }

        FinalizeDraftChange(syncOutputPath: true, markAsCustomized: false);
    }

    private void ApplyCapabilityDefaults(EncodingProfile? preferredProfile = null)
    {
        var capability = GetSelectedCapability();
        if (capability is null)
        {
            _activeProfile = preferredProfile;
            RaiseComposerPropertyChanges();
            return;
        }

        var wasSynchronizingDraft = _isSynchronizingDraft;
        var baselineProfile = preferredProfile
            ?? DefaultEncodingProfiles.GetDefault(capability.Kind);
        _isSynchronizingDraft = true;

        try
        {
            _draftProfileName = baselineProfile.Name;
            _draftProfileDescription = baselineProfile.Description;

            ReplaceItems(
                AvailableRateControlModes,
                capability.RateControlModes.Select(mode => new RateControlOption(mode, mode.ToDisplayLabel())));
            ReplaceItems(
                AvailablePresets,
                capability.Presets.Select(preset => new StringChoiceOption(preset, preset)));
            ReplaceItems(AvailableTunes, BuildChoiceOptions(capability.Tunes, Texts.Pick("不指定", "None")));
            ReplaceItems(AvailableProfiles, BuildChoiceOptions(capability.Profiles, Texts.Pick("自动", "Auto")));
            ReplaceItems(
                AvailableOutputFormats,
                capability.OutputFormats.Select(format => new StringChoiceOption(format, $".{format}")));

            SelectedRateControl = AvailableRateControlModes.FirstOrDefault(option => option.Value == baselineProfile.RateControl)
                ?? AvailableRateControlModes.FirstOrDefault();
            SelectedPreset = FindChoiceOption(AvailablePresets, baselineProfile.Preset, fallbackToFirst: true);
            SelectedTune = FindChoiceOption(AvailableTunes, baselineProfile.Tune, fallbackToFirst: true);
            SelectedProfileOption = FindChoiceOption(AvailableProfiles, baselineProfile.Profile, fallbackToFirst: true);
            SelectedOutputFormat = FindChoiceOption(AvailableOutputFormats, baselineProfile.OutputContainer, fallbackToFirst: true);
            DraftQuality = baselineProfile.Quality;
            DraftBitrate = baselineProfile.Bitrate ?? 3500;
            DraftAdditionalArguments = baselineProfile.AdditionalArguments;
            DraftUhdParameters = baselineProfile.UhdParameters;
        }
        finally
        {
            _isSynchronizingDraft = wasSynchronizingDraft;
        }
    }

    private void ApplyManualArgumentOverrides(string rawArguments)
    {
        if (SelectedEncoder is null || string.IsNullOrWhiteSpace(rawArguments))
        {
            return;
        }

        var overrides = EncoderArgumentOverrideParser.Parse(SelectedEncoder.Value, rawArguments);
        var wasSynchronizingDraft = _isSynchronizingDraft;
        _isSynchronizingDraft = true;

        try
        {
            if (overrides.RateControl is { } rateControl)
            {
                SelectedRateControl = AvailableRateControlModes.FirstOrDefault(option => option.Value == rateControl) ?? SelectedRateControl;
            }

            if (overrides.Preset is not null)
            {
                SelectedPreset = FindChoiceOption(AvailablePresets, overrides.Preset, fallbackToFirst: false) ?? SelectedPreset;
            }

            if (overrides.Tune is not null)
            {
                SelectedTune = FindChoiceOption(AvailableTunes, overrides.Tune, fallbackToFirst: false) ?? SelectedTune;
            }

            if (overrides.Profile is not null)
            {
                SelectedProfileOption = FindChoiceOption(AvailableProfiles, overrides.Profile, fallbackToFirst: false) ?? SelectedProfileOption;
            }

            if (overrides.Quality is { } quality && quality > 0)
            {
                DraftQuality = quality;
            }

            if (overrides.Bitrate is { } bitrate && bitrate > 0)
            {
                DraftBitrate = bitrate;
            }
        }
        finally
        {
            _isSynchronizingDraft = wasSynchronizingDraft;
        }
    }

    private void FinalizeDraftChange(bool syncOutputPath, bool markAsCustomized)
    {
        _activeProfile = BuildCurrentDraftProfile();

        if (markAsCustomized)
        {
            SelectedProfileCaption = Texts.ManualDraftCaption;
        }

        if (_activeProfile is not null)
        {
            _draftProfileName = _activeProfile.Name;
            _draftProfileDescription = _activeProfile.Description;
        }

        if (syncOutputPath)
        {
            TryPopulateOutputPathIfEmpty();
        }

        RaiseComposerPropertyChanges();
        SchedulePreviewRefresh();
    }

    private EncodingProfile? BuildCurrentDraftProfile()
    {
        if (SelectedEncoder is null
            || SelectedRateControl is null
            || SelectedPreset is null
            || SelectedOutputFormat is null)
        {
            return null;
        }

        var profile = new EncodingProfile(
            SelectedEncoder.Value,
            _draftProfileName,
            _draftProfileDescription,
            SelectedPreset.Value,
            SelectedTune?.Value ?? string.Empty,
            SelectedProfileOption?.Value ?? string.Empty,
            SelectedRateControl.Value,
            IsQualityControlVisible ? DraftQuality : 0,
            IsBitrateControlVisible ? (int?)Math.Round(DraftBitrate) : null,
            SelectedOutputFormat.Value,
            GetSanitizedAdditionalArguments(),
            GetSanitizedUhdParameters());
        RequestValidation.ValidateEncodingProfile(profile);
        return profile;
    }

    private EncoderCapability? GetSelectedCapability()
    {
        if (SelectedEncoder is null)
        {
            return null;
        }

        return Encoders.FirstOrDefault(item => item.Capability.Kind == SelectedEncoder.Value)?.Capability;
    }

    private static List<StringChoiceOption> BuildChoiceOptions(IEnumerable<string> values, string emptyLabel)
    {
        var result = new List<StringChoiceOption>
        {
            new(string.Empty, emptyLabel)
        };

        result.AddRange(values.Select(value => new StringChoiceOption(value, value)));
        return result;
    }

    private static StringChoiceOption? FindChoiceOption(
        IEnumerable<StringChoiceOption> options,
        string? preferredValue,
        bool fallbackToFirst)
    {
        if (!string.IsNullOrWhiteSpace(preferredValue))
        {
            var matched = options.FirstOrDefault(option => string.Equals(option.Value, preferredValue, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return matched;
            }
        }

        if (string.IsNullOrWhiteSpace(preferredValue))
        {
            var empty = options.FirstOrDefault(static option => string.IsNullOrWhiteSpace(option.Value));
            if (empty is not null)
            {
                return empty;
            }
        }

        return fallbackToFirst ? options.FirstOrDefault() : null;
    }

    private string GetSanitizedAdditionalArguments()
    {
        if (SelectedEncoder is null)
        {
            return DraftAdditionalArguments.Trim();
        }

        var preserveRawSourceParameters = false;
        if (!string.IsNullOrWhiteSpace(SourcePath))
        {
            try
            {
                preserveRawSourceParameters = InputSourceSupport.ResolvePipelineKind(SourcePath) == InputPipelineKind.RawYuvFile;
            }
            catch (NotSupportedException)
            {
                preserveRawSourceParameters = false;
            }
        }

        return EncoderArgumentOverrideParser
            .Parse(SelectedEncoder.Value, DraftAdditionalArguments, preserveRawSourceParameters)
            .RemainingArguments
            .Trim();
    }

    private string GetSanitizedUhdParameters()
    {
        return SelectedEncoder?.Value == EncoderKind.X265
            ? DraftUhdParameters.Trim()
            : string.Empty;
    }

    internal string? ImportHdrParametersFromText(string rawText)
    {
        if (SelectedEncoder?.Value != EncoderKind.X265)
        {
            return Texts.HdrImportFailedStatus;
        }

        var result = HdrTextImportParser.Parse(rawText);
        if (!result.Success)
        {
            return Texts.HdrImportFailedStatus;
        }

        DraftUhdParameters = result.Arguments;
        StatusText = Texts.HdrParametersImportedStatus;
        return null;
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Load();
        var configuredWorkspaceRootPath = string.IsNullOrWhiteSpace(_appPaths.ConfiguredWorkspaceRootPath)
            ? _appPaths.RootPath
            : _appPaths.ConfiguredWorkspaceRootPath;
        PreferSystemEncoders = settings.PreferSystemEncoders;
        AutoCheckUpdatesOnStartup = settings.AutoCheckUpdatesOnStartup;
        MaxConcurrentEncodingJobs = settings.MaxConcurrentEncodingJobs;
        QueueCompletionAction = settings.QueueCompletionAction;
        _savedWorkspaceRootPath = configuredWorkspaceRootPath;
        WorkspaceRootPath = configuredWorkspaceRootPath;
        _hasCompletedSetupGuide = settings.HasSeenSetupGuide;
        _manualToolPaths = new Dictionary<string, string>(settings.EffectiveManualToolPaths, StringComparer.OrdinalIgnoreCase);
        _hasRunInitialVsPluginDependencyUpdate = settings.HasRunInitialVsPluginDependencyUpdate;
        SelectedTheme = ThemeOptions.FirstOrDefault(option => option.Value == settings.Theme) ?? ThemeOptions[0];
        SelectedLanguage = LanguageOptions.FirstOrDefault(option => option.Value == settings.Language) ?? LanguageOptions[0];
    }

    private void RunInitialVsPluginDependencyUpdateIfNeeded()
    {
        if (_hasRunInitialVsPluginDependencyUpdate)
        {
            return;
        }

        _hasRunInitialVsPluginDependencyUpdate = true;
        SaveSettings(updateStatusText: false);

        var readiness = _environmentReadinessReport;
        _ = Task.Run(async () =>
        {
            try
            {
                await _setupBootstrapService.RefreshVsPluginPackageDefinitionsAsync(readiness);
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"Initial VS plugin dependency refresh failed. {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private void WriteDiagnostic(string message)
    {
        AppDiagnosticsLog.Write(_appPaths, nameof(MainWindowViewModel), message);
    }

    internal async Task<string?> PrepareWorkspaceRootChangeAsync(string proposedWorkspaceRootPath)
    {
        if (HasRunningJobs
            || IsAutoCompressionRunning
            || IsAudioProcessingRunning
            || IsBluRayDemuxRunning
            || _isCheckingUpdates
            || _isDownloadingAppUpdateInstaller
            || SetupGuideModule.IsSetupGuideInstallRunning
            || SetupGuideModule.IsRefreshingSetupGuide
            || SetupGuideModule.IsCheckingSetupDependencyUpdates)
        {
            return Texts.WorkspaceDirectoryChangeBlockedMessage;
        }

        string normalizedWorkspaceRootPath;
        try
        {
            normalizedWorkspaceRootPath = _appPaths.NormalizeWorkspaceRootPath(proposedWorkspaceRootPath);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if (_appPaths.IsWorkspaceRootInsideInstallRoot(normalizedWorkspaceRootPath)
            || _appPaths.IsWorkspaceRootInsideProgramFiles(normalizedWorkspaceRootPath))
        {
            return Texts.WorkspaceDirectoryInvalidLocationMessage;
        }

        if (string.Equals(normalizedWorkspaceRootPath, WorkspaceRootPath, StringComparison.OrdinalIgnoreCase))
        {
            WorkspaceRootPath = normalizedWorkspaceRootPath;
            return null;
        }

        StatusText = Texts.WorkspaceDirectoryPreparingStatus;

        try
        {
            await Task.Run(() => _appPaths.PrepareWorkspaceRootChange(normalizedWorkspaceRootPath));
            WorkspaceRootPath = normalizedWorkspaceRootPath;
            return null;
        }
        catch (WorkspaceRootConflictException ex)
        {
            return Texts.WorkspaceDirectoryConflictMessage(ex.RelativePath);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void SchedulePreviewRefresh()
    {
        if (_activeProfile is null)
        {
            CancelPendingPreviewRefresh();
            PreviewTitle = Texts.DraftNotReadyTitle;
            PreviewCommandLine = string.Empty;
            PreviewNotes = Texts.DraftNotReadyNotes;
            return;
        }

        CancelPendingPreviewRefresh();
        var requestVersion = Interlocked.Increment(ref _previewRefreshVersion);
        var cancellationTokenSource = new CancellationTokenSource();
        _previewRefreshCancellationTokenSource = cancellationTokenSource;

        _ = RefreshPreviewDeferredAsync(_activeProfile, requestVersion, cancellationTokenSource.Token);
    }

    private void ScheduleDraftInputRefresh()
    {
        CancelPendingDraftInputRefresh();
        var requestVersion = Interlocked.Increment(ref _draftInputRefreshVersion);
        var cancellationTokenSource = new CancellationTokenSource();
        _draftInputRefreshCancellationTokenSource = cancellationTokenSource;

        _ = RefreshDraftInputDeferredAsync(requestVersion, cancellationTokenSource.Token);
    }

    private async Task RefreshDraftInputDeferredAsync(int requestVersion, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InputPathRefreshDelay, cancellationToken);
            if (!IsDraftInputRefreshCurrent(requestVersion, cancellationToken))
            {
                return;
            }

            var hasPathState = !string.IsNullOrWhiteSpace(SourcePath) || !string.IsNullOrWhiteSpace(OutputPath);
            SetDraftInputRefreshPending(hasPathState);

            if (hasPathState && _activeProfile is not null)
            {
                PreviewTitle = Texts.DraftInputPreparingPreviewTitle;
                PreviewNotes = Texts.DraftInputPreparingPreviewNotes;
                await Task.Yield();
                if (!IsDraftInputRefreshCurrent(requestVersion, cancellationToken))
                {
                    return;
                }
            }

            _isApplyingDeferredDraftInputRefresh = true;
            try
            {
                TryPopulateOutputPathIfEmpty();
                RaiseDraftPathPropertyChanges();
            }
            finally
            {
                _isApplyingDeferredDraftInputRefresh = false;
            }

            if (!IsDraftInputRefreshCurrent(requestVersion, cancellationToken))
            {
                return;
            }

            SetDraftInputRefreshPending(false);
            SchedulePreviewRefresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool IsDraftInputRefreshCurrent(int requestVersion, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && requestVersion == Volatile.Read(ref _draftInputRefreshVersion);
    }

    private void SetDraftInputRefreshPending(bool isPending)
    {
        if (_isDraftInputRefreshPending == isPending)
        {
            return;
        }

        _isDraftInputRefreshPending = isPending;
        OnPropertyChanged(nameof(DraftOutputPreviewText));
    }

    private async Task RefreshPreviewDeferredAsync(
        EncodingProfile profile,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(120, cancellationToken);
            await UpdatePreviewAsync(profile, requestVersion, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsPreviewRequestCurrent(requestVersion, cancellationToken))
            {
                PreviewNotes = Texts.ActualDraftNotReadyMessage(ex.Message);
            }
        }
    }

    private void CancelPendingPreviewRefresh()
    {
        _previewRefreshCancellationTokenSource?.Cancel();
        _previewRefreshCancellationTokenSource?.Dispose();
        _previewRefreshCancellationTokenSource = null;
    }

    private void CancelPendingDraftInputRefresh()
    {
        _draftInputRefreshCancellationTokenSource?.Cancel();
        _draftInputRefreshCancellationTokenSource?.Dispose();
        _draftInputRefreshCancellationTokenSource = null;
    }

    private bool IsPreviewRequestCurrent(int requestVersion, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && requestVersion == Volatile.Read(ref _previewRefreshVersion);
    }

    private void TryPopulateOutputPathIfEmpty()
    {
        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            return;
        }

        var sourceDirectory = Path.GetDirectoryName(SourcePath);
        var suggestedPath = sourceDirectory ?? Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(OutputPath)
            && !string.Equals(OutputPath, _lastAutoOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetAutoOutputPath(suggestedPath);
    }

    private void SetAutoOutputPath(string path)
    {
        _isUpdatingOutputPath = true;

        try
        {
            OutputPath = path;
            _lastAutoOutputPath = path;
        }
        finally
        {
            _isUpdatingOutputPath = false;
        }
    }

    private void RaiseJobStatePropertyChanges()
    {
        OnPropertyChanged(nameof(HasRunningJobs));
        OnPropertyChanged(nameof(HasRunningAppWork));
        RaiseJobSummaryPropertyChanges();
        RaiseDashboardCardActivityPropertyChanges();
    }

    private void RaiseDashboardCardActivityPropertyChanges()
    {
        OnPropertyChanged(nameof(DashboardBluRayDemuxActivityVisibility));
        OnPropertyChanged(nameof(DashboardBluRayDemuxProgressValue));
        OnPropertyChanged(nameof(DashboardBluRayDemuxProgressIsIndeterminate));
        OnPropertyChanged(nameof(DashboardOverviewActivityVisibility));
        OnPropertyChanged(nameof(DashboardOverviewProgressValue));
        OnPropertyChanged(nameof(DashboardOverviewProgressIsIndeterminate));
        OnPropertyChanged(nameof(DashboardAudioProcessingActivityVisibility));
        OnPropertyChanged(nameof(DashboardAudioProcessingProgressValue));
        OnPropertyChanged(nameof(DashboardAudioProcessingProgressIsIndeterminate));
        OnPropertyChanged(nameof(DashboardAutoCompressionActivityVisibility));
        OnPropertyChanged(nameof(DashboardAutoCompressionProgressValue));
        OnPropertyChanged(nameof(DashboardAutoCompressionProgressIsIndeterminate));
    }

    private EncodingJobItemViewModel? GetDashboardRunningOverviewJob()
    {
        return Jobs.FirstOrDefault(static job => job.State == EncodingJobState.Running);
    }

    private bool IsDashboardBluRayDemuxActive()
    {
        return IsBluRayDiscScanning || IsBluRayPlaylistLoading || IsBluRayDemuxRunning;
    }

    private string BuildSelectedJobFramesText()
    {
        var currentFrame = SelectedJob?.CurrentFrame ?? 0;
        var totalFrames = SelectedJob?.TotalFrames?.ToString(CultureInfo.InvariantCulture) ?? "?";
        return $"{currentFrame.ToString(CultureInfo.InvariantCulture)}/{totalFrames} frames";
    }

    private int GetSelectedQueueJobStateCount(EncodingJobState state)
    {
        return _selectedQueueJobs.Count(job => job.State == state);
    }

    private static string FormatSelectedJobEta(TimeSpan? eta)
    {
        if (!eta.HasValue)
        {
            return "--:--:--";
        }

        var totalHours = Math.Max(0, (int)Math.Floor(eta.Value.TotalHours));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalHours:00}:{eta.Value.Minutes:00}:{eta.Value.Seconds:00}");
    }

    private static string FormatSelectedJobSize(long? bytes)
    {
        if (!bytes.HasValue)
        {
            return "--";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes.Value;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size:0} {units[unitIndex]}"
            : $"{size:0.#} {units[unitIndex]}";
    }

    private void RaiseSelectedJobProgressMetricPropertyChanges()
    {
        OnPropertyChanged(nameof(SelectedJobProgressValue));
        OnPropertyChanged(nameof(SelectedJobProgressPrimaryText));
        OnPropertyChanged(nameof(SelectedJobProgressSecondaryText));
        OnPropertyChanged(nameof(SelectedJobProgressPercentText));
        OnPropertyChanged(nameof(SelectedJobSourcePreparationVisibility));
        OnPropertyChanged(nameof(SelectedJobSourcePreparationText));
        OnPropertyChanged(nameof(SelectedJobFramesText));
        OnPropertyChanged(nameof(SelectedJobFpsText));
        OnPropertyChanged(nameof(SelectedJobBitrateText));
        OnPropertyChanged(nameof(SelectedJobEtaText));
        OnPropertyChanged(nameof(SelectedJobEstimatedSizeText));
    }

    private void SelectedJob_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, SelectedJob) || _isDisposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(e.PropertyName))
        {
            RaiseSelectedJobPropertyChanges();
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(EncodingJobItemViewModel.Summary):
            case nameof(EncodingJobItemViewModel.StateLabel):
                OnPropertyChanged(nameof(SelectedJobSummary));
                break;

            case nameof(EncodingJobItemViewModel.DisplayCommand):
            case nameof(EncodingJobItemViewModel.IsDisplayCommandResolved):
                OnPropertyChanged(nameof(SelectedJobCommandText));
                OnPropertyChanged(nameof(CanCopySelectedJobCommand));
                break;

            case nameof(EncodingJobItemViewModel.ProgressValue):
            case nameof(EncodingJobItemViewModel.ProgressPercentLabel):
            case nameof(EncodingJobItemViewModel.IsSourcePreparation):
            case nameof(EncodingJobItemViewModel.SourcePreparationText):
            case nameof(EncodingJobItemViewModel.HasSourcePreparationText):
            case nameof(EncodingJobItemViewModel.CurrentFrame):
            case nameof(EncodingJobItemViewModel.TotalFrames):
            case nameof(EncodingJobItemViewModel.FramesPerSecond):
            case nameof(EncodingJobItemViewModel.BitrateKbps):
            case nameof(EncodingJobItemViewModel.Eta):
            case nameof(EncodingJobItemViewModel.ProgressTelemetryPrimaryLine):
            case nameof(EncodingJobItemViewModel.EstimatedFileSizeBytes):
            case nameof(EncodingJobItemViewModel.ProgressTelemetrySecondaryLine):
                RaiseSelectedJobProgressMetricPropertyChanges();
                break;

            case nameof(EncodingJobItemViewModel.Log):
                OnPropertyChanged(nameof(SelectedJobLogText));
                break;
        }
    }

    private void RaiseSummaryPropertyChanges()
    {
        OnPropertyChanged(nameof(SelectedJobSummary));
    }

    private void RaiseComposerPropertyChanges()
    {
        TemplatesModule.Library.NotifyDraftChanged();
        OnPropertyChanged(nameof(SuggestedOutputExtension));
        OnPropertyChanged(nameof(SuggestedOutputFileName));
        OnPropertyChanged(nameof(DraftOutputPreviewText));
        OnPropertyChanged(nameof(QualityInputLabel));
        OnPropertyChanged(nameof(BitrateInputLabel));
        OnPropertyChanged(nameof(IsQualityControlVisible));
        OnPropertyChanged(nameof(IsBitrateControlVisible));
        OnPropertyChanged(nameof(IsX265Selected));
        OnPropertyChanged(nameof(X265UhdVisibility));
        OnPropertyChanged(nameof(DraftConstraintWarningText));
        OnPropertyChanged(nameof(DraftConstraintWarningVisibility));
        OnPropertyChanged(nameof(DraftQualityVisibility));
        OnPropertyChanged(nameof(DraftBitrateVisibility));
        OnPropertyChanged(nameof(CanQueueJob));
    }

    private void RaiseDraftPathPropertyChanges()
    {
        OnPropertyChanged(nameof(CanQueueJob));
        OnPropertyChanged(nameof(SuggestedOutputFileName));
        OnPropertyChanged(nameof(DraftOutputPreviewText));
    }

    private void RaiseJobSummaryPropertyChanges()
    {
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(EmptyQueueVisibility));
        OnPropertyChanged(nameof(QueueSelectionCommandBarVisibility));
        OnPropertyChanged(nameof(QueueSummary));
        RaiseQueueSelectionPropertyChanges();
        RefreshJobPositionFlags();
    }

    private void RefreshJobPositionFlags()
    {
        var floorIndex = GetQueuedMoveFloorIndex();
        var lastIndex = Jobs.Count - 1;

        for (var i = 0; i < Jobs.Count; i++)
        {
            var job = Jobs[i];
            if (job.State != EncodingJobState.Queued)
            {
                job.UpdatePositionFlags(false, false, false, false);
                continue;
            }

            var isFirstQueued = i == floorIndex;
            var isLastQueued = i == lastIndex;
            job.UpdatePositionFlags(
                canMoveUp: !isFirstQueued,
                canMoveDown: !isLastQueued,
                canMoveToTop: !isFirstQueued,
                canMoveToBottom: !isLastQueued);
        }
    }

    private void RaiseSelectedJobPropertyChanges()
    {
        OnPropertyChanged(nameof(SelectedJobSummary));
        RaiseSelectedJobProgressMetricPropertyChanges();
        OnPropertyChanged(nameof(SelectedJobCommandText));
        OnPropertyChanged(nameof(CanCopySelectedJobCommand));
        OnPropertyChanged(nameof(SelectedJobLogText));
    }

    private void RaiseQueueSelectionPropertyChanges()
    {
        OnPropertyChanged(nameof(SelectedQueueJobCount));
        OnPropertyChanged(nameof(SelectedQueuedJobCount));
        OnPropertyChanged(nameof(SelectedRunningJobCount));
        OnPropertyChanged(nameof(SelectedCancelableQueueJobCount));
        OnPropertyChanged(nameof(SelectedRemovableQueueJobCount));
        OnPropertyChanged(nameof(QueueSelectionStatusText));
        OnPropertyChanged(nameof(CanSelectAllQueueJobs));
        OnPropertyChanged(nameof(CanInvertQueueSelection));
        OnPropertyChanged(nameof(CanClearQueueSelection));
        OnPropertyChanged(nameof(CanStartSelectedJobs));
        OnPropertyChanged(nameof(CanCancelSelectedJobs));
        OnPropertyChanged(nameof(CanDeleteSelectedJobs));
        RaiseSelectedJobPropertyChanges();
    }

    private IEnumerable<EncodingJobItemViewModel> NormalizeSelectedQueueJobs(IEnumerable<EncodingJobItemViewModel> selectedJobs)
    {
        var selectedJobIds = selectedJobs
            .Where(static job => job is not null)
            .Select(static job => job.JobId)
            .ToHashSet();

        foreach (var job in Jobs)
        {
            if (!selectedJobIds.Contains(job.JobId))
            {
                continue;
            }

            yield return job;
        }
    }

    private void PruneSelectedQueueJobs()
    {
        var normalizedSelection = NormalizeSelectedQueueJobs(_selectedQueueJobs).ToList();
        if (_selectedQueueJobs.Count == normalizedSelection.Count
            && _selectedQueueJobs.SequenceEqual(normalizedSelection))
        {
            RaiseQueueSelectionPropertyChanges();
            return;
        }

        _selectedQueueJobs.Clear();
        _selectedQueueJobs.AddRange(normalizedSelection);
        RaiseQueueSelectionPropertyChanges();
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();

        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private IEnumerable<ThemeOption> BuildThemeOptions()
    {
        return
        [
            new ThemeOption(AppThemePreference.Default, Texts.ThemeLabel(AppThemePreference.Default)),
            new ThemeOption(AppThemePreference.Light, Texts.ThemeLabel(AppThemePreference.Light)),
            new ThemeOption(AppThemePreference.Dark, Texts.ThemeLabel(AppThemePreference.Dark))
        ];
    }

    private static IEnumerable<AutoCompressionMetricOption> BuildAutoCompressionMetricOptions()
    {
        return BuildAutoCompressionMetricOptions(
        [
            AutoCompressionMetric.Vmaf,
            AutoCompressionMetric.Ssimulacra2,
            AutoCompressionMetric.ButteraugliInf,
            AutoCompressionMetric.Butteraugli3,
            AutoCompressionMetric.Xpsnr,
            AutoCompressionMetric.XpsnrWeighted
        ]);
    }

    private static IEnumerable<AutoCompressionMetricOption> BuildAutoCompressionMetricOptions(IEnumerable<AutoCompressionMetric> metrics)
    {
        return
            metrics.Select(metric => new AutoCompressionMetricOption(metric, metric switch
            {
                AutoCompressionMetric.Vmaf => "VMAF",
                AutoCompressionMetric.Ssimulacra2 => "SSIMULACRA2",
                AutoCompressionMetric.ButteraugliInf => "Butteraugli-INF",
                AutoCompressionMetric.Butteraugli3 => "Butteraugli-3",
                AutoCompressionMetric.Xpsnr => "XPSNR",
                AutoCompressionMetric.XpsnrWeighted => "XPSNR-Weighted",
                _ => metric.ToString()
            }));
    }

    private static IEnumerable<StringChoiceOption> BuildAutoCompressionProbingStatisticOptions()
    {
        return BuildAutoCompressionProbingStatisticOptions(
        [
            "auto",
            "mean",
            "median",
            "harmonic",
            "percentile",
            "standard-deviation",
            "mode",
            "minimum",
            "maximum",
            "root-mean-square"
        ]);
    }

    private static IEnumerable<StringChoiceOption> BuildAutoCompressionProbingStatisticOptions(IEnumerable<string> values)
    {
        static string ToLabel(string value)
        {
            return value switch
            {
                "auto" => "Auto",
                "mean" => "Mean",
                "median" => "Median",
                "harmonic" => "Harmonic",
                "percentile" => "Percentile",
                "standard-deviation" => "Standard Deviation",
                "mode" => "Mode",
                "minimum" => "Minimum",
                "maximum" => "Maximum",
                "root-mean-square" => "Root Mean Square",
                _ => value
            };
        }

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Where(static value => !string.Equals(value, "percentile", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "standard-deviation", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => new StringChoiceOption(value, ToLabel(value)));
    }

    private static IEnumerable<StringChoiceOption> BuildAutoCompressionInterpolationMethodOptions()
    {
        return BuildAutoCompressionInterpolationMethodOptions(
        [
            "linear",
            "quadratic",
            "natural",
            "pchip",
            "catmull",
            "akima",
            "cubic-polynomial"
        ]);
    }

    private static IEnumerable<StringChoiceOption> BuildAutoCompressionInterpolationMethodOptions(IEnumerable<string> values)
    {
        var available = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var fourthRoundMethods = new[] { "linear", "quadratic", "natural" }
            .Where(method => available.Contains(method, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var fifthRoundMethods = available;

        var result = new List<StringChoiceOption>
        {
            new(string.Empty, "Backend Default")
        };

        foreach (var fourthRoundMethod in fourthRoundMethods)
        {
            foreach (var fifthRoundMethod in fifthRoundMethods)
            {
                var combined = $"{fourthRoundMethod}-{fifthRoundMethod}";
                result.Add(new StringChoiceOption(combined, combined));
            }
        }

        return result;
    }

    private static IEnumerable<StringChoiceOption> BuildConcurrentEncodingJobOptions()
    {
        return Enumerable
            .Range(MinConcurrentEncodingJobs, MaxConcurrentEncodingJobsLimit)
            .Select(value =>
            {
                var text = value.ToString(CultureInfo.InvariantCulture);
                return new StringChoiceOption(text, text);
            });
    }

    private IEnumerable<StringChoiceOption> BuildQueueCompletionActionOptions()
    {
        return
        [
            new StringChoiceOption(QueueCompletionAction.None.ToString(), Texts.QueueCompletionActionLabel(QueueCompletionAction.None)),
            new StringChoiceOption(QueueCompletionAction.Sleep.ToString(), Texts.QueueCompletionActionLabel(QueueCompletionAction.Sleep)),
            new StringChoiceOption(QueueCompletionAction.Shutdown.ToString(), Texts.QueueCompletionActionLabel(QueueCompletionAction.Shutdown))
        ];
    }

    private void ApplyLanguage(AppLanguage language)
    {
        Texts = new AppText(language);

        var themePreference = CurrentThemePreference;
        ReplaceItems(ThemeOptions, BuildThemeOptions());
        _selectedTheme = ThemeOptions.FirstOrDefault(option => option.Value == themePreference) ?? ThemeOptions[0];
        OnPropertyChanged(nameof(SelectedTheme));
        ReplaceItems(QueueCompletionActionOptions, BuildQueueCompletionActionOptions());
        SyncSelectedQueueCompletionActionOption(QueueCompletionAction);

        foreach (var job in Jobs)
        {
            job.SetLanguage(language);
        }

        TemplatesModule.Library.RefreshLibraryView();
        RaiseSetupGuidePropertyChanges();
        RefreshSelectedProfileCaption();
        RaiseSummaryPropertyChanges();
        RaiseComposerPropertyChanges();
        RaiseJobSummaryPropertyChanges();
        RaiseSelectedJobPropertyChanges();

        if (_activeProfile is null)
        {
            PreviewTitle = Texts.DraftNotReadyTitle;
            PreviewNotes = Texts.DraftNotReadyNotes;
        }

        if (string.IsNullOrWhiteSpace(OutputPath) && string.IsNullOrWhiteSpace(SourcePath))
        {
            OnPropertyChanged(nameof(SuggestedOutputFileName));
        }

        OnPropertyChanged(nameof(DraftOutputPreviewText));
        RaiseAppUpdatePropertyChanges();

        ApplyAutoCompressionLanguageState();
        ApplyAudioProcessingLanguageState();
        ApplyBluRayDemuxLanguageState();

        SchedulePreviewRefresh();
    }

    private string GetAppUpdateStatusText()
    {
        var currentVersion = GetKnownCurrentAppVersion();
        if (!string.IsNullOrWhiteSpace(_lastAppUpdateErrorMessage))
        {
            return _lastAppUpdateErrorMessage;
        }

        if (_isDownloadingAppUpdateInstaller && _lastAppUpdateResult is not null)
        {
            return Texts.AppUpdateDownloadingStatus(_lastAppUpdateResult.LatestVersion, _appUpdateDownloadProgressPercent);
        }

        if (_lastAppUpdateResult is null)
        {
            return Texts.AppUpdateIdleStatus;
        }

        return !_lastAppUpdateResult.HasPublishedRelease
            ? Texts.AppReleaseNotPublishedStatus(currentVersion)
            : _lastAppUpdateResult.UpdateAvailable
                ? _lastAppUpdateResult.CanDownloadInstaller
                    ? Texts.AppUpdateAvailableStatus(currentVersion, _lastAppUpdateResult.LatestVersion)
                    : Texts.AppUpdateManualDownloadStatus(currentVersion, _lastAppUpdateResult.LatestVersion)
                : _lastAppUpdateResult.IsCurrentVersionNewerThanRelease
                    ? Texts.AppCurrentVersionAheadStatus(currentVersion, _lastAppUpdateResult.LatestVersion)
                    : _lastAppUpdateResult.VersionsComparable
                        ? Texts.AppAlreadyLatestStatus(currentVersion)
                        : Texts.AppUpdateComparisonUnavailableStatus(currentVersion, _lastAppUpdateResult.LatestVersion);
    }

    private string GetKnownCurrentAppVersion()
    {
        return _lastAppUpdateResult?.CurrentVersion ?? GetCurrentAppVersionLabel();
    }

    private void RaiseAppUpdatePropertyChanges()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsCheckingAppUpdates));
        OnPropertyChanged(nameof(IsDownloadingAppUpdateInstaller));
        OnPropertyChanged(nameof(IsAppUpdateActionInProgress));
        OnPropertyChanged(nameof(IsAppUpdateAvailable));
        OnPropertyChanged(nameof(CanDownloadAppUpdateInstaller));
        OnPropertyChanged(nameof(HasAppUpdateError));
        OnPropertyChanged(nameof(AppUpdateActionText));
        OnPropertyChanged(nameof(AppUpdateActionIcon));
        OnPropertyChanged(nameof(CanExecuteAppUpdateAction));
        OnPropertyChanged(nameof(AppUpdateProgressVisibility));
        OnPropertyChanged(nameof(AppUpdateReleaseUrl));
        OnPropertyChanged(nameof(AppCurrentVersionText));
        OnPropertyChanged(nameof(AppLatestVersionText));
        OnPropertyChanged(nameof(AppLatestVersionVisibility));
        OnPropertyChanged(nameof(AppUpdateStatusText));
    }

    private static string GetCurrentAppVersionLabel()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return NormalizeVersionLabel(informationalVersion)
            ?? NormalizeVersionLabel(assembly.GetName().Version?.ToString())
            ?? "0.0.0";
    }

    private static string? NormalizeVersionLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 1
            && (trimmed[0] == 'v' || trimmed[0] == 'V')
            && char.IsDigit(trimmed[1]))
        {
            trimmed = trimmed[1..];
        }

        var versionMatch = Regex.Match(trimmed, "(?<base>\\d+\\.\\d+(?:\\.\\d+)*)(?<suffix>[0-9a-f]{7,12})?", RegexOptions.IgnoreCase);
        if (versionMatch.Success)
        {
            var suffix = versionMatch.Groups["suffix"].Success
                ? versionMatch.Groups["suffix"].Value.ToLowerInvariant()
                : string.Empty;
            return versionMatch.Groups["base"].Value + suffix;
        }

        return trimmed;
    }

    partial void InitializeAudioProcessingState();

    partial void DisposeAudioProcessingState();

    partial void HandleAudioEnvironmentReadinessApplied();

    partial void ApplyAudioProcessingLanguageState();

    partial void InitializeBluRayDemuxState();

    partial void DisposeBluRayDemuxState();

    partial void HandleBluRayEnvironmentReadinessApplied();

    partial void ApplyBluRayDemuxLanguageState();

    private void RefreshSelectedProfileCaption()
    {
        var library = TemplatesModule?.Library;
        var selectionKey = library?.CurrentTemplateSelectionKey;
        var editingId = library?.EditingUserTemplateId;
        var hasUnsaved = library?.HasUnsavedTemplateChanges == true;

        if (selectionKey is not null && hasUnsaved)
        {
            SelectedProfileCaption = Texts.ManualDraftCaption;
            return;
        }

        if (selectionKey is not null)
        {
            if (selectionKey.StartsWith("user:", StringComparison.Ordinal))
            {
                var name = string.IsNullOrWhiteSpace(DraftTemplateName) ? _activeProfile?.Name ?? string.Empty : DraftTemplateName;
                SelectedProfileCaption = Texts.UserCaption(name);
                return;
            }
        }

        if (editingId is not null)
        {
            var name = string.IsNullOrWhiteSpace(DraftTemplateName) ? _activeProfile?.Name ?? string.Empty : DraftTemplateName;
            SelectedProfileCaption = Texts.UserCaption(name);
            return;
        }

        if (string.IsNullOrWhiteSpace(DraftTemplateName)
            && string.IsNullOrWhiteSpace(DraftTemplateNotes)
            && selectionKey is null)
        {
            SelectedProfileCaption = Texts.NewTemplateCaption;
            return;
        }

        if (_activeProfile is null)
        {
            SelectedProfileCaption = Texts.NoProfileSelectedCaption;
            return;
        }

        SelectedProfileCaption = Texts.ManualDraftCaption;
    }

    private string BuildBinarySourceSummary(DiscoveredEncoderBinary binary)
    {
        return Texts.BinarySourceSummary(binary.Source, binary.SourceLabel);
    }

    private async Task RefreshSystemBinariesAsync(CancellationToken cancellationToken = default)
    {
        var discoveredBinaries = await Task.Run(
            () => _encoderDiscoveryService.DiscoverSystemBinaries(),
            cancellationToken);

        ReplaceItems(DetectedSystemBinaries, discoveredBinaries);
    }

    private Task<string> BuildDisplayCommandAsync(
        EncodingJobRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => _jobRunner.BuildDisplayCommand(request), cancellationToken);
    }

    private async Task ResolveJobDisplayCommandAsync(EncodingJobItemViewModel job, EncodingJobRequest request)
    {
        try
        {
            var displayCommand = await BuildDisplayCommandAsync(request);
            job.UpdateDisplayCommand(displayCommand);
            if (ReferenceEquals(SelectedJob, job))
            {
                OnPropertyChanged(nameof(SelectedJobCommandText));
            }
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"Failed to resolve job display command for job {job.JobId}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private DiscoveredEncoderBinary? ResolveEncoderFromCachedSources(
        EncoderKind kind,
        EncoderArchitecture preferredArchitecture,
        EncoderCatalogItem? catalogItem = null)
    {
        catalogItem ??= Encoders.FirstOrDefault(item => item.Capability.Kind == kind);

        var localBinaries = catalogItem?.Binaries ?? [];
        var localCandidate = localBinaries
            .Where(static binary => binary.Exists)
            .OrderByDescending(binary => binary.Architecture == preferredArchitecture)
            .Select(binary => new DiscoveredEncoderBinary(
                kind,
                binary.Architecture,
                binary.LocalPath,
                EncoderBinarySource.LocalToolset,
                "encoders",
                binary.DetectedVersion))
            .FirstOrDefault();

        if (localCandidate is not null)
        {
            return localCandidate;
        }

        if (!PreferSystemEncoders)
        {
            return null;
        }

        return DetectedSystemBinaries
            .Where(binary => binary.Kind == kind)
            .OrderByDescending(binary => binary.Architecture == preferredArchitecture)
            .ThenBy(binary => binary.Source)
            .FirstOrDefault();
    }
}

public enum MoveQueuedJobMode
{
    Next,
    Top,
    Up,
    Down,
    Bottom
}

public sealed record QueueJobPreflightResult(
    string BaseOutputPath,
    string FinalOutputPath,
    EncodingJobItemViewModel? DuplicateJob,
    EncodingJobItemViewModel? RunningOutputConflict,
    int QueuedOutputConflictCount,
    bool DiskOutputPathExists,
    string? ValidationError)
{
    public bool IsOutputPathAutoRenamed =>
        !string.IsNullOrWhiteSpace(BaseOutputPath)
        && !string.IsNullOrWhiteSpace(FinalOutputPath)
        && !string.Equals(BaseOutputPath, FinalOutputPath, StringComparison.OrdinalIgnoreCase);
}
