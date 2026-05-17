using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;

namespace FlowEncode.ViewModels;

public sealed class SetupGuideViewModel : ObservableObject, ISetupDependencyModuleViewModel
{
    private readonly ISetupGuideHost _host;
    private readonly ISetupBootstrapService _setupBootstrapService;
    private readonly ISetupGuideCacheService _setupGuideCacheService;
    private readonly IToolRegistryService _toolRegistryService;
    private readonly LocalAppPaths _appPaths;

    private SetupDependencyStatusReport? _setupGuideStatusReport;
    private bool _hasCompletedSetupGuide;
    private bool _isSetupGuideOpen;
    private int _selectedSetupGuideCardIndex;
    private bool _isSetupGuideInstallRunning;
    private bool _isRefreshingSetupGuide;
    private bool _isCheckingSetupDependencyUpdates;
    private DateTimeOffset? _setupGuideLocalCheckedAt;
    private DateTimeOffset? _setupGuideRemoteCheckedAt;

    public SetupGuideViewModel(
        ISetupGuideHost host,
        ISetupBootstrapService setupBootstrapService,
        ISetupGuideCacheService setupGuideCacheService,
        IToolRegistryService toolRegistryService,
        LocalAppPaths appPaths)
    {
        _host = host;
        _setupBootstrapService = setupBootstrapService;
        _setupGuideCacheService = setupGuideCacheService;
        _toolRegistryService = toolRegistryService;
        _appPaths = appPaths;
    }

    public AppText Texts => _host.Texts;

    internal ObservableCollection<SetupGuideCardViewModel> SetupGuideCards { get; } = [];

    internal bool IsSetupGuideOpen
    {
        get => _isSetupGuideOpen;
        private set
        {
            if (SetProperty(ref _isSetupGuideOpen, value))
            {
                OnPropertyChanged(nameof(SetupGuideVisibility));
                RaiseSetupGuideNavigationPropertyChanges();
            }
        }
    }

    internal int SelectedSetupGuideCardIndex
    {
        get => _selectedSetupGuideCardIndex;
        set
        {
            var normalized = SetupGuideCards.Count == 0
                ? 0
                : Math.Clamp(value, 0, SetupGuideCards.Count - 1);

            if (SetProperty(ref _selectedSetupGuideCardIndex, normalized))
            {
                RaiseSetupGuideNavigationPropertyChanges();
            }
        }
    }

    internal bool CanMoveSetupGuidePrevious => SelectedSetupGuideCardIndex > 0;

    internal bool CanMoveSetupGuideNext => SelectedSetupGuideCardIndex < SetupGuideCards.Count - 1;

    internal bool IsOnLastSetupGuideCard => SetupGuideCards.Count == 0 || SelectedSetupGuideCardIndex >= SetupGuideCards.Count - 1;

    internal bool CanAdvanceOrDismissSetupGuide => IsSetupGuideOpen;

    internal string SetupGuideForwardButtonText => IsOnLastSetupGuideCard
        ? Texts.SetupGuideCloseButton
        : Texts.SetupGuideNextButton;

    internal string SetupGuidePositionText => SetupGuideCards.Count == 0
        ? string.Empty
        : $"{SelectedSetupGuideCardIndex + 1} / {SetupGuideCards.Count}";

    internal Microsoft.UI.Xaml.Visibility SetupGuidePositionVisibility => SetupGuideCards.Count == 0
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    internal Microsoft.UI.Xaml.Visibility SetupGuideVisibility => IsSetupGuideOpen ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    internal string SetupGuideSummary
    {
        get
        {
            if (SetupGuideCards.Count == 0)
            {
                return Texts.EnvironmentStatePreparing;
            }

            var items = SetupGuideCards.SelectMany(static card => card.Items).ToList();
            var readyCount = items.Count(static item => item.State == ReadinessState.Ready);
            var avsItem = items.FirstOrDefault(static item => item.Kind == SetupDependencyKind.Avs2PipeMod);
            var avsNote = avsItem?.State == ReadinessState.Ready
                ? Texts.Pick("已检测到 Avs2Pipemod，可继续处理 .avs 输入。", "Avs2Pipemod is available for .avs input.")
                : Texts.Pick("如需 .avs 输入，请额外准备 Avs2Pipemod。", "If you need .avs input, prepare Avs2Pipemod separately.");

            return Texts.Pick(
                $"建议顺序：Python 3.12 -> VapourSynth -> VS 插件 / Python 包 -> 编码器与工具。当前已就绪 {readyCount}/{items.Count} 项。{avsNote}",
                $"Recommended order: Python 3.12 -> VapourSynth -> VS plugins / Python packages -> encoders and tools. {readyCount}/{items.Count} items are ready. {avsNote}");
        }
    }

    internal string EnvironmentCheckedAtText => GetSetupGuideLocalCheckedAtText();

    internal string SetupGuideRemoteCheckedAtText => GetSetupGuideRemoteCheckedAtText();

    internal Microsoft.UI.Xaml.Visibility SetupGuideRemoteCheckedAtVisibility => string.IsNullOrWhiteSpace(SetupGuideRemoteCheckedAtText)
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    internal bool IsRefreshingSetupGuide => _isRefreshingSetupGuide;

    internal bool IsCheckingSetupDependencyUpdates => _isCheckingSetupDependencyUpdates;

    internal Microsoft.UI.Xaml.Visibility SetupGuideActionProgressVisibility => _isRefreshingSetupGuide || _isCheckingSetupDependencyUpdates
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    internal string SetupGuideRefreshActionText => _isRefreshingSetupGuide
        ? Texts.SetupGuideRefreshingStatus
        : Texts.SetupGuideRefreshButton;

    internal string SetupGuideUpdateCheckActionText => _isCheckingSetupDependencyUpdates
        ? Texts.SetupGuideCheckingUpdatesStatus
        : Texts.SetupGuideCheckUpdatesButton;

    internal bool CanExecuteSetupGuideRefreshAction => !_isSetupGuideInstallRunning
        && !_isRefreshingSetupGuide
        && !_isCheckingSetupDependencyUpdates;

    internal bool CanExecuteSetupGuideUpdateCheckAction => !_isSetupGuideInstallRunning
        && !_isRefreshingSetupGuide
        && !_isCheckingSetupDependencyUpdates;

    internal bool IsSetupGuideInstallRunning => _isSetupGuideInstallRunning;

    internal string AppRootPath => _appPaths.RootPath;

    internal string EncodersRootPath => _appPaths.ToolsetRootPath;

    internal string ToolsRootPath => _appPaths.ToolsRootPath;

    internal string SettingsRootPath => _appPaths.SettingsRootPath;

    internal string TemplatesRootPath => _appPaths.WorkspaceTemplatesRootPath;

    internal string LocalizationRootPath => _appPaths.LocalizationRootPath;

    internal string DownloadsRootPath => _appPaths.DownloadsRootPath;

    internal void OpenSetupGuide()
    {
        IsSetupGuideOpen = true;
    }

    internal async Task OpenSetupGuideAsync()
    {
        if (SetupGuideCards.Count > 0 && _setupGuideStatusReport is not null)
        {
            IsSetupGuideOpen = true;
            return;
        }

        if (SetupGuideCards.Count == 0 && _setupGuideStatusReport is not null)
        {
            RefreshSetupGuideCards();
            RaiseSetupGuideStatePropertyChanges();
            IsSetupGuideOpen = true;
            return;
        }

        if (TryRestoreSetupGuideSnapshot())
        {
            IsSetupGuideOpen = true;
            return;
        }

        await RefreshSetupGuideAsync(openWhenFinished: true);
    }

    internal async Task EnsureSetupGuideCardsAsync()
    {
        if (SetupGuideCards.Count > 0 && _setupGuideStatusReport is not null)
        {
            return;
        }

        if (SetupGuideCards.Count == 0 && _setupGuideStatusReport is not null)
        {
            RefreshSetupGuideCards();
            RaiseSetupGuideStatePropertyChanges();
            return;
        }

        if (TryRestoreSetupGuideSnapshot())
        {
            return;
        }

        if (_host.EnvironmentReadinessReport is null)
        {
            await _host.RefreshAsync(includeUpdates: false);
        }

        if (_host.EnvironmentReadinessReport is not null)
        {
            await RefreshSetupGuideStatusAsync(
                includeRemoteMetadata: false,
                statusOverride: null,
                openWhenFinished: false,
                forceEnvironmentScan: false,
                preferCachedSnapshot: false);
        }
    }

    internal void MoveSetupGuidePrevious()
    {
        if (CanMoveSetupGuidePrevious)
        {
            SelectedSetupGuideCardIndex--;
        }
    }

