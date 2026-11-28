using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;

namespace FlowEncode.ViewModels;

public sealed class SetupGuideViewModel : ObservableObject, ISetupDependencyModuleViewModel, IDisposable
{
    private readonly ISetupGuideHost _host;
    private readonly ISetupBootstrapService _setupBootstrapService;
    private readonly ISetupGuideCacheService _setupGuideCacheService;
    private readonly IToolRegistryService _toolRegistryService;
    private readonly LocalAppPaths _appPaths;
    private readonly SetupDependencyInstallCoordinator _installCoordinator = new();
    private readonly Dictionary<SetupDependencyKind, SetupGuideDependencyItemViewModel> _installingDependencies = [];
    private readonly SemaphoreSlim _dependencyRefreshGate = new(1, 1);

    private SetupDependencyStatusReport? _setupGuideStatusReport;
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

    public ObservableCollection<SetupGuideCardViewModel> SetupGuideCards { get; } = [];

    public bool IsSetupGuideOpen
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

    public int SelectedSetupGuideCardIndex
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

    public bool CanMoveSetupGuidePrevious => SelectedSetupGuideCardIndex > 0;

    public bool CanMoveSetupGuideNext => SelectedSetupGuideCardIndex < SetupGuideCards.Count - 1;

    public bool IsOnLastSetupGuideCard => SetupGuideCards.Count == 0 || SelectedSetupGuideCardIndex >= SetupGuideCards.Count - 1;

    public bool CanAdvanceOrDismissSetupGuide => IsSetupGuideOpen;

    public string SetupGuideForwardButtonText => IsOnLastSetupGuideCard
        ? Texts.SetupGuideCloseButton
        : Texts.SetupGuideNextButton;

    public string SetupGuidePositionText => SetupGuideCards.Count == 0
        ? string.Empty
        : $"{SelectedSetupGuideCardIndex + 1} / {SetupGuideCards.Count}";