    internal void MoveSetupGuideNext()
    {
        if (CanMoveSetupGuideNext)
        {
            SelectedSetupGuideCardIndex++;
        }
    }

    internal string? AdvanceOrDismissSetupGuide()
    {
        if (CanMoveSetupGuideNext)
        {
            MoveSetupGuideNext();
            return null;
        }

        return DismissSetupGuide();
    }

    internal string? DismissSetupGuide()
    {
        IsSetupGuideOpen = false;

        if (_hasCompletedSetupGuide)
        {
            return null;
        }

        _hasCompletedSetupGuide = true;
        _host.HasCompletedSetupGuide = true;
        return _host.SaveSettings(updateStatusText: false);
    }

    internal async Task RefreshSetupGuideAsync(bool openWhenFinished = false)
    {
        await RefreshSetupGuideStatusAsync(
            includeRemoteMetadata: false,
            statusOverride: Texts.SetupGuideLocalRefreshCompletedStatus,
            openWhenFinished: openWhenFinished,
            forceEnvironmentScan: true,
            preferCachedSnapshot: false);
    }

    internal async Task CheckSetupDependencyUpdatesAsync(bool openWhenFinished = false)
    {
        await RefreshSetupGuideStatusAsync(
            includeRemoteMetadata: true,
            statusOverride: null,
            openWhenFinished: openWhenFinished,
            forceEnvironmentScan: false,
            preferCachedSnapshot: true);
    }

    internal async Task<string?> InstallSetupDependencyAsync(SetupDependencyKind kind)
    {
        var selectedCardIndex = SelectedSetupGuideCardIndex;
        var item = FindSetupGuideDependency(kind);
        if (item is null)
        {
            return Texts.Pick("未找到对应的首启依赖项。", "Setup dependency item was not found.");
        }

        if (_isSetupGuideInstallRunning)
        {
            return Texts.InstallAlreadyRunning;
        }

        _isSetupGuideInstallRunning = true;
        _host.NotifyBusyChanged();
        item.BeginOperation();
        var shouldRefreshSetupGuideStatus = false;
        string? errorMessage = null;

        try
        {
            var progress = new Progress<SetupInstallProgress>(item.ReportProgress);
            await _setupBootstrapService.InstallAsync(kind, progress);
            await _host.RefreshAsync(Texts.Pick("首启依赖操作完成。", "Setup dependency action completed."), includeUpdates: false);
            shouldRefreshSetupGuideStatus = true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            item.FinishOperation();
            _isSetupGuideInstallRunning = false;
            _host.NotifyBusyChanged();
        }

        if (shouldRefreshSetupGuideStatus)
        {
            await RefreshSetupGuideAfterDependencyMutationAsync();
        }

        RestoreSetupGuideCardSelection(selectedCardIndex);
        return errorMessage;
    }

    internal bool RequiresSetupDependencyManualImport(SetupDependencyKind kind)
    {
        return kind is SetupDependencyKind.Avs2PipeMod
            or SetupDependencyKind.DgDemux
            or SetupDependencyKind.Eac3To
            or SetupDependencyKind.Deew
            or SetupDependencyKind.Dee
            or SetupDependencyKind.OpusExt;
    }

    public bool CanManuallySelectSetupDependency(SetupDependencyKind kind)
    {
        return GetManualPathKeys(kind).Count > 0;
    }

    internal bool HasManualPinnedSetupDependency(SetupDependencyKind kind)
    {
        return GetManualPathKeys(kind).Any(key => _host.ManualToolPaths.ContainsKey(key));
    }

    internal string GetSetupDependencyDisplayName(SetupDependencyKind kind)
    {
        return GetSetupDependencyTitle(kind);
    }

    internal string GetSetupDependencyCurrentPath(SetupDependencyKind kind)
    {
        var item = FindSetupGuideDependency(kind);
        if (!string.IsNullOrWhiteSpace(item?.ExecutablePath))
        {
            return item.ExecutablePath;
        }

        var manualPath = GetManualPathKeys(kind)
            .Select(key => _host.ManualToolPaths.TryGetValue(key, out var path) ? path : null)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (!string.IsNullOrWhiteSpace(manualPath))
        {
            return manualPath;
        }

        return kind switch
        {
            SetupDependencyKind.FfmpegBundle => _appPaths.ToolsRootPath,
            SetupDependencyKind.VsPluginBundle or SetupDependencyKind.Awsmfunc or SetupDependencyKind.Vsjetpack => _appPaths.WorkspaceRootPath,
            _ => _appPaths.ToolsRootPath
        };
    }

    internal async Task<string?> PinSetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath)
    {
        var selectedCardIndex = SelectedSetupGuideCardIndex;
        if (!CanManuallySelectSetupDependency(kind))
        {
            return Texts.ManualToolSelectUnsupported;
        }

        if (string.IsNullOrWhiteSpace(sourcePath)
            || !File.Exists(sourcePath)
            || !string.Equals(Path.GetExtension(sourcePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Texts.ManualToolInvalidSelection;
        }

        var normalizedPath = Path.GetFullPath(sourcePath);
        var manualEntries = BuildManualPathEntries(kind, normalizedPath);
        if (manualEntries.Count == 0)
        {
            return Texts.ManualToolInvalidSelection;
        }

        var updatedPaths = new Dictionary<string, string>(_host.ManualToolPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var key in GetManualPathKeys(kind))
        {
            updatedPaths.Remove(key);
        }

        foreach (var entry in manualEntries)
        {
            updatedPaths[entry.Key] = entry.Value;
        }

        _host.ManualToolPaths = updatedPaths;
        var saveError = _host.SaveSettings(updateStatusText: false);
        if (!string.IsNullOrWhiteSpace(saveError))
        {
            return saveError;
        }

        var title = GetSetupDependencyTitle(kind);
        await _host.RefreshAsync(Texts.ManualToolPinnedStatus(title), includeUpdates: false);
        await RefreshSetupGuideAfterDependencyMutationAsync();
        RestoreSetupGuideCardSelection(selectedCardIndex);
        return null;
    }

    internal async Task<string?> ClearManualPinnedSetupDependencyAsync(SetupDependencyKind kind, bool refreshAfterClear = true)
    {
        var selectedCardIndex = SelectedSetupGuideCardIndex;
        var keys = GetManualPathKeys(kind);
        if (keys.Count == 0)
        {
            return Texts.ManualToolSelectUnsupported;
        }

        if (!keys.Any(key => _host.ManualToolPaths.ContainsKey(key)))
        {
            return null;
        }

        var updatedPaths = new Dictionary<string, string>(_host.ManualToolPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            updatedPaths.Remove(key);
        }

        _host.ManualToolPaths = updatedPaths;
        var saveError = _host.SaveSettings(updateStatusText: false);
        if (!string.IsNullOrWhiteSpace(saveError))
        {
            return saveError;
        }

        if (!refreshAfterClear)
        {
            return null;
        }

        var title = GetSetupDependencyTitle(kind);
        await _host.RefreshAsync(Texts.ManualToolPinClearedStatus(title), includeUpdates: false);
        await RefreshSetupGuideAfterDependencyMutationAsync();
        RestoreSetupGuideCardSelection(selectedCardIndex);
        return null;
    }

    internal async Task<string?> ImportSetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath)
    {
        var selectedCardIndex = SelectedSetupGuideCardIndex;
        if (!RequiresSetupDependencyManualImport(kind))
        {
            return Texts.Pick("当前依赖不支持本地导入。", "This dependency does not support local import.");
        }

        var item = FindSetupGuideDependency(kind);
        if (item is null)
        {
            return Texts.Pick("未找到对应的首启依赖项。", "Setup dependency item was not found.");
        }

        if (_isSetupGuideInstallRunning)
        {
            return Texts.InstallAlreadyRunning;
        }

        _isSetupGuideInstallRunning = true;
        _host.NotifyBusyChanged();
        item.BeginOperation();
        var shouldRefreshSetupGuideStatus = false;
        string? errorMessage = null;

        try
        {
            item.ReportProgress(new SetupInstallProgress(kind, 15, Texts.Pick("正在导入本地文件...", "Importing local file..."), false));
            await CopySetupDependencyBinaryAsync(kind, sourcePath);
            item.ReportProgress(new SetupInstallProgress(kind, 100, Texts.Pick("本地文件已导入。", "Local file imported."), false));

            await _host.RefreshAsync(Texts.SetupDependencyImportedStatus(item.Title), includeUpdates: false);
            shouldRefreshSetupGuideStatus = true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            item.FinishOperation();
            _isSetupGuideInstallRunning = false;
            _host.NotifyBusyChanged();
        }

        if (shouldRefreshSetupGuideStatus)
        {
            await RefreshSetupGuideAfterDependencyMutationAsync();
        }

        RestoreSetupGuideCardSelection(selectedCardIndex);
        return errorMessage;
    }

    internal async Task<string?> UninstallSetupDependencyAsync(SetupDependencyKind kind)
    {
        var selectedCardIndex = SelectedSetupGuideCardIndex;
        var item = FindSetupGuideDependency(kind);
        if (item is null)
        {
            return Texts.Pick("未找到对应的首启依赖项。", "Setup dependency item was not found.");
        }

        if (_isSetupGuideInstallRunning)
        {
            return Texts.InstallAlreadyRunning;
        }

        _isSetupGuideInstallRunning = true;
        _host.NotifyBusyChanged();
        item.BeginOperation();
        var shouldRefreshSetupGuideStatus = false;
        string? errorMessage = null;

        try
        {
            var progress = new Progress<SetupInstallProgress>(item.ReportProgress);
            await _setupBootstrapService.UninstallAsync(kind, progress);
            await _host.RefreshAsync(Texts.SetupDependencyUninstalledStatus(item.Title), includeUpdates: false);
            shouldRefreshSetupGuideStatus = true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            item.FinishOperation();
            _isSetupGuideInstallRunning = false;
            _host.NotifyBusyChanged();
        }

        if (shouldRefreshSetupGuideStatus)
        {
            await RefreshSetupGuideAfterDependencyMutationAsync();
        }

        RestoreSetupGuideCardSelection(selectedCardIndex);
        return errorMessage;
    }

    internal void ApplyEnvironmentReadiness(EnvironmentReadinessReport report)
    {
        _setupGuideLocalCheckedAt = report.CheckedAt;
        RaiseSetupGuidePropertyChanges();
    }

    internal bool HasSetupGuideStatusReport => _setupGuideStatusReport is not null;

    internal SetupDependencyStatus ResolveSetupStatus(SetupDependencyKind kind)
    {
        return _setupGuideStatusReport?.Dependencies.FirstOrDefault(item => item.Kind == kind)
            ?? BuildFallbackSetupStatus(kind);
    }

    internal bool TryRestoreCachedSnapshot()
    {
        return TryRestoreSetupGuideSnapshot();
    }

    private Task RefreshSetupGuideAfterDependencyMutationAsync()
    {
        return RefreshSetupGuideStatusAsync(
            includeRemoteMetadata: false,
            statusOverride: null,
            openWhenFinished: false,
            forceEnvironmentScan: false,
            preferCachedSnapshot: false);
    }

    private async Task RefreshSetupGuideStatusAsync(
        bool includeRemoteMetadata,
        string? statusOverride,
        bool openWhenFinished,
        bool forceEnvironmentScan,
        bool preferCachedSnapshot)
    {
        if (_isSetupGuideInstallRunning
            || _isRefreshingSetupGuide
            || _isCheckingSetupDependencyUpdates)
        {
            return;
        }

        var shouldReopen = IsSetupGuideOpen || openWhenFinished;
        var cachedSnapshot = preferCachedSnapshot ? _setupGuideCacheService.Load() : null;
        var cachedReport = cachedSnapshot?.StatusReport is null
            ? null
            : RestoreSetupGuideStatusReport(cachedSnapshot.StatusReport);
        var previousReport = _setupGuideStatusReport ?? cachedReport;
        var previousRemoteCheckedAt = _setupGuideRemoteCheckedAt ?? cachedSnapshot?.RemoteCheckedAt;

        if (includeRemoteMetadata)
        {
            _isCheckingSetupDependencyUpdates = true;
        }
        else
        {
            _isRefreshingSetupGuide = true;
        }

        RaiseSetupGuideStatePropertyChanges();

        try
        {
            if (preferCachedSnapshot && SetupGuideCards.Count == 0 && _setupGuideStatusReport is null)
            {
                TryRestoreSetupGuideSnapshot();
            }

            if (forceEnvironmentScan || _host.EnvironmentReadinessReport is null)
            {
                await _host.RefreshAsync(includeUpdates: false);
            }

            if (_host.EnvironmentReadinessReport is null)
            {
                if (!TryRestoreSetupGuideSnapshot())
                {
                    _setupGuideStatusReport = null;
                    RefreshSetupGuideCards();
                }

                return;
            }

            _setupGuideStatusReport = includeRemoteMetadata
                ? await _setupBootstrapService.GetStatusReportAsync(_host.EnvironmentReadinessReport)
                : await _setupBootstrapService.GetLocalStatusReportAsync(_host.EnvironmentReadinessReport, previousReport);

            if (includeRemoteMetadata)
            {
                _setupGuideRemoteCheckedAt = _setupGuideStatusReport.CheckedAt;
            }
            else if (previousRemoteCheckedAt.HasValue)
            {
                _setupGuideRemoteCheckedAt = previousRemoteCheckedAt;
            }

            PersistSetupGuideSnapshot();
            RefreshSetupGuideCards();

            if (statusOverride is not null)
            {
                _host.StatusText = statusOverride;
            }
            else if (includeRemoteMetadata)
            {
                _host.StatusText = BuildSetupDependencyUpdateStatus(_setupGuideStatusReport);
            }
        }
        catch (Exception ex)
        {
            _host.StatusText = includeRemoteMetadata
                ? Texts.UpdatesCheckFailedStatus(ex.Message)
                : Texts.RefreshFailedStatus(ex.Message);
        }
        finally
        {
            IsSetupGuideOpen = shouldReopen;

            if (includeRemoteMetadata)
            {
                _isCheckingSetupDependencyUpdates = false;
            }
            else
            {
                _isRefreshingSetupGuide = false;
            }

            RaiseSetupGuideStatePropertyChanges();
        }
    }

    private void RefreshSetupGuideCards()
    {
        var selectedCardIndex = SelectedSetupGuideCardIndex;

        if (_host.EnvironmentReadinessReport is null && _setupGuideStatusReport is null)
        {
            ReplaceItems(SetupGuideCards, Array.Empty<SetupGuideCardViewModel>());
            RestoreSetupGuideCardSelection(selectedCardIndex);
            RaiseSetupGuideNavigationPropertyChanges();
            OnPropertyChanged(nameof(SetupGuideSummary));
            return;
        }

        var cards = new[]
        {
            new SetupGuideCardViewModel(
                Texts.Pick("Python", "Python"),
                Texts.Pick("首个必须项。后续所有 Python 侧安装动作都依赖这里。", "The first required step. All Python-side installs depend on this card."),
                Texts.Pick("默认目标固定为官方 Windows x64 的 Python 3.12.10。只要存在任意 3.12.x，就视为已安装。", "The default target is the official Windows x64 Python 3.12.10. Any 3.12.x install counts as ready."),
                [BuildSetupDependencyItem(SetupDependencyKind.Python312)]),
            new SetupGuideCardViewModel(
                Texts.Pick("VapourSynth", "VapourSynth"),
                Texts.Pick("Python 准备好之后，这一张卡片负责 VS 运行时和脚本依赖。", "After Python is ready, this card covers the VS runtime and script-side dependencies."),
                Texts.Pick("Python 3.12 是自动安装这张卡片里所有依赖的前置条件。", "Python 3.12 is required before automatic install is enabled for this card."),
                [
                    BuildSetupDependencyItem(SetupDependencyKind.VapourSynth),
                    BuildSetupDependencyItem(SetupDependencyKind.Vsrepo),
                    BuildSetupDependencyItem(SetupDependencyKind.VsPluginBundle),
                    BuildSetupDependencyItem(SetupDependencyKind.Awsmfunc),
                    BuildSetupDependencyItem(SetupDependencyKind.Vsjetpack)
                ]),
            new SetupGuideCardViewModel(
                Texts.Pick("编码器 / FFmpeg", "Encoders / FFmpeg"),
                Texts.Pick("这些是视频工作流最常用的核心二进制。FFmpeg 与 Av1an 支持自动安装。", "These are the core binaries used most often in the video workflow. FFmpeg and Av1an support automatic install."),
                Texts.Pick("本地编码器放入工作目录下的 encoders，命令行工具放入 tools。", "Managed encoders go into the workspace encoders folder, and CLI tools go into tools."),
                [
                    BuildSetupDependencyItem(SetupDependencyKind.FfmpegBundle),
                    BuildSetupDependencyItem(SetupDependencyKind.X264),
                    BuildSetupDependencyItem(SetupDependencyKind.X265),
                    BuildSetupDependencyItem(SetupDependencyKind.SvtAv1),
                    BuildSetupDependencyItem(SetupDependencyKind.Av1an)
                ]),
            new SetupGuideCardViewModel(
                Texts.Pick("解复用 / 其他依赖", "Demux / Other Dependencies"),
                Texts.Pick("这里保留蓝光解复用、AviSynth 兼容链和音频相关工具。大多需要手动准备。", "This card keeps Blu-ray demux, the AviSynth compatibility chain, and audio tools. Most are manual dependencies."),
                Texts.Pick("DGDemux 为蓝光解复用默认后端；eac3to 为可选后端。这些本地工具会托管在当前用户工作目录的分类文件夹中。", "DGDemux is the default Blu-ray demux backend, and eac3to is the optional backend. These local tools are managed inside categorized folders under the current user's workspace."),
                [
                    BuildSetupDependencyItem(SetupDependencyKind.Avs2PipeMod),
                    BuildSetupDependencyItem(SetupDependencyKind.DgDemux),
                    BuildSetupDependencyItem(SetupDependencyKind.Eac3To),
                    BuildSetupDependencyItem(SetupDependencyKind.Deew),
                    BuildSetupDependencyItem(SetupDependencyKind.Dee),
                    BuildSetupDependencyItem(SetupDependencyKind.OpusExt)
                ])
        };

        ReplaceItems(SetupGuideCards, cards);
        RestoreSetupGuideCardSelection(selectedCardIndex);
        RaiseSetupGuideNavigationPropertyChanges();
        OnPropertyChanged(nameof(SetupGuideSummary));
    }

    private void RestoreSetupGuideCardSelection(int selectedCardIndex)
    {
        SelectedSetupGuideCardIndex = Math.Min(selectedCardIndex, Math.Max(SetupGuideCards.Count - 1, 0));
    }

    private void RaiseSetupGuidePropertyChanges()
    {
        if (_hasCompletedSetupGuide && SetupGuideCards.Count == 0 && !IsSetupGuideOpen)
        {
            RaiseSetupGuideStatePropertyChanges();
            return;
        }

        RefreshSetupGuideCards();
        RaiseSetupGuideStatePropertyChanges();
    }

    private void RaiseSetupGuideNavigationPropertyChanges()
    {
        OnPropertyChanged(nameof(CanMoveSetupGuidePrevious));
        OnPropertyChanged(nameof(CanMoveSetupGuideNext));
        OnPropertyChanged(nameof(IsOnLastSetupGuideCard));
        OnPropertyChanged(nameof(CanAdvanceOrDismissSetupGuide));
        OnPropertyChanged(nameof(SetupGuideForwardButtonText));
        OnPropertyChanged(nameof(SetupGuidePositionText));
        OnPropertyChanged(nameof(SetupGuidePositionVisibility));
    }

    private void RaiseSetupGuideStatePropertyChanges()
    {
        OnPropertyChanged(nameof(EnvironmentCheckedAtText));
        OnPropertyChanged(nameof(SetupGuideRemoteCheckedAtText));
        OnPropertyChanged(nameof(SetupGuideRemoteCheckedAtVisibility));
        OnPropertyChanged(nameof(IsRefreshingSetupGuide));
        OnPropertyChanged(nameof(IsCheckingSetupDependencyUpdates));
        OnPropertyChanged(nameof(SetupGuideActionProgressVisibility));
        OnPropertyChanged(nameof(SetupGuideRefreshActionText));
        OnPropertyChanged(nameof(SetupGuideUpdateCheckActionText));
        OnPropertyChanged(nameof(CanExecuteSetupGuideRefreshAction));
        OnPropertyChanged(nameof(CanExecuteSetupGuideUpdateCheckAction));
    }

    private bool TryRestoreSetupGuideSnapshot()
    {
        var snapshot = _setupGuideCacheService.Load();
        if (snapshot?.StatusReport is null)
        {
            return false;
        }

        _setupGuideLocalCheckedAt = snapshot.LocalCheckedAt;
        _setupGuideRemoteCheckedAt = snapshot.RemoteCheckedAt;
        _setupGuideStatusReport = RestoreSetupGuideStatusReport(snapshot.StatusReport);

        RefreshSetupGuideCards();
        RaiseSetupGuideStatePropertyChanges();
        return SetupGuideCards.Count > 0;
    }

    private void PersistSetupGuideSnapshot()
    {
        if (_setupGuideStatusReport is null)
        {
            _setupGuideCacheService.Clear();
            return;
        }

        _setupGuideCacheService.Save(new SetupGuideCacheSnapshot(
            SetupGuideCacheSnapshot.CurrentSchemaVersion,
            DateTimeOffset.Now,
            _setupGuideLocalCheckedAt,
            _setupGuideRemoteCheckedAt,
            BuildSetupGuideCacheStatusReport(_setupGuideStatusReport)));
    }

    private SetupDependencyStatusReport RestoreSetupGuideStatusReport(SetupGuideCacheStatusReport report)
    {
        return new SetupDependencyStatusReport(
            report.CheckedAt,
            report.Dependencies
                .Select(dependency => new SetupDependencyStatus(
                    dependency.Kind,
                    dependency.State,
                    dependency.InstalledVersion,
                    dependency.LatestVersion,
                    dependency.UpdateAvailable,
                    dependency.ExecutablePath,
                    GetCurrentSetupDependencyReleaseUrl(dependency.Kind),
                    dependency.IsInstallSupported,
                    dependency.IsInstallEnabled,
                    dependency.Detail))
                .ToArray());
    }

    private SetupGuideCacheStatusReport BuildSetupGuideCacheStatusReport(SetupDependencyStatusReport report)
    {
        return new SetupGuideCacheStatusReport(
            report.CheckedAt,
            report.Dependencies
                .Select(dependency => new SetupGuideCacheDependencyStatus(
                    dependency.Kind,
                    dependency.State,
                    dependency.InstalledVersion,
                    dependency.LatestVersion,
                    dependency.UpdateAvailable,
                    dependency.ExecutablePath,
                    dependency.IsInstallSupported,
                    dependency.IsInstallEnabled,
                    dependency.Detail))
                .ToArray());
    }

    private string GetCurrentSetupDependencyReleaseUrl(SetupDependencyKind kind, string fallbackUrl = "")
    {
        try
        {
            var toolKind = kind switch
            {
                SetupDependencyKind.Python312 => RegisteredToolKind.Python,
                SetupDependencyKind.VapourSynth => RegisteredToolKind.Vspipe,
                SetupDependencyKind.Vsrepo => RegisteredToolKind.Vsrepo,
                SetupDependencyKind.VsPluginBundle => RegisteredToolKind.Vsrepo,
                SetupDependencyKind.Awsmfunc => RegisteredToolKind.PythonModuleAwsmfunc,
                SetupDependencyKind.Vsjetpack => RegisteredToolKind.PythonModuleVsjetpack,
                SetupDependencyKind.FfmpegBundle => RegisteredToolKind.Ffmpeg,
                SetupDependencyKind.X264 => RegisteredToolKind.X264,
                SetupDependencyKind.X265 => RegisteredToolKind.X265,
                SetupDependencyKind.SvtAv1 => RegisteredToolKind.SvtAv1,
                SetupDependencyKind.Av1an => RegisteredToolKind.Av1an,
                SetupDependencyKind.Avs2PipeMod => RegisteredToolKind.Avs2PipeMod,
                SetupDependencyKind.DgDemux => RegisteredToolKind.DgDemux,
                SetupDependencyKind.Eac3To => RegisteredToolKind.Eac3To,
                SetupDependencyKind.Deew => RegisteredToolKind.Deew,
                SetupDependencyKind.Dee => RegisteredToolKind.Dee,
                SetupDependencyKind.OpusExt => RegisteredToolKind.OpusExt,
                _ => (RegisteredToolKind?)null
            };

            if (!toolKind.HasValue)
            {
                return fallbackUrl;
            }

            var releaseUrl = _toolRegistryService.GetTool(toolKind.Value).ReleaseUrl;
            return string.IsNullOrWhiteSpace(releaseUrl)
                ? fallbackUrl
                : releaseUrl;
        }
        catch
        {
            return fallbackUrl;
        }
    }

    private string GetSetupGuideLocalCheckedAtText()
    {
        return _setupGuideLocalCheckedAt.HasValue
            ? Texts.SetupGuideLocalCheckedAtLabel(_setupGuideLocalCheckedAt.Value)
            : Texts.SetupGuideLocalCheckIdleStatus;
    }

    private string GetSetupGuideRemoteCheckedAtText()
    {
        return _setupGuideRemoteCheckedAt.HasValue
            ? Texts.SetupGuideRemoteCheckedAtLabel(_setupGuideRemoteCheckedAt.Value)
            : Texts.SetupGuideRemoteCheckIdleStatus;
    }

    private string BuildSetupDependencyUpdateStatus(SetupDependencyStatusReport report)
    {
        var updateCount = report.Dependencies.Count(status => HasSetupDependencyUpdate(status.Kind, status));
        return updateCount > 0
            ? Texts.SetupDependencyUpdatesFoundStatus(updateCount)
            : Texts.NoSetupDependencyUpdatesStatus;
    }

    private SetupGuideDependencyItemViewModel BuildSetupDependencyItem(SetupDependencyKind kind)
    {
        var status = ResolveSetupStatus(kind);
        var title = GetSetupDependencyTitle(kind);
        var description = GetSetupDependencyDescription(kind);
        var isInstalled = IsSetupDependencyInstalled(kind, status);
        var hasUpdateAvailable = HasSetupDependencyUpdate(kind, status);
        var canUninstall = CanUninstallSetupDependency(kind, status, isInstalled);
        var isManualPinned = HasManualPinnedSetupDependency(kind);
        var statusText = BuildSetupStatusText(kind, status);
        var warningText = BuildSetupWarningText(kind, status, isInstalled, canUninstall, hasUpdateAvailable, isManualPinned);
        var primaryActionText = GetSetupPrimaryActionText(kind, isInstalled, hasUpdateAvailable);

        return new SetupGuideDependencyItemViewModel(
            kind,
            title,
            description,
            status.State,
            Texts.ReadinessStateLabel(status.State),
            statusText,
            string.IsNullOrWhiteSpace(status.InstalledVersion)
                ? string.Empty
                : Texts.Pick($"已装：{status.InstalledVersion}", $"Installed: {status.InstalledVersion}"),
            string.IsNullOrWhiteSpace(status.LatestVersion)
                ? string.Empty
                : Texts.Pick($"最新：{status.LatestVersion}", $"Latest: {status.LatestVersion}"),
            warningText,
            status.Detail,
            status.ExecutablePath,
            status.ReleaseUrl,
            primaryActionText,
            ShouldShowSetupPrimaryAction(status, isInstalled, hasUpdateAvailable),
            IsSetupPrimaryActionEnabled(kind, status, isInstalled),
            isInstalled,
            canUninstall,
            CanManuallySelectSetupDependency(kind),
            isManualPinned);
    }

    private SetupGuideDependencyItemViewModel? FindSetupGuideDependency(SetupDependencyKind kind)
    {
        return SetupGuideCards
            .SelectMany(static card => card.Items)
            .FirstOrDefault(item => item.Kind == kind);
    }

    private SetupDependencyStatus ResolveSetupStatus(SetupDependencyKind kind)
    {
        return _setupGuideStatusReport?.Dependencies.FirstOrDefault(item => item.Kind == kind)
            ?? BuildFallbackSetupStatus(kind);
    }

    private SetupDependencyStatus BuildFallbackSetupStatus(SetupDependencyKind kind)
    {
        var python = GetToolResult(RegisteredToolKind.Python);
        var pythonReady = python.State == ReadinessState.Ready && python.DetectedVersion.Contains("3.12", StringComparison.OrdinalIgnoreCase);

        return kind switch
        {
            SetupDependencyKind.Python312 => new SetupDependencyStatus(
                kind,
                pythonReady ? ReadinessState.Ready : python.State == ReadinessState.Ready ? ReadinessState.Partial : python.State,
                python.DetectedVersion,
                pythonReady ? string.Empty : "3.12.10",
                false,
                python.ExecutablePath,
                python.ReleaseUrl,
                true,
                true,
                BuildToolProbeDetail(python)),
            SetupDependencyKind.VapourSynth => BuildFallbackToolStatus(kind, RegisteredToolKind.Vspipe, pythonReady),
            SetupDependencyKind.Vsrepo => BuildFallbackToolStatus(kind, RegisteredToolKind.Vsrepo, pythonReady),
            SetupDependencyKind.VsPluginBundle => new SetupDependencyStatus(
                kind,
                GetCapabilityReadiness(EnvironmentCapabilityKind.VapourSynthPluginStack).State,
                $"{GetCapabilityReadiness(EnvironmentCapabilityKind.VapourSynthPluginStack).SatisfiedRequirementCount}/{GetCapabilityReadiness(EnvironmentCapabilityKind.VapourSynthPluginStack).TotalRequirementCount}",
                string.Empty,
                false,
                string.Empty,
                GetToolResult(RegisteredToolKind.Vsrepo).ReleaseUrl,
                true,
                pythonReady,
                string.Join(Environment.NewLine, GetCapabilityReadiness(EnvironmentCapabilityKind.VapourSynthPluginStack).Requirements.Select(BuildRequirementDetail))),
            SetupDependencyKind.Awsmfunc => BuildFallbackToolStatus(kind, RegisteredToolKind.PythonModuleAwsmfunc, pythonReady),
            SetupDependencyKind.Vsjetpack => BuildFallbackToolStatus(kind, RegisteredToolKind.PythonModuleVsjetpack, pythonReady),
            SetupDependencyKind.FfmpegBundle => new SetupDependencyStatus(
                kind,
                ResolveCompositeSetupState(GetToolResult(RegisteredToolKind.Ffmpeg), GetToolResult(RegisteredToolKind.Ffprobe)),
                GetToolResult(RegisteredToolKind.Ffmpeg).DetectedVersion,
                string.Empty,
                false,
                GetToolResult(RegisteredToolKind.Ffmpeg).ExecutablePath,
                GetToolResult(RegisteredToolKind.Ffmpeg).ReleaseUrl,
                true,
                true,
                string.Join(Environment.NewLine, [
                    $"ffmpeg: {BuildToolProbeDetail(GetToolResult(RegisteredToolKind.Ffmpeg))}",
                    $"ffprobe: {BuildToolProbeDetail(GetToolResult(RegisteredToolKind.Ffprobe))}"
                ])),
            SetupDependencyKind.X264 => BuildFallbackToolStatus(kind, RegisteredToolKind.X264, true),
            SetupDependencyKind.X265 => BuildFallbackToolStatus(kind, RegisteredToolKind.X265, true),
            SetupDependencyKind.SvtAv1 => BuildFallbackToolStatus(kind, RegisteredToolKind.SvtAv1, true),
            SetupDependencyKind.Av1an => BuildFallbackToolStatus(kind, RegisteredToolKind.Av1an, true),
            SetupDependencyKind.Avs2PipeMod => BuildFallbackToolStatus(kind, RegisteredToolKind.Avs2PipeMod, true, installSupported: false),
            SetupDependencyKind.DgDemux => BuildFallbackToolStatus(kind, RegisteredToolKind.DgDemux, true, installSupported: false),
            SetupDependencyKind.Eac3To => BuildFallbackToolStatus(kind, RegisteredToolKind.Eac3To, true, installSupported: false),
            SetupDependencyKind.Deew => BuildFallbackToolStatus(kind, RegisteredToolKind.Deew, true, installSupported: false),
            SetupDependencyKind.Dee => BuildFallbackToolStatus(kind, RegisteredToolKind.Dee, true, installSupported: false),
            SetupDependencyKind.OpusExt => BuildFallbackToolStatus(kind, RegisteredToolKind.OpusExt, true, installSupported: false),
            _ => new SetupDependencyStatus(kind, ReadinessState.Unknown, string.Empty, string.Empty, false, string.Empty, string.Empty, false, false, string.Empty)
        };
    }

    private SetupDependencyStatus BuildFallbackToolStatus(
        SetupDependencyKind dependencyKind,
        RegisteredToolKind toolKind,
        bool isInstallEnabled,
        bool installSupported = true)
    {
        var result = GetToolResult(toolKind);
        return new SetupDependencyStatus(
            dependencyKind,
            result.State,
            result.DetectedVersion,
            string.Empty,
            false,
            result.ExecutablePath,
            result.ReleaseUrl,
            installSupported,
            isInstallEnabled,
            BuildToolProbeDetail(result));
    }

    private string GetSetupDependencyTitle(SetupDependencyKind kind)
    {
        return kind switch
        {
            SetupDependencyKind.Python312 => "Python 3.12",
            SetupDependencyKind.VapourSynth => "VapourSynth",
            SetupDependencyKind.Vsrepo => "vsrepo",
            SetupDependencyKind.VsPluginBundle => Texts.Pick("VS 插件包", "VS Plugin Bundle"),
            SetupDependencyKind.Awsmfunc => "awsmfunc",
            SetupDependencyKind.Vsjetpack => "vsjetpack",
            SetupDependencyKind.FfmpegBundle => "FFmpeg / FFprobe",
            SetupDependencyKind.X264 => "x264",
            SetupDependencyKind.X265 => "x265",
            SetupDependencyKind.SvtAv1 => "SVT-AV1",
            SetupDependencyKind.Av1an => "Av1an",
            SetupDependencyKind.Avs2PipeMod => "Avs2Pipemod",
            SetupDependencyKind.DgDemux => "DGDemux",
            SetupDependencyKind.Eac3To => "eac3to",
            SetupDependencyKind.Deew => "deew",
            SetupDependencyKind.Dee => "DEE",
            SetupDependencyKind.OpusExt => Texts.Pick("Opus 编码器", "Opus Encoder"),
            _ => kind.ToString()
        };
    }

    private string GetSetupDependencyDescription(SetupDependencyKind kind)
    {
        return kind switch
        {
            SetupDependencyKind.Python312 => Texts.Pick("官方 Windows x64 安装器；首启自动安装的基线运行时。", "Official Windows x64 installer; the baseline runtime for guided installs."),
            SetupDependencyKind.VapourSynth => Texts.Pick("主视频脚本运行时，负责 .vpy 输入和 vspipe。", "Primary script runtime for .vpy input and vspipe."),
            SetupDependencyKind.Vsrepo => Texts.Pick("VS 插件安装器与包清单工具。", "VS package installer and manifest tool."),
            SetupDependencyKind.VsPluginBundle => Texts.Pick("ffms2、fpng、libp2p、lsmas、placebo、mvsfunc、havsfunc。", "ffms2, fpng, libp2p, lsmas, placebo, mvsfunc, and havsfunc."),
            SetupDependencyKind.Awsmfunc => Texts.Pick("常见脚本环境会用到的 Python 包。", "Python package used by common script stacks."),
            SetupDependencyKind.Vsjetpack => Texts.Pick("JET 系 VS 依赖合集。", "JET-style VS dependency bundle."),
            SetupDependencyKind.FfmpegBundle => Texts.Pick("媒体探测、封装和解码的基础工具。", "Core tools for probing, muxing, and decoding."),
            SetupDependencyKind.X264 => Texts.Pick("H.264 编码器。", "H.264 encoder."),
            SetupDependencyKind.X265 => Texts.Pick("HEVC 编码器。", "HEVC encoder."),
            SetupDependencyKind.SvtAv1 => Texts.Pick("AV1 编码器。", "AV1 encoder."),
            SetupDependencyKind.Av1an => Texts.Pick("自动压制流程依赖。", "Required for the auto-encode workflow."),
            SetupDependencyKind.Avs2PipeMod => Texts.Pick("AviSynth 输入桥接工具。", "Bridge tool for AviSynth input."),
            SetupDependencyKind.DgDemux => Texts.Pick("蓝光播放列表扫描与解复用默认后端。", "Default backend for Blu-ray playlist scanning and demux."),
            SetupDependencyKind.Eac3To => Texts.Pick("音频扫描与 FLAC 转换。", "Audio scanning and FLAC conversion."),
            SetupDependencyKind.Deew => Texts.Pick("多声道转 DDP。", "Multichannel audio to DDP."),
            SetupDependencyKind.Dee => Texts.Pick("deew 的本地 DEE 依赖。", "Local DEE dependency for deew."),
            SetupDependencyKind.OpusExt => Texts.Pick("Opus 命令行编码器。", "Command-line Opus encoder."),
            _ => string.Empty
        };
    }

    private string BuildSetupStatusText(SetupDependencyKind kind, SetupDependencyStatus status)
    {
        return kind switch
        {
            SetupDependencyKind.Python312 => BuildPythonStatusText(status),
            SetupDependencyKind.VapourSynth => BuildPythonDependentStatusText(status,
                Texts.Pick("已检测到可用的 VapourSynth / vspipe。", "A usable VapourSynth / vspipe runtime was detected."),
                Texts.Pick("未检测到 VapourSynth 运行时。", "VapourSynth runtime was not detected.")),
            SetupDependencyKind.Vsrepo => BuildPythonDependentStatusText(status,
                Texts.Pick("已检测到 vsrepo。", "vsrepo was detected."),
                Texts.Pick("未检测到 vsrepo。", "vsrepo was not detected.")),
            SetupDependencyKind.VsPluginBundle => BuildPythonDependentStatusText(status,
                Texts.Pick("必需的 VS 插件包已基本齐备。", "The required VS plugin bundle is basically ready."),
                Texts.Pick("这一步不是可选项，安装 Python 和 VapourSynth 后还必须继续补齐。", "This is required, not optional. You still need to complete it after Python and VapourSynth.")),
            SetupDependencyKind.Awsmfunc => BuildPythonDependentStatusText(status,
                Texts.Pick("已检测到 awsmfunc。", "awsmfunc was detected."),
                Texts.Pick("未检测到 awsmfunc。", "awsmfunc was not detected.")),
            SetupDependencyKind.Vsjetpack => BuildPythonDependentStatusText(status,
                Texts.Pick("已检测到 vsjetpack。", "vsjetpack was detected."),
                Texts.Pick("未检测到 vsjetpack。", "vsjetpack was not detected.")),
            SetupDependencyKind.FfmpegBundle => BuildGenericSetupStatusText(status,
                Texts.Pick("FFmpeg / FFprobe 已就绪。", "FFmpeg / FFprobe are ready."),
                Texts.Pick("FFmpeg / FFprobe 缺失。", "FFmpeg / FFprobe are missing.")),
            SetupDependencyKind.X264 or SetupDependencyKind.X265 or SetupDependencyKind.SvtAv1 or SetupDependencyKind.Av1an or SetupDependencyKind.Avs2PipeMod or SetupDependencyKind.DgDemux or SetupDependencyKind.Eac3To or SetupDependencyKind.Deew or SetupDependencyKind.Dee or SetupDependencyKind.OpusExt
                => BuildGenericSetupStatusText(status,
                    Texts.Pick($"已检测到 {GetSetupDependencyTitle(kind)}。", $"{GetSetupDependencyTitle(kind)} was detected."),
                    Texts.Pick($"未检测到 {GetSetupDependencyTitle(kind)}。", $"{GetSetupDependencyTitle(kind)} was not detected.")),
            _ => string.Empty
        };
    }

    private string BuildPythonStatusText(SetupDependencyStatus status)
    {
        if (status.State == ReadinessState.Ready)
        {
            return Texts.Pick(
                "已检测到 Python 3.12，可继续安装 VapourSynth 和后续 Python 侧依赖。",
                "Python 3.12 was detected. You can continue with VapourSynth and the remaining Python-side dependencies.");
        }

        if (status.State == ReadinessState.Partial)
        {
            return Texts.Pick(
                "检测到了更高版本的 Python。当前环境可能可用，但首启引导仍以 3.12 为目标。",
                "A higher Python version was detected. The current environment may still work, but this guide still targets 3.12.");
        }

        if (!string.IsNullOrWhiteSpace(status.InstalledVersion))
        {
            return Texts.Pick(
                $"当前检测到的 Python 版本为 {status.InstalledVersion}，引导目标仍为 3.12。",
                $"The detected Python version is {status.InstalledVersion}, but the guide target remains 3.12.");
        }

        return Texts.Pick(
            "未检测到 Python 3.12。建议先安装官方 Windows x64 版本。",
            "Python 3.12 was not detected. Install the official Windows x64 build first.");
    }

    private string BuildPythonDependentStatusText(SetupDependencyStatus status, string readyText, string missingText)
    {
        if (status.State == ReadinessState.Ready)
        {
            return readyText;
        }

        if (!status.IsInstallEnabled && status.IsInstallSupported)
        {
            return Texts.Pick(
                "需先安装 Python 3.12，当前项的自动安装才会启用。",
                "Install Python 3.12 first before automatic install is enabled for this item.");
        }

        return missingText;
    }

    private string BuildGenericSetupStatusText(SetupDependencyStatus status, string readyText, string missingText)
    {
        return status.State switch
        {
            ReadinessState.Ready => readyText,
            ReadinessState.Partial => Texts.Pick("已检测到部分依赖，建议继续补齐。", "Part of the dependency was detected. Complete the remaining pieces."),
            ReadinessState.Misconfigured => Texts.Pick("检测到文件，但当前无法稳定调用。", "The files were detected, but the current invocation is not stable."),
            _ => missingText
        };
    }

    private string BuildSetupWarningText(
        SetupDependencyKind kind,
        SetupDependencyStatus status,
        bool isInstalled,
        bool canUninstall,
        bool hasUpdateAvailable,
        bool isManualPinned)
    {
        var warnings = new List<string>();
        if (isManualPinned)
        {
            warnings.Add(Texts.ManualToolPinnedStatus(GetSetupDependencyTitle(kind)));
        }

        if (kind == SetupDependencyKind.Python312 && status.State == ReadinessState.Partial)
        {
            warnings.Add(Texts.Pick(
                "检测到更高版本的 Python，可能与当前引导目标存在兼容性差异。你可以保持现状，也可以并行安装 3.12.10。",
                "A higher Python version was detected and may differ from the current guide target. You can keep the current setup or install 3.12.10 in parallel."));
        }

        if (hasUpdateAvailable && !string.IsNullOrWhiteSpace(status.LatestVersion))
        {
            warnings.Add(Texts.Pick(
                $"检测到可用新版本：{status.LatestVersion}",
                $"A newer version is available: {status.LatestVersion}"));
        }

        if (status.IsInstallSupported && !status.IsInstallEnabled)
        {
            warnings.Add(Texts.Pick(
                "自动安装按钮已保留，但需要先准备 Python 3.12。",
                "The install button is kept visible, but Python 3.12 must be ready first."));
        }

        if (isInstalled && !canUninstall && HasLocalManagedUninstallScope(kind))
        {
            warnings.Add(Texts.SetupDependencyExternalLocationWarning);
        }

        return string.Join(Environment.NewLine, warnings);
    }

    private string GetSetupPrimaryActionText(
        SetupDependencyKind kind,
        bool isInstalled,
        bool hasUpdateAvailable)
    {
        if (kind == SetupDependencyKind.Python312)
        {
            return isInstalled
                ? Texts.Pick("更新 3.12.10", "Update 3.12.10")
                : Texts.Pick("安装 3.12.10", "Install 3.12.10");
        }

        if (RequiresSetupDependencyManualImport(kind))
        {
            return Texts.ImportButton;
        }

        return isInstalled || hasUpdateAvailable
            ? Texts.UpdateButton
            : Texts.InstallButton;
    }

    private bool IsSetupPrimaryActionEnabled(
        SetupDependencyKind kind,
        SetupDependencyStatus status,
        bool isInstalled)
    {
        if (RequiresSetupDependencyManualImport(kind))
        {
            return true;
        }

        if (isInstalled)
        {
            return true;
        }

        return kind == SetupDependencyKind.Python312 || status.IsInstallEnabled;
    }

    private static bool ShouldShowSetupPrimaryAction(
        SetupDependencyStatus status,
        bool isInstalled,
        bool hasUpdateAvailable)
    {
        if (!isInstalled)
        {
            return true;
        }

        if (hasUpdateAvailable)
        {
            return true;
        }

        return status.State is ReadinessState.Partial or ReadinessState.Misconfigured or ReadinessState.Missing;
    }

    private static bool HasSetupDependencyUpdate(SetupDependencyKind kind, SetupDependencyStatus status)
    {
        if (kind == SetupDependencyKind.Python312)
        {
            return false;
        }

        if (status.UpdateAvailable)
        {
            return true;
        }

        var installedVersion = ExtractComparableSetupVersion(status.InstalledVersion);
        var latestVersion = ExtractComparableSetupVersion(status.LatestVersion);
        if (installedVersion is not null && latestVersion is not null)
        {
            return installedVersion < latestVersion;
        }

        if (kind == SetupDependencyKind.FfmpegBundle
            && TryExtractFfmpegBuildDate(status.InstalledVersion, out var installedBuildDate)
            && TryExtractFfmpegBuildDate(status.LatestVersion, out var latestBuildDate))
        {
            return installedBuildDate.Date < latestBuildDate.Date;
        }

        if (kind == SetupDependencyKind.VsPluginBundle
            || kind == SetupDependencyKind.Awsmfunc
            || string.IsNullOrWhiteSpace(status.InstalledVersion)
            || string.IsNullOrWhiteSpace(status.LatestVersion))
        {
            return false;
        }

        var supportsRawVersionFallback = kind is SetupDependencyKind.Python312
            or SetupDependencyKind.VapourSynth
            or SetupDependencyKind.Vsrepo
            or SetupDependencyKind.Vsjetpack
            or SetupDependencyKind.X264
            or SetupDependencyKind.X265
            or SetupDependencyKind.SvtAv1
            or SetupDependencyKind.Av1an;

        return supportsRawVersionFallback
            && !string.Equals(status.InstalledVersion, status.LatestVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractFfmpegBuildDate(string value, out DateTime buildDate)
    {
        buildDate = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compactMatch = Regex.Match(value, "(?<!\\d)(20\\d{6})(?!\\d)");
        if (compactMatch.Success
            && DateTime.TryParseExact(
                compactMatch.Groups[1].Value,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out buildDate))
        {
            return true;
        }

        var dashedMatch = Regex.Match(value, "(?<!\\d)(20\\d{2})[-/.](\\d{2})[-/.](\\d{2})(?!\\d)");
        if (!dashedMatch.Success)
        {
            return false;
        }

        var normalized = string.Concat(
            dashedMatch.Groups[1].Value,
            dashedMatch.Groups[2].Value,
            dashedMatch.Groups[3].Value);

        return DateTime.TryParseExact(
            normalized,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out buildDate);
    }

    private static Version? ExtractComparableSetupVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var versionMatch = Regex.Match(value, "(\\d+\\.\\d+(?:\\.\\d+)*)");
        return versionMatch.Success && Version.TryParse(versionMatch.Value, out var parsedVersion)
            ? parsedVersion
            : null;
    }

    private bool IsSetupDependencyInstalled(SetupDependencyKind kind, SetupDependencyStatus status)
    {
        return kind switch
        {
            SetupDependencyKind.Python312 => status.State == ReadinessState.Ready,
            SetupDependencyKind.VsPluginBundle => GetInstalledPluginCount(status.InstalledVersion) > 0
                || status.State is ReadinessState.Ready or ReadinessState.Partial or ReadinessState.Misconfigured,
            SetupDependencyKind.VapourSynth or
            SetupDependencyKind.Vsrepo or
            SetupDependencyKind.Awsmfunc or
            SetupDependencyKind.Vsjetpack => !string.IsNullOrWhiteSpace(status.InstalledVersion)
                || !string.IsNullOrWhiteSpace(status.ExecutablePath)
                || status.State is ReadinessState.Ready or ReadinessState.Partial or ReadinessState.Misconfigured,
            _ => !string.IsNullOrWhiteSpace(status.ExecutablePath)
                || status.State is ReadinessState.Ready or ReadinessState.Partial or ReadinessState.Misconfigured
        };
    }

    private bool CanUninstallSetupDependency(
        SetupDependencyKind kind,
        SetupDependencyStatus status,
        bool isInstalled)
    {
        if (!isInstalled)
        {
            return false;
        }

        return kind switch
        {
            SetupDependencyKind.Python312 => true,
            SetupDependencyKind.VapourSynth or
            SetupDependencyKind.Vsrepo or
            SetupDependencyKind.VsPluginBundle or
            SetupDependencyKind.Awsmfunc or
            SetupDependencyKind.Vsjetpack => true,
            SetupDependencyKind.X264 => HasManagedEncoderBinary(EncoderKind.X264),
            SetupDependencyKind.X265 => HasManagedEncoderBinary(EncoderKind.X265),
            SetupDependencyKind.SvtAv1 => HasManagedEncoderBinary(EncoderKind.SvtAv1),
            SetupDependencyKind.FfmpegBundle => HasManagedToolBinary("ffmpeg.exe"),
            SetupDependencyKind.Av1an => HasManagedToolBinary("av1an.exe"),
            SetupDependencyKind.Avs2PipeMod or
            SetupDependencyKind.DgDemux or
            SetupDependencyKind.Eac3To or
            SetupDependencyKind.Deew or
            SetupDependencyKind.Dee or
            SetupDependencyKind.OpusExt => GetManualSetupDependencyManagedPaths(kind).Any(File.Exists),
            _ => IsPathInsideAppRoot(status.ExecutablePath)
        };
    }

    private bool HasLocalManagedUninstallScope(SetupDependencyKind kind)
    {
        return kind is SetupDependencyKind.FfmpegBundle
            or SetupDependencyKind.X264
            or SetupDependencyKind.X265
            or SetupDependencyKind.SvtAv1
            or SetupDependencyKind.Av1an
            or SetupDependencyKind.Avs2PipeMod
            or SetupDependencyKind.DgDemux
            or SetupDependencyKind.Eac3To
            or SetupDependencyKind.Deew
            or SetupDependencyKind.Dee
            or SetupDependencyKind.OpusExt;
    }

    private bool HasManagedToolBinary(string fileName)
    {
        return File.Exists(Path.Combine(_appPaths.ToolsRootPath, fileName));
    }

    private bool HasManagedEncoderBinary(EncoderKind kind)
    {
        return Enum.GetValues<EncoderArchitecture>()
            .Select(architecture => _appPaths.GetBinaryPath(kind, architecture))
            .Any(File.Exists);
    }

    private bool IsPathInsideAppRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.GetFullPath(_appPaths.RootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var normalizedPath = Path.GetFullPath(path);
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int GetInstalledPluginCount(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var separatorIndex = value.IndexOf('/');
        if (separatorIndex < 0)
        {
            return 0;
        }

        return int.TryParse(value[..separatorIndex], out var count)
            ? count
            : 0;
    }

    private async Task CopySetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Source path is required.", nameof(sourcePath));
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The selected dependency binary was not found.", sourcePath);
        }

        var targetPath = GetManualSetupDependencyTargetPath(kind);
        var targetDirectory = Path.GetDirectoryName(targetPath) ?? _appPaths.ToolsRootPath;
        Directory.CreateDirectory(targetDirectory);

        await using var sourceStream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var targetStream = File.Open(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await sourceStream.CopyToAsync(targetStream);
    }

    private string GetManualSetupDependencyTargetPath(SetupDependencyKind kind)
    {
        return kind switch
        {
            SetupDependencyKind.Avs2PipeMod => Path.Combine(_appPaths.ToolsRootPath, "avs2pipemod64.exe"),
            SetupDependencyKind.DgDemux => Path.Combine(_appPaths.ToolsRootPath, "DGDemux.exe"),
            SetupDependencyKind.Eac3To => Path.Combine(_appPaths.ToolsRootPath, "eac3to.exe"),
            SetupDependencyKind.Deew => Path.Combine(_appPaths.ToolsRootPath, "deew.exe"),
            SetupDependencyKind.Dee => Path.Combine(_appPaths.ToolsRootPath, "dee.exe"),
            SetupDependencyKind.OpusExt => Path.Combine(_appPaths.ToolsRootPath, "opusext.exe"),
            _ => throw new InvalidOperationException("This dependency does not use manual file import.")
        };
    }

    private IEnumerable<string> GetManualSetupDependencyManagedPaths(SetupDependencyKind kind)
    {
        return kind switch
        {
            SetupDependencyKind.Avs2PipeMod => new[]
            {
                Path.Combine(_appPaths.ToolsRootPath, "avs2pipemod64.exe"),
                Path.Combine(_appPaths.ToolsRootPath, "avs2pipemod.exe"),
                Path.Combine(_appPaths.ToolsRootPath, "Avs2Pipemod.exe")
            },
            SetupDependencyKind.DgDemux => new[]
            {
                Path.Combine(_appPaths.ToolsRootPath, "DGDemux.exe"),
                Path.Combine(_appPaths.ToolsRootPath, "dgdemux.exe")
            },
            SetupDependencyKind.Eac3To => new[]
            {
                Path.Combine(_appPaths.ToolsRootPath, "eac3to.exe"),
                Path.Combine(_appPaths.ToolsRootPath, "Eac3to.exe")
            },
            SetupDependencyKind.Deew => new[]
            {
                Path.Combine(_appPaths.ToolsRootPath, "deew.exe"),
                Path.Combine(_appPaths.ToolsRootPath, "Deew.exe")
            },
            SetupDependencyKind.Dee => new[]
            {
                Path.Combine(_appPaths.ToolsRootPath, "dee.exe"),
                Path.Combine(_appPaths.ToolsRootPath, "DEE.exe"),
                Path.Combine(_appPaths.ToolsRootPath, "DolbyEncodingEngine.exe")
            },
            SetupDependencyKind.OpusExt => new[]
            {
                Path.Combine(_appPaths.ToolsRootPath, "opusext.exe"),
                Path.Combine(_appPaths.ToolsRootPath, "OpusExt.exe"),
                Path.Combine(_appPaths.ToolsRootPath, "opusenc.exe"),
                Path.Combine(_appPaths.ToolsRootPath, "OpusEnc.exe")
            },
            _ => Array.Empty<string>()
        };
    }

    private IReadOnlyList<string> GetManualPathKeys(SetupDependencyKind kind)
    {
        return kind switch
        {
            SetupDependencyKind.Python312 => [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Python)],
            SetupDependencyKind.VapourSynth => [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Vspipe)],
            SetupDependencyKind.FfmpegBundle =>
            [
                ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Ffmpeg),
                ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Ffprobe)
            ],
            SetupDependencyKind.X264 => [ManualToolPathKeys.ForEncoder(EncoderKind.X264)],
            SetupDependencyKind.X265 => [ManualToolPathKeys.ForEncoder(EncoderKind.X265)],
            SetupDependencyKind.SvtAv1 => [ManualToolPathKeys.ForEncoder(EncoderKind.SvtAv1)],
            SetupDependencyKind.Av1an => [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Av1an)],
            SetupDependencyKind.Avs2PipeMod => [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Avs2PipeMod)],
            SetupDependencyKind.DgDemux => [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.DgDemux)],
            SetupDependencyKind.Eac3To => [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Eac3To)],
            SetupDependencyKind.Deew => [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Deew)],
            SetupDependencyKind.Dee => [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Dee)],
            SetupDependencyKind.OpusExt => [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.OpusExt)],
            _ => []
        };
    }

    private IReadOnlyDictionary<string, string> BuildManualPathEntries(SetupDependencyKind kind, string executablePath)
    {
        if (kind == SetupDependencyKind.FfmpegBundle)
        {
            return BuildFfmpegBundleManualPathEntries(executablePath);
        }

        var key = GetManualPathKeys(kind).FirstOrDefault();
        return string.IsNullOrWhiteSpace(key)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [key] = executablePath
            };
    }

    private static IReadOnlyDictionary<string, string> BuildFfmpegBundleManualPathEntries(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fileName.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("ffmpeg64.exe", StringComparison.OrdinalIgnoreCase))
        {
            entries[ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Ffmpeg)] = executablePath;
            var ffprobePath = ResolveSiblingExecutable(directory, "ffprobe.exe", "ffprobe64.exe");
            if (!string.IsNullOrWhiteSpace(ffprobePath))
            {
                entries[ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Ffprobe)] = ffprobePath;
            }

            return entries;
        }

        if (fileName.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("ffprobe64.exe", StringComparison.OrdinalIgnoreCase))
        {
            entries[ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Ffprobe)] = executablePath;
            var ffmpegPath = ResolveSiblingExecutable(directory, "ffmpeg.exe", "ffmpeg64.exe");
            if (!string.IsNullOrWhiteSpace(ffmpegPath))
            {
                entries[ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Ffmpeg)] = ffmpegPath;
            }
        }

        return entries;
    }

    private static string? ResolveSiblingExecutable(string directory, params string[] fileNames)
    {
        foreach (var fileName in fileNames)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static ReadinessState ResolveCompositeSetupState(ToolProbeResult primary, ToolProbeResult secondary)
    {
        if (primary.State == ReadinessState.Ready && secondary.State == ReadinessState.Ready)
        {
            return ReadinessState.Ready;
        }

        if (primary.State == ReadinessState.Misconfigured || secondary.State == ReadinessState.Misconfigured)
        {
            return ReadinessState.Misconfigured;
        }

        if (primary.State == ReadinessState.Ready || secondary.State == ReadinessState.Ready)
        {
            return ReadinessState.Partial;
        }

        return primary.State == ReadinessState.Unknown || secondary.State == ReadinessState.Unknown
            ? ReadinessState.Unknown
            : ReadinessState.Missing;
    }

    private ToolProbeResult GetToolResult(RegisteredToolKind kind)
    {
        return _host.GetToolResult(kind);
    }

    private CapabilityReadiness GetCapabilityReadiness(EnvironmentCapabilityKind kind)
    {
        return _host.GetCapabilityReadiness(kind);
    }

    private string BuildToolProbeDetail(ToolProbeResult result)
    {
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

    private string BuildRequirementDetail(CapabilityRequirementReadiness requirement)
    {
        var label = string.Join(
            " / ",
            requirement.CandidateResults.Select(static result => result.DisplayName));
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

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();

        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