    public Microsoft.UI.Xaml.Visibility SetupGuidePositionVisibility => SetupGuideCards.Count == 0
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility SetupGuideVisibility => IsSetupGuideOpen ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string SetupGuideSummary
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
                $"先准备 Python 3.14 和 VapourSynth，再安装 JET、awsmfunc 和配套插件。当前已就绪 {readyCount}/{items.Count} 项。{avsNote}",
                $"Set up Python 3.14 and VapourSynth first, then install JET, awsmfunc, and the companion plugins. {readyCount}/{items.Count} items are ready. {avsNote}");
        }
    }

    public string DependencyCheckSummary => _setupGuideRemoteCheckedAt.HasValue
        ? $"{GetSetupGuideLocalCheckedAtText()} · {Texts.SetupGuideRemoteCheckedAtLabel(_setupGuideRemoteCheckedAt.Value)}"
        : GetSetupGuideLocalCheckedAtText();

    public bool IsRefreshingSetupGuide => _isRefreshingSetupGuide;

    public bool IsCheckingSetupDependencyUpdates => _isCheckingSetupDependencyUpdates;

    public Microsoft.UI.Xaml.Visibility SetupGuideActionProgressVisibility => _isRefreshingSetupGuide || _isCheckingSetupDependencyUpdates
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string SetupGuideRefreshActionText => _isRefreshingSetupGuide
        ? Texts.SetupGuideRefreshingButton
        : Texts.SetupGuideRefreshButton;

    public string SetupGuideUpdateCheckActionText => _isCheckingSetupDependencyUpdates
        ? Texts.CheckingUpdatesButton
        : Texts.SetupGuideCheckUpdatesButton;

    public bool CanExecuteSetupGuideRefreshAction => CanExecuteSetupGuideAction;

    public bool CanExecuteSetupGuideUpdateCheckAction => CanExecuteSetupGuideAction;

    private bool CanExecuteSetupGuideAction => !IsSetupGuideInstallRunning
        && !_isRefreshingSetupGuide
        && !_isCheckingSetupDependencyUpdates;

    internal bool IsSetupGuideInstallRunning => _isSetupGuideInstallRunning || _installingDependencies.Count > 0;

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

    public Task OpenAsync()
    {
        return OpenSetupGuideAsync();
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

    public Task EnsureCardsAsync()
    {
        return EnsureSetupGuideCardsAsync();
    }

    public void MoveSetupGuidePrevious()
    {
        if (CanMoveSetupGuidePrevious)
        {
            SelectedSetupGuideCardIndex--;
        }
    }

    public void MoveSetupGuideNext()
    {
        if (CanMoveSetupGuideNext)
        {
            SelectedSetupGuideCardIndex++;
        }
    }

    public string? AdvanceOrDismissSetupGuide()
    {
        if (CanMoveSetupGuideNext)
        {
            MoveSetupGuideNext();
            return null;
        }

        return DismissSetupGuide();
    }

    public string? DismissSetupGuide()
    {
        IsSetupGuideOpen = false;

        if (_host.HasCompletedSetupGuide)
        {
            return null;
        }

        _host.HasCompletedSetupGuide = true;
        return _host.SaveSettings(updateStatusText: false);
    }

    public Task RefreshSetupGuideAsync()
    {
        return RefreshSetupGuideAsync(openWhenFinished: false);
    }

    internal async Task RefreshSetupGuideAsync(bool openWhenFinished)
    {
        await RefreshSetupGuideStatusAsync(
            includeRemoteMetadata: false,
            statusOverride: Texts.SetupGuideLocalRefreshCompletedStatus,
            openWhenFinished: openWhenFinished,
            forceEnvironmentScan: true,
            preferCachedSnapshot: false);
    }

    public async Task CheckSetupDependencyUpdatesAsync(bool openWhenFinished = false)
    {
        await RefreshSetupGuideStatusAsync(
            includeRemoteMetadata: true,
            statusOverride: null,
            openWhenFinished: openWhenFinished,
            forceEnvironmentScan: false,
            preferCachedSnapshot: true);
    }

    public async Task<string?> InstallSetupDependencyAsync(SetupDependencyKind kind, bool updateBeyondBaseline = false)
    {
        var item = FindSetupGuideDependency(kind);
        if (item is null)
        {
            return Texts.Pick("未找到对应的首启依赖项。", "Setup dependency item was not found.");
        }

        if (_isSetupGuideInstallRunning || _installingDependencies.ContainsKey(kind))
        {
            return Texts.InstallAlreadyRunning;
        }

        if (kind != SetupDependencyKind.Python312 && HasManualPinnedSetupDependency(kind) && !RequiresSetupDependencyManualImport(kind))
        {
            return Texts.ManualToolPinnedUpdateBlocked(GetSetupDependencyTitle(kind));
        }

        var status = ResolveSetupStatus(kind);
        if (!status.IsInstallSupported || !status.IsInstallEnabled)
        {
            return Texts.Pick("请先安装此依赖所需的前置组件。", "Install the required prerequisites first.");
        }

        return await RunDependencyInstallAsync(item, (progress, cancellationToken) => updateBeyondBaseline
            ? _setupBootstrapService.UpdateBeyondBaselineAsync(kind, progress, cancellationToken)
            : _setupBootstrapService.InstallAsync(kind, progress, cancellationToken));
    }

    public async Task<string?> ChangeVapourSynthPluginAsync(string packageName, VapourSynthPluginAction action)
    {
        const SetupDependencyKind kind = SetupDependencyKind.VapourSynthUtilityPlugins;
        var name = VapourSynthPluginNames.Normalize(packageName);
        if (name is null)
            return Texts.OtherPluginsInvalidName;
        if (VapourSynthPluginNames.IsManagedSeparately(name))
            return Texts.OtherPluginsManagedSeparately(name);
        if (_isSetupGuideInstallRunning || _installingDependencies.ContainsKey(kind))
            return Texts.InstallAlreadyRunning;

        var item = FindSetupGuideDependency(kind);
        if (action != VapourSynthPluginAction.Install &&
            item?.OtherPlugins.Any(plugin => plugin.Name == name && plugin.IsInstalled) != true)
        {
            return action == VapourSynthPluginAction.Update
                ? Texts.OtherPluginsUnavailableForUpdate
                : Texts.OtherPluginsUnavailableForUninstall;
        }

        if (item is null || (action != VapourSynthPluginAction.Uninstall && !item.CanInstallOtherPlugins))
            return Texts.OtherPluginsNeedsRuntime;

        return await RunDependencyInstallAsync(item, (progress, cancellationToken) => action switch
        {
            VapourSynthPluginAction.Update => _setupBootstrapService.UpdateVapourSynthPluginAsync(name, item.ExecutablePath, progress, cancellationToken),
            VapourSynthPluginAction.Uninstall => _setupBootstrapService.UninstallVapourSynthPluginAsync(name, item.ExecutablePath, progress, cancellationToken),
            _ => _setupBootstrapService.InstallVapourSynthPluginAsync(name, item.ExecutablePath, progress, cancellationToken)
        });
    }

    internal async Task<string?> DownloadDeewComponentsAsync()
    {
        var item = FindSetupGuideDependency(SetupDependencyKind.Deew);
        if (_isSetupGuideInstallRunning || _installingDependencies.ContainsKey(SetupDependencyKind.Deew))
            return Texts.InstallAlreadyRunning;
        if (item is null || !item.DeewComponentsDownloadEnabled)
            return Texts.DeewComponentsDownloadUnavailable;

        return await RunDependencyInstallAsync(item, (progress, cancellationToken) =>
            _setupBootstrapService.DownloadDeewComponentsAsync(item.ExecutablePath, progress, cancellationToken),
            Texts.DeewComponentDownloading);
    }

    private async Task<string?> RunDependencyInstallAsync(SetupGuideDependencyItemViewModel item,
        Func<IProgress<SetupInstallProgress>, CancellationToken, Task> install, Func<string, string>? progressText = null)
    {
        var kind = item.Kind;
        _installingDependencies.Add(kind, item);
        item.BeginOperation();
        RaiseSetupGuideStatePropertyChanges();
        string? errorMessage = null;

        try
        {
            var progress = new Progress<SetupInstallProgress>(update => item.ReportProgress(progressText is null
                ? update : update with { StatusText = progressText(update.StatusText) }));
            await _installCoordinator.RunAsync(
                kind,
                cancellationToken => install(progress, cancellationToken),
                () => item.ReportProgress(new SetupInstallProgress(kind, 0,
                    Texts.Pick("等待安装空位或前置组件完成...", "Waiting for an install slot or prerequisites..."), true)));
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            try
            {
                // Serialize scans, but let other downloads and installs keep running.
                await _dependencyRefreshGate.WaitAsync();
                try
                {
                    _host.InvalidateToolProbeCache();
                    await _host.RefreshAsync(includeUpdates: false);
                    await RefreshSetupGuideStatusAsync(
                        includeRemoteMetadata: false,
                        statusOverride: null,
                        openWhenFinished: false,
                        forceEnvironmentScan: false,
                        preferCachedSnapshot: false,
                        allowDuringInstall: true);
                }
                finally
                {
                    _dependencyRefreshGate.Release();
                }
            }
            catch (Exception ex)
            {
                errorMessage ??= ex.Message;
            }
            finally
            {
                _installingDependencies.Remove(kind);
                item.FinishOperation();
                RefreshSetupGuideCards();
                RaiseSetupGuideStatePropertyChanges();
            }
        }

        return errorMessage;
    }

    public bool RequiresSetupDependencyManualImport(SetupDependencyKind kind)
    {
        return ManualToolPackage.Find(kind) is not null;
    }

    public bool CanManuallySelectSetupDependency(SetupDependencyKind kind)
    {
        return GetManualPathKeys(kind).Count > 0;
    }

    public bool HasManualPinnedSetupDependency(SetupDependencyKind kind)
    {
        return GetManualPathKeys(kind).Any(key => _host.ManualToolPaths.ContainsKey(key));
    }

    public string GetSetupDependencyDisplayName(SetupDependencyKind kind)
    {
        return GetSetupDependencyTitle(kind);
    }

    public string GetSetupDependencyCurrentPath(SetupDependencyKind kind)
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
            SetupDependencyKind.Awsmfunc or SetupDependencyKind.Vsjetpack or SetupDependencyKind.VapourSynthUtilityPlugins => _appPaths.WorkspaceRootPath,
            _ => _appPaths.ToolsRootPath
        };
    }

    public async Task<string?> PinSetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath)
    {
        if (IsSetupGuideInstallRunning)
        {
            return Texts.InstallAlreadyRunning;
        }

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
        _host.InvalidateToolProbeCache();
        await _host.RefreshAsync(Texts.ManualToolPinnedStatus(title), includeUpdates: false);
        await RefreshSetupGuideAfterDependencyMutationAsync();
        RestoreSetupGuideCardSelection(selectedCardIndex);
        return null;
    }

    public async Task<string?> ClearManualPinnedSetupDependencyAsync(SetupDependencyKind kind, bool refreshAfterClear = true)
    {
        if (IsSetupGuideInstallRunning)
        {
            return Texts.InstallAlreadyRunning;
        }

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
            _host.InvalidateToolProbeCache();
            await RefreshSetupGuideAfterDependencyMutationAsync();
            RestoreSetupGuideCardSelection(selectedCardIndex);
            return null;
        }

        var title = GetSetupDependencyTitle(kind);
        _host.InvalidateToolProbeCache();
        await _host.RefreshAsync(Texts.ManualToolPinClearedStatus(title), includeUpdates: false);
        await RefreshSetupGuideAfterDependencyMutationAsync();
        RestoreSetupGuideCardSelection(selectedCardIndex);
        return null;
    }

    public async Task<string?> ImportSetupDependencyAsync(SetupDependencyKind kind, string sourcePath)
    {
        var selectedCardIndex = SelectedSetupGuideCardIndex;
        var package = ManualToolPackage.Find(kind);
        if (package is null)
        {
            return Texts.Pick("当前依赖不支持本地导入。", "This dependency does not support local import.");
        }

        var item = FindSetupGuideDependency(kind);
        if (item is null)
        {
            return Texts.Pick("未找到对应的首启依赖项。", "Setup dependency item was not found.");
        }

        if (IsSetupGuideInstallRunning)
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
            item.ReportProgress(new SetupInstallProgress(kind, 15, Texts.Pick("正在导入本地工具…", "Importing local tool…"), false));
            var toolsRoot = _appPaths.ToolsRootPath;
            await Task.Run(() => package.Import(sourcePath, toolsRoot));

            item.ReportProgress(new SetupInstallProgress(kind, 100, Texts.Pick("本地工具已导入。", "Local tool imported."), false));

            _host.InvalidateToolProbeCache();
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

    public async Task<string?> UninstallSetupDependencyAsync(SetupDependencyKind kind)
    {
        var selectedCardIndex = SelectedSetupGuideCardIndex;
        var item = FindSetupGuideDependency(kind);
        if (item is null)
        {
            return Texts.Pick("未找到对应的首启依赖项。", "Setup dependency item was not found.");
        }

        if (IsSetupGuideInstallRunning)
        {
            return Texts.InstallAlreadyRunning;
        }

        if (!item.IsUninstallBaseEnabled)
        {
            return Texts.SetupDependencyUninstallUnavailable;
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
            _host.InvalidateToolProbeCache();
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

    internal void RefreshLocalizedState()
    {
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

    internal Task RefreshFromEnvironmentAsync() => RefreshSetupGuideAfterDependencyMutationAsync();

    private async Task RefreshSetupGuideStatusAsync(
        bool includeRemoteMetadata,
        string? statusOverride,
        bool openWhenFinished,
        bool forceEnvironmentScan,
        bool preferCachedSnapshot,
        bool allowDuringInstall = false)
    {
        if ((!allowDuringInstall && IsSetupGuideInstallRunning)
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
                if (forceEnvironmentScan)
                {
                    _host.InvalidateToolProbeCache();
                }
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

            // A recommendation check uses the existing local snapshot; refresh is the explicit rescan action.
            _setupGuideStatusReport = includeRemoteMetadata
                && previousReport is not null
                && previousReport.CheckedAt >= _host.EnvironmentReadinessReport.CheckedAt
                    ? previousReport with { CheckedAt = DateTimeOffset.Now }
                    : includeRemoteMetadata
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
            _host.NotifyEnvironmentReadinessChanged();

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
        var expansionStates = SetupGuideCards.Select(static card => card.IsExpanded).ToArray();

        if (_host.EnvironmentReadinessReport is null && _setupGuideStatusReport is null)
        {
            ReplaceItems(SetupGuideCards, Array.Empty<SetupGuideCardViewModel>());
            RestoreSetupGuideCardSelection(selectedCardIndex);
            RaiseSetupGuideNavigationPropertyChanges();
            OnPropertyChanged(nameof(SetupGuideSummary));
            return;
        }

        SetupGuideCardViewModel CreateCard(
            int index,
            string title,
            string description,
            string summary,
            IEnumerable<SetupGuideDependencyItemViewModel> items)
        {
            return new SetupGuideCardViewModel(
                Texts,
                title,
                description,
                summary,
                items,
                index < expansionStates.Length && expansionStates[index]);
        }

        var cards = new[]
        {
            CreateCard(
                0,
                Texts.Pick("Python", "Python"),
                Texts.Pick("请先准备 Python，后续 Python 组件的安装都依赖它。", "The first required step. All Python-side installs depend on this card."),
                Texts.Pick("推荐安装 Python 3.14 x64。已有 Python 3.13 或更新的 x64 环境也可使用。", "Python 3.14 x64 is recommended. Existing Python 3.13 or newer x64 installations also work."),
                [BuildSetupDependencyItem(SetupDependencyKind.Python312)]),
            CreateCard(
                1,
                Texts.Pick("VapourSynth", "VapourSynth"),
                Texts.Pick("Python 准备好后，在这里安装 VapourSynth 运行时和脚本依赖。", "After Python is ready, this card covers the VS runtime and script-side dependencies."),
                Texts.Pick("先安装 Python 3.14 x64，再安装本组依赖。", "Install Python 3.14 x64 before installing the dependencies in this group."),
                [
                    BuildSetupDependencyItem(SetupDependencyKind.VapourSynth),
                    BuildSetupDependencyItem(SetupDependencyKind.Awsmfunc),
                    BuildSetupDependencyItem(SetupDependencyKind.Vsjetpack),
                    BuildSetupDependencyItem(SetupDependencyKind.VapourSynthUtilityPlugins)
                ]),
            CreateCard(
                2,
                Texts.Pick("编码器 / FFmpeg", "Encoders / FFmpeg"),
                Texts.Pick("这些工具用于常见的视频处理任务。FFmpeg 支持自动安装；Av1an 有可用的托管兼容包时也可自动安装。", "These are the core binaries used most often in the video workflow. FFmpeg supports automatic install, while Av1an can auto-install when a managed compatible package is available."),
                Texts.Pick("本地编码器放入工作目录下的 encoders，命令行工具放入 tools。", "Managed encoders go into the workspace encoders folder, and CLI tools go into tools."),
                [
                    BuildSetupDependencyItem(SetupDependencyKind.FfmpegBundle),
                    BuildSetupDependencyItem(SetupDependencyKind.X264),
                    BuildSetupDependencyItem(SetupDependencyKind.X265),
                    BuildSetupDependencyItem(SetupDependencyKind.SvtAv1),
                    BuildSetupDependencyItem(SetupDependencyKind.Av1an)
                ]),
            CreateCard(
                3,
                Texts.Pick("解复用 / 其他依赖", "Demux / Other Dependencies"),
                Texts.Pick("蓝光解复用、AviSynth 兼容和音频处理所需的工具，大多需要手动准备。", "This card keeps Blu-ray demux, the AviSynth compatibility chain, and audio tools. Most are manual dependencies."),
                Texts.Pick("蓝光解复用默认使用 DGDemux，也可选择 eac3to。这些本地工具按类别保存在当前用户的工作目录中。", "DGDemux is the default Blu-ray demux backend, and eac3to is the optional backend. These local tools are managed inside categorized folders under the current user's workspace."),
                [
                    BuildSetupDependencyItem(SetupDependencyKind.Avs2PipeMod),
                    BuildSetupDependencyItem(SetupDependencyKind.DgDemux),
                    BuildSetupDependencyItem(SetupDependencyKind.Eac3To),
                    BuildSetupDependencyItem(SetupDependencyKind.Deew),
                    BuildSetupDependencyItem(SetupDependencyKind.Dee),
                    BuildSetupDependencyItem(SetupDependencyKind.OpusExt)
                ])
        };

        if (SetupGuideCards.Count == cards.Length)
        {
            for (var i = 0; i < cards.Length; i++)
            {
                SetupGuideCards[i].UpdateFrom(cards[i]);
            }
        }
        else
        {
            ReplaceItems(SetupGuideCards, cards);
        }

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
        if (_host.HasCompletedSetupGuide && SetupGuideCards.Count == 0 && !IsSetupGuideOpen)
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
        _host.NotifyBusyChanged();
        OnPropertyChanged(nameof(DependencyCheckSummary));
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
        if (snapshot?.StatusReport is null
            || snapshot.StatusReport.CheckedAt < snapshot.EnvironmentCheckedAt)
        {
            return false;
        }

        _setupGuideLocalCheckedAt = snapshot.LocalCheckedAt;
        _setupGuideRemoteCheckedAt = snapshot.RemoteCheckedAt;
        _setupGuideStatusReport = RestoreSetupGuideStatusReport(snapshot.StatusReport);

        RefreshSetupGuideCards();
        RaiseSetupGuideStatePropertyChanges();
        _host.NotifyEnvironmentReadinessChanged();
        return SetupGuideCards.Count > 0;
    }

    private void PersistSetupGuideSnapshot()
    {
        if (_setupGuideStatusReport is null)
        {
            _setupGuideCacheService.Clear();
            return;
        }

        var environmentSnapshot = _setupGuideCacheService.Load();
        _setupGuideCacheService.Save(new SetupGuideCacheSnapshot(
            SetupGuideCacheSnapshot.CurrentSchemaVersion,
            DateTimeOffset.Now,
            _setupGuideLocalCheckedAt,
            _setupGuideRemoteCheckedAt,
            BuildSetupGuideCacheStatusReport(_setupGuideStatusReport),
            environmentSnapshot?.EnvironmentTools,
            environmentSnapshot?.EnvironmentSignature ?? string.Empty,
            environmentSnapshot?.EnvironmentCheckedAt));
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
                    dependency.Detail,
                    PythonPackages: dependency.PythonPackages))
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
                    dependency.Detail,
                    dependency.PythonPackages))
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

    private string BuildSetupDependencyUpdateStatus(SetupDependencyStatusReport report)
    {
        return Texts.Pick("推荐版本已核对。现有工具可用时，可继续使用当前环境。", "Recommended versions checked. Keep the current environment while it works.");
    }

    private SetupGuideDependencyItemViewModel BuildSetupDependencyItem(SetupDependencyKind kind)
    {
        // Keep progress bindings attached when a different install refreshes the cards.
        if (_installingDependencies.TryGetValue(kind, out var installingItem))
        {
            return installingItem;
        }

        var status = ResolveSetupStatus(kind);
        var title = GetSetupDependencyTitle(kind);
        var canManage = kind != SetupDependencyKind.Dee;
        var isInstalled = IsSetupDependencyInstalled(kind, status);
        var hasUpdateAvailable = HasSetupDependencyUpdate(kind, status);
        var showPrimaryAction = ShouldShowSetupPrimaryAction(status, isInstalled, hasUpdateAvailable);
        var checkJetpackUpdatesManually = kind == SetupDependencyKind.Vsjetpack && status.State == ReadinessState.Ready && !hasUpdateAvailable;
        var canUninstall = canManage && CanUninstallSetupDependency(kind, status, isInstalled);
        var showUninstall = canUninstall || (canManage && isInstalled && (HasLocalManagedUninstallScope(kind)
            || kind is SetupDependencyKind.Python312 or SetupDependencyKind.VapourSynth or SetupDependencyKind.Awsmfunc or SetupDependencyKind.Vsjetpack));
        var isManualPinned = canManage && HasManualPinnedSetupDependency(kind);
        var statusText = BuildSetupStatusText(kind, status);
        var warningText = BuildSetupWarningText(kind, status, isInstalled, canUninstall, isManualPinned);
        var primaryActionText = GetSetupPrimaryActionText(kind, status, isInstalled, hasUpdateAvailable);

        return new SetupGuideDependencyItemViewModel(
            kind,
            title,
            status.State,
            Texts.ReadinessStateLabel(status.State),
            BuildInlineSetupStatus(status),
            statusText,
            warningText,
            status.ExecutablePath,
            status.ReleaseUrl,
            primaryActionText,
            canManage && kind != SetupDependencyKind.VapourSynthUtilityPlugins && !ToolchainBaseline.IsRecommendedInstallation(status)
                && (RequiresSetupDependencyManualImport(kind) || showPrimaryAction),
            canManage && IsSetupPrimaryActionEnabled(kind, status, isInstalled),
            showUninstall,
            canUninstall,
            canManage && CanManuallySelectSetupDependency(kind),
            isManualPinned,
            Texts.Pick("更多", "More"),
            Texts.Pick("更新工具（不推荐）", "Update tool (not recommended)"),
            canManage && (checkJetpackUpdatesManually
                || (isInstalled && kind is not (SetupDependencyKind.Vsjetpack or SetupDependencyKind.VapourSynthUtilityPlugins)
                    && showPrimaryAction && !ToolchainBaseline.HasBaseline(kind) && !RequiresSetupDependencyManualImport(kind))),
            ToolchainBaseline.HasBaseline(kind) ? Texts.RecommendedVersionHelp : string.Empty)
        {
            UninstallToolTip = canUninstall ? Texts.UninstallTooltip : Texts.SetupDependencyUninstallUnavailable,
            PythonPackages = status.PythonPackages ?? [],
            IsOtherPluginInstallationAvailable = kind == SetupDependencyKind.VapourSynthUtilityPlugins
                && status.IsInstallEnabled && !string.IsNullOrWhiteSpace(status.ExecutablePath),
            OtherPlugins = kind == SetupDependencyKind.VapourSynthUtilityPlugins ? BuildOtherPluginRows(status) : []
        };
    }

    private string BuildInlineSetupStatus(SetupDependencyStatus status)
    {
        var reportedVersion = status.InstalledVersion.Trim();
        var shortVersion = Regex.Match(reportedVersion, @"(?<![\w.])(?:R\d+(?:\.\d+)*|v?(?<number>\d+(?:\.\d+)+(?:[-+~][a-z0-9.+~-]+)?))",
            RegexOptions.IgnoreCase);
        var installed = shortVersion.Groups["number"].Success ? shortVersion.Groups["number"].Value : shortVersion.Value;
        var recommended = ToolchainBaseline.VersionFor(status.Kind);
        if (recommended.Length > 0)
        {
            if (ToolchainBaseline.IsRecommendedVersion(status.Kind, reportedVersion))
                return Texts.Pick($"{recommended}（推荐）", $"{recommended} (recommended)");
            if (status.Kind == SetupDependencyKind.X265 && recommended.StartsWith(installed + " ", StringComparison.Ordinal))
                return installed;
            return string.IsNullOrWhiteSpace(installed)
                ? Texts.Pick($"推荐 {recommended}", $"Recommended {recommended}")
                : Texts.Pick($"已装 {installed} · 推荐 {recommended}", $"Installed {installed} · Recommended {recommended}");
        }

        if (status.Kind == SetupDependencyKind.Vsjetpack && status.PythonPackages is { Length: > 0 } packages)
        {
            var count = Texts.JetpackPackageCount(packages.Count(static package => package.IsReady), packages.Length);
            return string.IsNullOrWhiteSpace(installed) ? count : $"{installed} · {count}";
        }

        if (HasSetupDependencyUpdate(status.Kind, status) && !string.IsNullOrWhiteSpace(status.LatestVersion))
        {
            var latest = Texts.Pick($"最新 {status.LatestVersion}", $"Latest {status.LatestVersion}");
            return installed.Length == 0 ? latest : Texts.Pick($"已装 {installed} · {latest}", $"Installed {installed} · {latest}");
        }

        return installed;
    }

    private SetupGuideDependencyItemViewModel? FindSetupGuideDependency(SetupDependencyKind kind)
    {
        return SetupGuideCards
            .SelectMany(static card => card.Items)
            .FirstOrDefault(item => item.Kind == kind);
    }

    private SetupDependencyStatus BuildFallbackSetupStatus(SetupDependencyKind kind)
    {
        var python = GetToolResult(RegisteredToolKind.Python);
        var pip = GetToolResult(RegisteredToolKind.Pip);
        var detectedPythonVersion = ExtractComparableSetupVersion(python.DetectedVersion);
        var compatiblePythonReady = python.State == ReadinessState.Ready
            && detectedPythonVersion is not null
            && detectedPythonVersion.Major == 3
            && detectedPythonVersion >= new Version(3, 13)
            && python.DetectedVersion.Contains("x64", StringComparison.OrdinalIgnoreCase);
        var pythonReady = compatiblePythonReady && pip.State == ReadinessState.Ready;
        var pythonState = pythonReady
            ? ReadinessState.Ready
            : compatiblePythonReady && pip.State == ReadinessState.Misconfigured
                ? ReadinessState.Misconfigured
                : compatiblePythonReady
                    ? ReadinessState.Partial
                    : python.State == ReadinessState.Ready ? ReadinessState.Partial : python.State;

        return kind switch
        {
            SetupDependencyKind.Python312 => new SetupDependencyStatus(
                kind,
                pythonState,
                python.DetectedVersion,
                string.Empty,
                false,
                python.ExecutablePath,
                python.ReleaseUrl,
                true,
                true,
                string.Join(Environment.NewLine, [BuildToolProbeDetail(python), $"pip: {BuildToolProbeDetail(pip)}"])),
            SetupDependencyKind.VapourSynth => BuildFallbackToolStatus(kind, RegisteredToolKind.Vspipe, compatiblePythonReady),
            SetupDependencyKind.Awsmfunc => BuildFallbackToolStatus(kind, RegisteredToolKind.PythonModuleAwsmfunc, compatiblePythonReady),
            SetupDependencyKind.Vsjetpack => BuildFallbackToolStatus(kind, RegisteredToolKind.PythonModuleVsjetpack, compatiblePythonReady),
            SetupDependencyKind.VapourSynthUtilityPlugins => BuildFallbackToolStatus(kind, RegisteredToolKind.VapourSynthUtilityPlugins,
                compatiblePythonReady && GetToolResult(RegisteredToolKind.Vspipe).IsReady),
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
        if (toolKind == RegisteredToolKind.Av1an)
        {
            var effectiveState = result.State == ReadinessState.Ready && !result.IsProtocolCompatible
                ? ReadinessState.Partial
                : result.State;
            var compatibilityDetail = result.State switch
            {
                ReadinessState.Ready when result.IsProtocolCompatible => "Protocol compatible",
                ReadinessState.Ready => "Executable detected, protocol compatibility pending",
                _ => result.BackendCompatibilityDetail
            };
            var detail = string.IsNullOrWhiteSpace(result.DetectedVersion)
                ? compatibilityDetail
                : string.IsNullOrWhiteSpace(compatibilityDetail)
                    ? result.DetectedVersion
                    : $"{result.DetectedVersion} · {compatibilityDetail}";

            return new SetupDependencyStatus(
                dependencyKind,
                effectiveState,
                result.DetectedVersion,
                string.Empty,
                false,
                result.ExecutablePath,
                result.ReleaseUrl,
                installSupported,
                isInstallEnabled,
                detail,
                result.IsProtocolCompatible,
                result.ProtocolVersion,
                compatibilityDetail);
        }

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
            result.PythonPackages is null ? BuildToolProbeDetail(result) : string.Empty,
            PythonPackages: result.PythonPackages);
    }

    private string GetSetupDependencyTitle(SetupDependencyKind kind)
    {
        return kind switch
        {
            SetupDependencyKind.Python312 => "Python",
            SetupDependencyKind.VapourSynth => "VapourSynth",
            SetupDependencyKind.Awsmfunc => "awsmfunc",
            SetupDependencyKind.Vsjetpack => "vsjetpack[full]",
            SetupDependencyKind.VapourSynthUtilityPlugins => Texts.OtherPluginsTitle,
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

    private string BuildSetupStatusText(SetupDependencyKind kind, SetupDependencyStatus status)
    {
        if (kind == SetupDependencyKind.VapourSynthUtilityPlugins)
        {
            return status.IsInstallEnabled || status.State == ReadinessState.Ready
                ? string.Empty
                : Texts.OtherPluginsNeedsRuntime;
        }

        if (status.State == ReadinessState.Ready)
            return string.Empty;

        if (kind is SetupDependencyKind.VapourSynth or SetupDependencyKind.Awsmfunc or SetupDependencyKind.Vsjetpack
            && !status.IsInstallEnabled)
        {
            return Texts.Pick("请先安装 Python 3.13 x64 或更高版本。", "Install Python 3.13 x64 or newer first.");
        }

        return kind switch
        {
            SetupDependencyKind.Python312 => BuildPythonStatusText(status),
            SetupDependencyKind.Dee => Texts.Pick(
                $"按音频任务准备所需的授权 DEE {DeewToolchain.MinimumDeeVersion} 或更新版本。",
                $"Provide the licensed DEE {DeewToolchain.MinimumDeeVersion} or newer components required by your audio tasks."),
            SetupDependencyKind.Vsjetpack when status.PythonPackages is { Length: > 0 } => string.Empty,
            SetupDependencyKind.FfmpegBundle when status.State is ReadinessState.Partial or ReadinessState.Misconfigured =>
                Texts.Pick("请检查 FFmpeg 和 FFprobe，或修复为推荐版本。", "Check FFmpeg and FFprobe or restore the recommended version."),
            _ when status.State is ReadinessState.Partial or ReadinessState.Misconfigured =>
                status.Detail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                    ?? Texts.Pick("请查看详情，确认此工具能否运行。", "Open Details to check whether this tool can run."),
            _ => string.Empty
        };
    }

    private string BuildPythonStatusText(SetupDependencyStatus status)
    {
        var version = ExtractComparableSetupVersion(status.InstalledVersion);
        return version >= new Version(3, 13) && status.InstalledVersion.Contains("x64", StringComparison.OrdinalIgnoreCase)
            ? Texts.Pick("请修复此环境的 pip。", "Repair pip in this environment.")
            : Texts.Pick("VapourSynth 需要 Python 3.13 x64 或更高版本。", "VapourSynth requires Python 3.13 x64 or newer.");
    }

    private IReadOnlyList<OtherVapourSynthPluginItem> BuildOtherPluginRows(SetupDependencyStatus status)
    {
        var probed = status.PythonPackages;
        var defaults = VapourSynthPluginNames.DefaultPlugins;
        var packages = defaults.Select(name => probed?.FirstOrDefault(package => package.Name == name)
                ?? new PythonPackageStatus(name, "", false))
            .Concat(probed?.Where(package => !defaults.Contains(package.Name)) ?? []);
        var canInstall = status.IsInstallEnabled && !string.IsNullOrWhiteSpace(status.ExecutablePath);
        return packages.Select(package =>
        {
            var hasVersion = !string.IsNullOrWhiteSpace(package.InstalledVersion);
            var requiresLoadCheck = VapourSynthPluginNames.Required.Contains(package.Name);
            return new OtherVapourSynthPluginItem(
                package.Name,
                hasVersion ? $"{package.Name} {package.InstalledVersion}" : package.Name,
                Texts.OtherPluginStatus(package, probed is not null, requiresLoadCheck),
                Texts.InstallButton,
                Texts.UpdateButton,
                Texts.UninstallButton,
                Texts.OtherPluginUninstallLabel(package.Name),
                hasVersion,
                canInstall);
        }).ToArray();
    }

    private string BuildSetupWarningText(
        SetupDependencyKind kind,
        SetupDependencyStatus status,
        bool isInstalled,
        bool canUninstall,
        bool isManualPinned)
    {
        var warnings = new List<string>();
        if (isManualPinned)
        {
            warnings.Add(Texts.ManualToolPinnedStatus(GetSetupDependencyTitle(kind)));
        }

        if (kind == SetupDependencyKind.X265
            && isInstalled
            && status.State == ReadinessState.Ready
            && !ToolchainBaseline.SupportsX265EightAndTenBit(status.InstalledVersion))
        {
            warnings.Add(Texts.Pick(
                "当前 x265 不满足 8+10 位 multilib 要求，修复为推荐版本可同时支持 8 位和 10 位压制。",
                "The current x265 build does not meet the 8+10-bit multilib requirement. Restore the recommended version to support both 8-bit and 10-bit encoding."));
        }

        if (status.IsInstallSupported && !status.IsInstallEnabled
            && kind is not (SetupDependencyKind.VapourSynth or SetupDependencyKind.Awsmfunc or SetupDependencyKind.Vsjetpack or SetupDependencyKind.VapourSynthUtilityPlugins))
        {
            warnings.Add(Texts.Pick(
                "当前未找到带完整性校验的自动安装包，请稍后重新检查更新或打开发布页手动安装。",
                "No integrity-verified automatic package is currently available. Check again later or install manually from the release page."));
        }

        if (isInstalled && !canUninstall && HasLocalManagedUninstallScope(kind))
        {
            warnings.Add(Texts.SetupDependencyExternalLocationWarning);
        }

        return string.Join(Environment.NewLine, warnings);
    }

    private string GetSetupPrimaryActionText(
        SetupDependencyKind kind,
        SetupDependencyStatus status,
        bool isInstalled,
        bool hasUpdateAvailable)
    {
        if (ToolchainBaseline.HasBaseline(kind))
            return ToolchainBaseline.IsRecommendedInstallation(status)
                ? Texts.Pick("已是推荐版本", "Recommended version installed")
                : Texts.Pick(isInstalled && kind != SetupDependencyKind.Python312 ? "修复为推荐版本" : "安装推荐版本",
                    isInstalled && kind != SetupDependencyKind.Python312 ? "Restore recommended version" : "Install recommended version");

        if (kind == SetupDependencyKind.Python312)
        {
            var targetVersion = string.IsNullOrWhiteSpace(status.LatestVersion)
                ? string.Empty
                : $" {status.LatestVersion}";
            return isInstalled
                ? Texts.Pick($"更新{targetVersion}", $"Update{targetVersion}")
                : Texts.Pick($"安装{targetVersion}", $"Install{targetVersion}");
        }

        if (RequiresSetupDependencyManualImport(kind))
        {
            return Texts.ImportButton;
        }

        if (kind == SetupDependencyKind.Vsjetpack)
        {
            return status.State switch
            {
                ReadinessState.Ready => hasUpdateAvailable ? Texts.UpdateButton : Texts.SetupCheckAndUpdateButton,
                ReadinessState.Partial or ReadinessState.Misconfigured when isInstalled => Texts.SetupRepairDependenciesButton,
                _ => Texts.InstallButton
            };
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
        if (ToolchainBaseline.IsRecommendedInstallation(status)) return false;
        if (RequiresSetupDependencyManualImport(kind))
        {
            return true;
        }

        if (isInstalled)
        {
            return !status.IsInstallSupported || status.IsInstallEnabled;
        }

        return kind == SetupDependencyKind.Python312 || status.IsInstallEnabled;
    }

    private static bool ShouldShowSetupPrimaryAction(
        SetupDependencyStatus status,
        bool isInstalled,
        bool hasUpdateAvailable)
    {
        if (ToolchainBaseline.HasBaseline(status.Kind) || !isInstalled)
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

        if (kind == SetupDependencyKind.Awsmfunc
            || string.IsNullOrWhiteSpace(status.InstalledVersion)
            || string.IsNullOrWhiteSpace(status.LatestVersion))
        {
            return false;
        }

        var supportsRawVersionFallback = kind is SetupDependencyKind.Python312
            or SetupDependencyKind.VapourSynth
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
            SetupDependencyKind.VapourSynthUtilityPlugins => status.PythonPackages?.Any(static package =>
                package.IsReady || !string.IsNullOrWhiteSpace(package.InstalledVersion)) == true,
            SetupDependencyKind.VapourSynth or
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
        if (ManualToolPackage.Find(kind) is { } package)
        {
            var toolsRoot = _appPaths.ToolsRootPath;
            return Directory.Exists(package.GetDirectory(toolsRoot)) || package.GetLegacyPaths(toolsRoot).Any(File.Exists);
        }

        if (!isInstalled)
        {
            return false;
        }

        return kind switch
        {
            SetupDependencyKind.Python312 => _setupBootstrapService.CanUninstall(kind),
            SetupDependencyKind.VapourSynthUtilityPlugins => false,
            SetupDependencyKind.VapourSynth or
            SetupDependencyKind.Awsmfunc or
            SetupDependencyKind.Vsjetpack => _setupBootstrapService.CanUninstall(kind),
            SetupDependencyKind.X264 => HasManagedEncoderBinary(EncoderKind.X264),
            SetupDependencyKind.X265 => HasManagedEncoderBinary(EncoderKind.X265),
            SetupDependencyKind.SvtAv1 => HasManagedEncoderBinary(EncoderKind.SvtAv1),
            SetupDependencyKind.FfmpegBundle => HasManagedToolBinary(ExternalToolKind.Ffmpeg),
            SetupDependencyKind.Av1an => HasManagedToolBinary(ExternalToolKind.Av1an),
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
            || RequiresSetupDependencyManualImport(kind);
    }

    private bool HasManagedToolBinary(ExternalToolKind kind)
    {
        return File.Exists(_appPaths.GetManagedExternalToolPath(kind));
    }

    private bool HasManagedEncoderBinary(EncoderKind kind)
    {
        return File.Exists(_appPaths.GetInstalledBinaryPath(kind, EncoderArchitecture.X64));
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
            ReadinessState.Partial or ReadinessState.Misconfigured when !string.IsNullOrWhiteSpace(result.FailureReason) => result.FailureReason,
            ReadinessState.Missing => Texts.ToolMissingDetail(result.DisplayName),
            ReadinessState.Unknown when !string.IsNullOrWhiteSpace(result.FailureReason) => result.FailureReason,
            _ => Texts.ToolUnknownDetail(result.DisplayName)
        };
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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
