using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Controls.AutoCompression;
using FlowEncode.Controls.AudioProcessing;
using FlowEncode.Controls.BluRayDemux;
using FlowEncode.Controls.Dashboard;
using FlowEncode.Controls.Overview;
using FlowEncode.Controls.Settings;
using FlowEncode.Controls.Templates;
using FlowEncode.Controls.VapourSynth;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using FlowEncode.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace FlowEncode;

public sealed partial class MainWindow : Window, ISettingsViewHost, IShellNavigationHost, IDashboardViewHost, ITemplatesViewHost, IOverviewViewHost
{
    private const int WindowMessageSetIcon = 0x0080;
    private const int WindowIconSmall = 0;
    private const int WindowIconLarge = 1;
    private const int WindowClassLongIcon = -14;
    private const int WindowClassLongSmallIcon = -34;
    private readonly AppLaunchActivation _launchActivation;
    private readonly LocalAppSettingsService _localAppSettingsService;
    private readonly IAppDiagnostics _diagnostics;
    private readonly SemaphoreSlim _externalVapourSynthOpenLock = new(1, 1);
    private readonly TaskCompletionSource<bool> _windowReadyCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, MainWindowShellSectionDefinition> _shellSectionDefinitions;
    private readonly MainWindowShellSectionController _shellSections;
    private readonly UISettings _uiSettings = new();
    private DataPackageView? _activeDragDataView;
    private bool? _activeDragContainsSupportedScript;
    private string _activeShellSectionTag = MainShellSections.Dashboard;
    private bool _isWindowReady;
    private bool _hasCompletedInitialization;
    private bool _isPersistingSettings;
    private bool _isCloseConfirmationInProgress;
    private bool _isShutdownConfirmed;
    private bool _closeCleanupCompleted;
    private IntPtr _windowLargeIconHandle;
    private IntPtr _windowSmallIconHandle;
    private const int ShowWindowRestore = 9;

    public MainWindowViewModel ViewModel { get; }

    private DashboardView? DashboardPanel => GetShellSectionControl<DashboardView>(MainShellSections.Dashboard);

    private VapourSynthWorkspaceView? VapourSynthWorkspacePanel => GetShellSectionControl<VapourSynthWorkspaceView>(MainShellSections.VapourSynthWorkspace);

    private OverviewView? OverviewPanel => GetShellSectionControl<OverviewView>(MainShellSections.Overview);

    private TemplatesView? TemplatesPanel => GetShellSectionControl<TemplatesView>(MainShellSections.Templates);

    private AutoCompressionView? AutoCompressionPanel => GetShellSectionControl<AutoCompressionView>(MainShellSections.AutoCompression);

    private AudioProcessingView? AudioProcessingPanel => GetShellSectionControl<AudioProcessingView>(MainShellSections.AudioProcessing);

    private BluRayDemuxView? BluRayDemuxPanel => GetShellSectionControl<BluRayDemuxView>(MainShellSections.BluRayDemux);

    private SettingsView? SettingsPanel => GetShellSectionControl<SettingsView>(MainShellSections.Settings);

    public MainWindow(
        MainWindowViewModel viewModel,
        AppLaunchActivation launchActivation,
        LocalAppSettingsService localAppSettingsService,
        IAppDiagnostics diagnostics)
    {
        ViewModel = viewModel;
        _launchActivation = launchActivation;
        _localAppSettingsService = localAppSettingsService;
        _diagnostics = diagnostics;
        InitializeComponent();
        _shellSectionDefinitions = BuildShellSectionDefinitions();
        _shellSections = new MainWindowShellSectionController(ShellContentHost, CreateShellSectionControl, OnShellSectionLoaded);
        SetupGuideOverlay.Host = this;

        RootLayout.DataContext = ViewModel;
        RootLayout.ActualThemeChanged += RootLayout_ActualThemeChanged;
        RootLayout.SizeChanged += RootLayout_SizeChanged;
        InitializeShellSections();
        _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyEmbeddedAppIcon();

        AppWindow.Closing += AppWindow_Closing;

        Activated += MainWindow_Activated;

        if (_launchActivation.HasRequestedVapourSynthFile)
        {
            SelectNavigationItem(MainShellSections.VapourSynthWorkspace);
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var runningJobCount = ViewModel.Jobs.Count(job => job.State == EncodingJobState.Running);
        var isAutoCompressionRunning = ViewModel.IsAutoCompressionRunning;
        var isAudioProcessingRunning = ViewModel.IsAudioProcessingRunning;
        var isBluRayDemuxRunning = ViewModel.IsBluRayDemuxRunning;
        var hasRunningWork = ViewModel.HasRunningAppWork;

        if (_isShutdownConfirmed)
        {
            PrepareForClose();
            return;
        }

        args.Cancel = true;
        if (_isCloseConfirmationInProgress)
        {
            return;
        }

        _isCloseConfirmationInProgress = true;

        try
        {
            if (!await PrepareVapourSynthWorkspaceForCloseAsync())
            {
                return;
            }

            if (hasRunningWork)
            {
                var confirmed = await ShowConfirmationAsync(
                    ViewModel.Texts.CloseRunningJobsTitle,
                    ViewModel.Texts.CloseRunningWorkMessage(runningJobCount, isAutoCompressionRunning, isAudioProcessingRunning, isBluRayDemuxRunning),
                    ViewModel.Texts.CloseRunningJobsButton,
                    ViewModel.Texts.CancelButton,
                    ContentDialogButton.Close);

                if (!confirmed)
                {
                    return;
                }

                await ViewModel.CancelRunningJobsForShutdownAsync();
            }

            await CloseVapourSynthPreviewForShutdownAsync();
            _isShutdownConfirmed = true;
            PrepareForClose();
            Close();
        }
        catch (Exception ex)
        {
            await ReportNonFatalWindowExceptionAsync("Failed to close main window", ViewModel.Texts.CloseRunningJobsTitle, ex);
        }
        finally
        {
            _isCloseConfirmationInProgress = false;
        }
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        ApplyEmbeddedAppIcon();
        _isWindowReady = true;
        try
        {
            UpdateAdaptiveLayout(RootLayout.ActualWidth);
            await Task.Yield();
            await ViewModel.InitializeAsync();
            await ShowRecoveredSettingsNoticeIfNeededAsync();
            await ShowRecoveredWorkspaceNoticeIfNeededAsync();
            ApplyTheme(ViewModel.SettingsModule.CurrentThemePreference);
            ApplyVapourSynthWorkspacePresentationIfLoaded();
            if (_launchActivation.HasRequestedVapourSynthFile)
            {
                SelectNavigationItem(MainShellSections.VapourSynthWorkspace);
            }

            InitializeTemplateLibrarySelectionIfLoaded();
            _hasCompletedInitialization = true;
        }
        catch (Exception ex)
        {
            await ReportNonFatalWindowExceptionAsync("Failed to initialize main window", ViewModel.Texts.ErrorSaveFailedTitle, ex);
        }
        finally
        {
            _windowReadyCompletionSource.TrySetResult(true);
        }
    }

    private void InitializeShellSections()
    {
        EnsureShellSectionControl(_activeShellSectionTag);
        ShowShellSection(_activeShellSectionTag);
    }

    private T? GetShellSectionControl<T>(string tag) where T : UserControl
    {
        return _shellSections.GetControl<T>(tag);
    }

    private UserControl EnsureShellSectionControl(string tag)
    {
        var normalizedTag = MainShellSections.Normalize(tag);
        var control = _shellSections.EnsureControl(normalizedTag);
        ApplyAdaptiveLayoutToSection(
            normalizedTag,
            RootLayout.ActualWidth,
            CreateShellContentPadding(RootLayout.ActualWidth),
            RootLayout.ActualWidth < 1000,
            RootLayout.ActualWidth < 700);
        return control;
    }

    private UserControl CreateShellSectionControl(string tag)
    {
        var normalizedTag = MainShellSections.Normalize(tag);
        if (_shellSectionDefinitions.TryGetValue(normalizedTag, out var definition))
        {
            return definition.CreateControl();
        }

        throw new ArgumentOutOfRangeException(nameof(tag), tag, "Unknown shell section.");
    }

    private Dictionary<string, MainWindowShellSectionDefinition> BuildShellSectionDefinitions()
    {
        return new Dictionary<string, MainWindowShellSectionDefinition>(StringComparer.Ordinal)
        {
            [MainShellSections.Dashboard] = new(
                ShellSectionLifetime.Sticky,
                () => CreateSectionView<DashboardView>(ViewModel.DashboardModule, view => view.Host = this),
                static (control, width, contentPadding, _, _) => ((DashboardView)control).ApplyLayout(width, contentPadding)),
            [MainShellSections.BluRayDemux] = new(
                ShellSectionLifetime.Recreatable,
                () => CreateSectionView<BluRayDemuxView>(ViewModel.BluRayDemuxModule),
                static (control, _, contentPadding, stackedWorkspace, compactForms) => ((BluRayDemuxView)control).ApplyLayout(stackedWorkspace, compactForms, contentPadding)),
            [MainShellSections.VapourSynthWorkspace] = new(
                ShellSectionLifetime.Sticky,
                () => CreateSectionView<VapourSynthWorkspaceView>(),
                null,
                (_, window) => window.ApplyVapourSynthWorkspacePresentationIfLoaded()),
            [MainShellSections.Overview] = new(
                ShellSectionLifetime.Sticky,
                () => CreateSectionView<OverviewView>(ViewModel.OverviewModule, view => view.Host = this),
                static (control, width, contentPadding, _, _) => ((OverviewView)control).ApplyLayout(width, contentPadding)),
            [MainShellSections.Templates] = new(
                ShellSectionLifetime.Sticky,
                () => CreateSectionView<TemplatesView>(ViewModel.TemplatesModule, view => view.Host = this),
                static (control, _, contentPadding, stackedWorkspace, compactForms) => ((TemplatesView)control).ApplyLayout(stackedWorkspace, compactForms, contentPadding),
                (_, window) => window.InitializeTemplateLibrarySelectionIfLoaded()),
            [MainShellSections.AudioProcessing] = new(
                ShellSectionLifetime.Recreatable,
                () => CreateSectionView<AudioProcessingView>(ViewModel.AudioProcessingModule),
                static (control, _, contentPadding, stackedWorkspace, compactForms) => ((AudioProcessingView)control).ApplyLayout(stackedWorkspace, compactForms, contentPadding)),
            [MainShellSections.AutoCompression] = new(
                ShellSectionLifetime.Recreatable,
                () => CreateSectionView<AutoCompressionView>(ViewModel.AutoCompressionModule),
                static (control, width, contentPadding, _, compactForms) => ((AutoCompressionView)control).ApplyLayout(compactForms, width, contentPadding)),
            [MainShellSections.Settings] = new(
                ShellSectionLifetime.Sticky,
                () => CreateSectionView<SettingsView>(ViewModel.SettingsModule, view => view.Host = this),
                static (control, _, contentPadding, _, compactForms) => ((SettingsView)control).ApplyLayout(compactForms, contentPadding),
                null,
                static async (_, window) => await window.ViewModel.SetupGuideModule.EnsureCardsAsync())
        };
    }

    private static TView CreateSectionView<TView>(object? dataContext = null, Action<TView>? configure = null)
        where TView : UserControl, new()
    {
        var view = new TView();
        if (dataContext is not null)
        {
            view.DataContext = dataContext;
        }

        configure?.Invoke(view);
        return view;
    }

    private void OnShellSectionLoaded(string tag)
    {
        var normalizedTag = MainShellSections.Normalize(tag);
        if (_shellSectionDefinitions.TryGetValue(normalizedTag, out var definition))
        {
            _diagnostics.TryRun(
                nameof(MainWindow),
                "Run shell section loaded callback",
                () => definition.OnLoaded?.Invoke(normalizedTag, this),
                AppDiagnosticSeverity.Error,
                DiagnosticContext(("section", normalizedTag)));
        }
    }

    private async Task<bool> WaitForShellSectionMaterializedAsync(string tag)
    {
        return await _shellSections.WaitForMaterializedAsync(tag);
    }

    private void ShowShellSection(string tag)
    {
        _activeShellSectionTag = MainShellSections.Normalize(tag);
        _shellSections.Show(_activeShellSectionTag);
    }

    private void ApplyVapourSynthWorkspacePresentationIfLoaded()
    {
        if (VapourSynthWorkspacePanel is null)
        {
            return;
        }

        VapourSynthWorkspacePanel.ViewModel.ApplyLanguage(ViewModel.SettingsModule.CurrentLanguagePreference);
        VapourSynthWorkspacePanel.UpdateEditorPresentation(RootLayout.ActualTheme);
        VapourSynthWorkspacePanel.UpdatePreviewPresentation(
            ViewModel.SettingsModule.CurrentLanguagePreference,
            ViewModel.SettingsModule.CurrentThemePreference);
    }

    private void InitializeTemplateLibrarySelectionIfLoaded()
    {
        if (TemplatesPanel is null)
        {
            return;
        }

        TemplatesPanel.InitializeSelectionIfLoaded();
    }

    private async Task<bool> PrepareVapourSynthWorkspaceForCloseAsync()
    {
        if (VapourSynthWorkspacePanel is null)
        {
            return true;
        }

        return await VapourSynthWorkspacePanel.PrepareForAppCloseAsync(RootLayout.XamlRoot);
    }

    private async Task CloseVapourSynthPreviewForShutdownAsync()
    {
        if (VapourSynthWorkspacePanel is null)
        {
            return;
        }

        await VapourSynthWorkspacePanel.ClosePreviewWindowForAppShutdownAsync();
    }

    private async void RootLayout_DragOver(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var containsSupportedScript = await ContainsSupportedScriptFileAsync(e.DataView);
            e.AcceptedOperation = containsSupportedScript
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
            SetDragDropOverlayVisible(containsSupportedScript);
        }
        catch (Exception ex)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            SetDragDropOverlayVisible(false);
            _diagnostics.WriteException(
                nameof(MainWindow),
                "Inspect drag-over payload",
                ex,
                AppDiagnosticSeverity.Warning);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void RootLayout_Drop(object sender, DragEventArgs e)
    {
        try
        {
            ResetActiveDragState();

            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var storageItems = await e.DataView.GetStorageItemsAsync();
            var file = storageItems
                .OfType<StorageFile>()
                .FirstOrDefault(static item => AppLaunchActivation.IsSupportedScriptExtension(item.Path));

            if (file is null)
            {
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            await HandleExternalVapourSynthOpenAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ReportNonFatalWindowExceptionAsync("Failed to handle dropped VapourSynth script", ViewModel.Texts.ErrorSelectionFailedTitle, ex);
        }
    }

    private void RootLayout_DragLeave(object sender, DragEventArgs e)
    {
        ResetActiveDragState();
    }

    private void RootLayout_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTitleBarColors(sender.ActualTheme);
        if (VapourSynthWorkspacePanel is not null)
        {
            VapourSynthWorkspacePanel.UpdateEditorPresentation(sender.ActualTheme);
        }
    }

    public async Task HandleExternalVapourSynthOpenAsync(string filePath)
    {
        var normalizedPath = AppLaunchActivation.NormalizeSupportedScriptPath(filePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        await _externalVapourSynthOpenLock.WaitAsync();

        try
        {
            await _windowReadyCompletionSource.Task;
            ActivateAndBringToFront();
            SelectNavigationItem(MainShellSections.VapourSynthWorkspace);
            if (await WaitForShellSectionMaterializedAsync(MainShellSections.VapourSynthWorkspace)
                && VapourSynthWorkspacePanel is not null)
            {
                await VapourSynthWorkspacePanel.OpenExternalDocumentAsync(normalizedPath);
            }

            ActivateAndBringToFront();
        }
        catch (Exception ex)
        {
            await ReportNonFatalWindowExceptionAsync("Failed to open external VapourSynth script", ViewModel.Texts.ErrorSelectionFailedTitle, ex);
        }
        finally
        {
            _externalVapourSynthOpenLock.Release();
        }
    }

    public void NavigateToEncodingPage(string sourcePath)
    {
        ViewModel.SourcePath = sourcePath;
        SelectNavigationItem("overview");
        ActivateAndBringToFront();
    }

    private async void ShellNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        try
        {
            var tag = args.SelectedItemContainer?.Tag?.ToString()
                ?? (ShellNavigationView.SelectedItem as NavigationViewItem)?.Tag?.ToString()
                ?? MainShellSections.Dashboard;
            await NavigateToShellSectionAsync(tag);
        }
        catch (Exception ex)
        {
            await ReportNonFatalWindowExceptionAsync("Failed to navigate shell section", ViewModel.Texts.ErrorSelectionFailedTitle, ex);
        }
    }

    private void RootLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAdaptiveLayout(e.NewSize.Width);
    }

    private void UpdateAdaptiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var stackedWorkspace = width < 1000;
        var compactForms = width < 700;
        var contentPadding = CreateShellContentPadding(width);

        foreach (var sectionTag in _shellSections.GetSectionTagsSnapshot())
        {
            ApplyAdaptiveLayoutToSection(sectionTag, width, contentPadding, stackedWorkspace, compactForms);
        }

        SetupGuideOverlay.RefreshLayout();
    }

    private static Thickness CreateShellContentPadding(double width)
    {
        if (width <= 0)
        {
            return new Thickness(28, 16, 28, 28);
        }

        return width < 1100
            ? new Thickness(18, 12, 18, 20)
            : width < 1400
                ? new Thickness(22, 14, 22, 24)
                : new Thickness(28, 16, 28, 28);
    }

    private void ApplyAdaptiveLayoutToSection(
        string tag,
        double width,
        Thickness contentPadding,
        bool stackedWorkspace,
        bool compactForms)
    {
        var normalizedTag = MainShellSections.Normalize(tag);
        if (!_shellSectionDefinitions.TryGetValue(normalizedTag, out var definition)
            || definition.ApplyLayout is null)
        {
            return;
        }

        var control = _shellSections.GetControl(normalizedTag);
        if (control is null)
        {
            return;
        }

        definition.ApplyLayout(control, width, contentPadding, stackedWorkspace, compactForms);
    }

    private async Task HandleAppUpdateAsync()
    {
        var settings = ViewModel.SettingsModule;
        if (settings.IsAppUpdateAvailable)
        {
            if (!settings.CanDownloadAppUpdateInstaller)
            {
                OpenUrl(settings.AppUpdateReleaseUrl);
                return;
            }

            var installerPath = await settings.DownloadLatestAppInstallerAsync();
            if (string.IsNullOrWhiteSpace(installerPath))
            {
                if (settings.HasAppUpdateError)
                {
                    await ShowMessageAsync(settings.Texts.AppUpdateSectionTitle, settings.AppUpdateStatusText);
                }

                return;
            }

            var installNow = await ShowConfirmationAsync(
                settings.Texts.AppUpdateReadyTitle,
                settings.Texts.AppUpdateReadyMessage,
                settings.Texts.InstallNowButton,
                settings.Texts.LaterButton);

            if (!installNow)
            {
                return;
            }

            if (ViewModel.HasRunningJobs
                || ViewModel.IsAutoCompressionRunning
                || ViewModel.IsAudioProcessingRunning
                || ViewModel.IsBluRayDemuxRunning)
            {
                await ShowMessageAsync(settings.Texts.AppUpdateReadyTitle, settings.Texts.AppUpdateInstallRequiresIdleMessage);
                return;
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installerPath)
                });
                if (process is null)
                {
                    _diagnostics.Write(
                        nameof(MainWindow),
                        "Installer process did not start.",
                        AppDiagnosticSeverity.Warning,
                        DiagnosticContext(("installerPath", installerPath)));
                    await ShowMessageAsync(settings.Texts.ErrorInstallFailedTitle, settings.AppUpdateStatusText);
                    return;
                }

                Close();
            }
            catch (Exception ex)
            {
                _diagnostics.WriteException(
                    nameof(MainWindow),
                    "Launch downloaded installer",
                    ex,
                    AppDiagnosticSeverity.Error,
                    DiagnosticContext(("installerPath", installerPath)));
                await ShowMessageAsync(settings.Texts.ErrorInstallFailedTitle, ex.Message);
            }

            return;
        }

        var result = await settings.RefreshAvailableUpdatesAsync();
        if (result is null)
        {
            if (settings.HasAppUpdateError)
            {
                await ShowMessageAsync(settings.Texts.AppUpdateSectionTitle, settings.AppUpdateStatusText);
            }

            return;
        }

        if (!result.UpdateAvailable)
        {
            await ShowMessageAsync(settings.Texts.AppUpdateSectionTitle, settings.AppUpdateStatusText);
        }
    }

    private void SelectNavigationItem(string tag)
    {
        var navigationItem = FindNavigationItem(MainShellSections.Normalize(tag));
        if (navigationItem is null)
        {
            _diagnostics.Write(
                nameof(MainWindow),
                "Navigation item was not found.",
                AppDiagnosticSeverity.Warning,
                DiagnosticContext(("requestedSection", tag)));
            return;
        }

        ShellNavigationView.SelectedItem = navigationItem;
    }

    private async Task NavigateToShellSectionAsync(string tag)
    {
        var normalizedTag = MainShellSections.Normalize(tag);
        var previousTag = _activeShellSectionTag;
        var wasMaterialized = _shellSections.IsMaterialized(normalizedTag);
        ReleaseRecreatableSectionsExcept(normalizedTag);
        EnsureShellSectionControl(normalizedTag);
        ShowShellSection(normalizedTag);

        if (_shellSectionDefinitions.TryGetValue(normalizedTag, out var definition) && definition.OnActivated is not null)
        {
            await definition.OnActivated(normalizedTag, this);
        }

        if (!wasMaterialized || !_shellSections.IsMaterialized(normalizedTag))
        {
            var materialized = await WaitForShellSectionMaterializedAsync(normalizedTag);
            if (!materialized || !string.Equals(_activeShellSectionTag, normalizedTag, StringComparison.Ordinal))
            {
                return;
            }
        }

        if (!string.Equals(previousTag, normalizedTag, StringComparison.Ordinal)
            && !string.Equals(_activeShellSectionTag, normalizedTag, StringComparison.Ordinal))
        {
            return;
        }

        UpdateAdaptiveLayout(RootLayout.ActualWidth);
    }

    private NavigationViewItem? FindNavigationItem(string tag)
    {
        return ShellNavigationView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
    }

    private void ReleaseRecreatableSectionsExcept(string activeTag)
    {
        foreach (var sectionTag in _shellSections.GetSectionTagsSnapshot())
        {
            if (string.Equals(sectionTag, activeTag, StringComparison.Ordinal))
            {
                continue;
            }

            if (_shellSectionDefinitions.TryGetValue(sectionTag, out var definition)
                && definition.Lifetime == ShellSectionLifetime.Recreatable)
            {
                _shellSections.Release(sectionTag);
            }
        }
    }

    private async Task OpenSetupGuideAsync()
    {
        await ViewModel.SetupGuideModule.OpenAsync();
        await Task.Yield();
        SetupGuideOverlay.RefreshLayout();
    }

    private async Task<bool> ShowConfirmationAsync(
        string title,
        string message,
        string primaryButtonText,
        string closeButtonText,
        ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        return await WindowInteractionHelper.ShowConfirmationAsync(
            RootLayout.XamlRoot,
            RootLayout.ActualTheme,
            title,
            message,
            primaryButtonText,
            closeButtonText,
            defaultButton);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await WindowInteractionHelper.ShowMessageAsync(
            RootLayout.XamlRoot,
            RootLayout.ActualTheme,
            ViewModel.Texts.OkButton,
            title,
            message);
    }

    private async Task ShowRecoveredSettingsNoticeIfNeededAsync()
    {
        var recoveryInfo = _localAppSettingsService.ConsumeLastLoadRecoveryInfo();
        if (recoveryInfo is null)
        {
            return;
        }

        await ShowMessageAsync(
            ViewModel.Texts.SettingsRecoveredTitle,
            ViewModel.Texts.SettingsRecoveredMessage(
                recoveryInfo.BackupPath,
                recoveryInfo.LoadError,
                recoveryInfo.BackupError));
    }

    private async Task ShowRecoveredWorkspaceNoticeIfNeededAsync()
    {
        WorkspaceRootRecoveryInfo? recoveryInfo;

        try
        {
            recoveryInfo = App.GetService<LocalAppPaths>().ConsumeStartupWorkspaceRecoveryInfo();
        }
        catch (Exception ex)
        {
            _diagnostics.WriteException(
                nameof(MainWindow),
                "Read startup workspace recovery info",
                ex,
                AppDiagnosticSeverity.Warning);
            return;
        }

        if (recoveryInfo is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(recoveryInfo.FailureReason))
        {
            TryWriteWindowDiagnostic(
                $"Workspace root fallback. {recoveryInfo.FailureReason}",
                AppDiagnosticSeverity.Warning,
                DiagnosticContext(
                    ("configuredPath", recoveryInfo.ConfiguredPath),
                    ("activePath", recoveryInfo.ActivePath)));
        }

        await ShowMessageAsync(
            ViewModel.Texts.WorkspaceRecoveredTitle,
            ViewModel.Texts.WorkspaceRecoveredMessage(
                recoveryInfo.ConfiguredPath,
                recoveryInfo.ActivePath));
    }

    private async Task ReportNonFatalWindowExceptionAsync(string operationName, string errorTitle, Exception ex)
    {
        _diagnostics.WriteException(nameof(MainWindow), operationName, ex, AppDiagnosticSeverity.Error);
        try
        {
            await ShowMessageAsync(errorTitle, ex.Message);
        }
        catch (Exception dialogException)
        {
            _diagnostics.WriteException(
                nameof(MainWindow),
                "Show non-fatal error dialog",
                dialogException,
                AppDiagnosticSeverity.Warning,
                DiagnosticContext(("sourceOperation", operationName)));
        }
    }

    private static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            if (process is null)
            {
                TryWriteWindowDiagnostic(
                    "Explorer process did not start.",
                    AppDiagnosticSeverity.Warning,
                    DiagnosticContext(("path", path)));
            }
        }
        catch (Exception ex)
        {
            TryWriteWindowException(
                "Open path in Explorer",
                ex,
                AppDiagnosticSeverity.Warning,
                DiagnosticContext(("path", path)));
        }
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (process is null)
            {
                TryWriteWindowDiagnostic(
                    "URL process did not start.",
                    AppDiagnosticSeverity.Warning,
                    DiagnosticContext(("url", url)));
            }
        }
        catch (Exception ex)
        {
            TryWriteWindowException(
                "Open URL",
                ex,
                AppDiagnosticSeverity.Warning,
                DiagnosticContext(("url", url)));
        }
    }

    private void ActivateAndBringToFront()
    {
        Activate();

        var windowHandle = WindowNative.GetWindowHandle(this);
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        if (IsIconic(windowHandle))
        {
            ShowWindow(windowHandle, ShowWindowRestore);
        }

        SetForegroundWindow(windowHandle);
    }

    public void BringToFront()
    {
        ActivateAndBringToFront();
    }

    public void CloseWithoutPrompt()
    {
        _isShutdownConfirmed = true;
        Close();
    }

    private void ApplyTitleBarColors(ElementTheme actualTheme)
    {
        var titleBar = AppWindow.TitleBar;
        var foregroundColor = ResolveThemeColor(actualTheme, "TitleBarButtonForegroundBrush");
        var inactiveForegroundColor = ResolveThemeColor(actualTheme, "TitleBarButtonInactiveForegroundBrush");

        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ForegroundColor = foregroundColor;
        titleBar.InactiveForegroundColor = inactiveForegroundColor;
        titleBar.ButtonForegroundColor = foregroundColor;
        titleBar.ButtonHoverForegroundColor = foregroundColor;
        titleBar.ButtonPressedForegroundColor = foregroundColor;
        titleBar.ButtonInactiveForegroundColor = inactiveForegroundColor;
    }

    private static Windows.UI.Color ResolveThemeColor(ElementTheme actualTheme, string resourceKey)
    {
        try
        {
            if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(resourceKey, out var activeResource))
            {
                return activeResource switch
                {
                    Windows.UI.Color color => color,
                    SolidColorBrush brush => brush.Color,
                    _ => actualTheme == ElementTheme.Light ? Colors.Black : Colors.White
                };
            }

            var themeKey = ResolveThemeDictionaryKey(actualTheme);
            if (Microsoft.UI.Xaml.Application.Current.Resources.ThemeDictionaries[themeKey] is ResourceDictionary themeDictionary)
            {
                var resource = themeDictionary[resourceKey];
                return resource switch
                {
                    Windows.UI.Color color => color,
                    SolidColorBrush brush => brush.Color,
                    _ => actualTheme == ElementTheme.Light ? Colors.Black : Colors.White
                };
            }
        }
        catch (Exception ex)
        {
            TryWriteWindowException(
                "Resolve theme resource",
                ex,
                AppDiagnosticSeverity.Warning,
                DiagnosticContext(("resourceKey", resourceKey), ("actualTheme", actualTheme.ToString())));
        }

        return actualTheme == ElementTheme.Light ? Colors.Black : Colors.White;
    }

    private static string ResolveThemeDictionaryKey(ElementTheme actualTheme)
    {
        try
        {
            if (new AccessibilitySettings().HighContrast)
            {
                return "HighContrast";
            }
        }
        catch (Exception ex)
        {
            TryWriteWindowException("Inspect HighContrast state", ex, AppDiagnosticSeverity.Warning);
        }

        return actualTheme == ElementTheme.Light ? "Light" : "Dark";
    }

    private void UiSettings_ColorValuesChanged(UISettings sender, object args)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                ApplyTitleBarColors(RootLayout.ActualTheme);
                ViewModel.RefreshTemplateLibraryView();
                ApplyVapourSynthWorkspacePresentationIfLoaded();
            }
            catch (Exception ex)
            {
                _diagnostics.WriteException(
                    nameof(MainWindow),
                    "Apply system color change",
                    ex,
                    AppDiagnosticSeverity.Warning);
            }
        }))
        {
            _diagnostics.Write(
                nameof(MainWindow),
                "Failed to enqueue system color change handling.",
                AppDiagnosticSeverity.Warning);
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

        ApplyTitleBarColors(RootLayout.ActualTheme);
    }

    private async Task<bool> ContainsSupportedScriptFileAsync(DataPackageView dataView)
    {
        if (!dataView.Contains(StandardDataFormats.StorageItems))
        {
            return false;
        }

        if (ReferenceEquals(_activeDragDataView, dataView) && _activeDragContainsSupportedScript.HasValue)
        {
            return _activeDragContainsSupportedScript.Value;
        }

        try
        {
            var storageItems = await dataView.GetStorageItemsAsync().AsTask();
            var containsSupportedScript = storageItems
                .OfType<StorageFile>()
                .Any(static item => AppLaunchActivation.IsSupportedScriptExtension(item.Path));
            _activeDragDataView = dataView;
            _activeDragContainsSupportedScript = containsSupportedScript;
            return containsSupportedScript;
        }
        catch (Exception ex)
        {
            _activeDragDataView = dataView;
            _activeDragContainsSupportedScript = false;
            _diagnostics.WriteException(
                nameof(MainWindow),
                "Inspect drag-and-drop storage items",
                ex,
                AppDiagnosticSeverity.Warning);
            return false;
        }
    }

    private void ResetActiveDragState()
    {
        _activeDragDataView = null;
        _activeDragContainsSupportedScript = null;
        SetDragDropOverlayVisible(false);
    }

    private void SetDragDropOverlayVisible(bool isVisible)
    {
        var targetVisibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        if (DragDropOverlay.Visibility != targetVisibility)
        {
            DragDropOverlay.Visibility = targetVisibility;
        }
    }

    private async Task<bool> PersistSettingsAsync(bool refreshTemplateLibrary)
    {
        if (!_isWindowReady || !_hasCompletedInitialization || _isPersistingSettings)
        {
            return false;
        }

        _isPersistingSettings = true;

        try
        {
            var error = ViewModel.SaveSettings();
            if (!string.IsNullOrWhiteSpace(error))
            {
                await ShowMessageAsync(ViewModel.Texts.ErrorSaveSettingsFailedTitle, error);
                return false;
            }

            ApplyTheme(ViewModel.SettingsModule.CurrentThemePreference);
            ApplyVapourSynthWorkspacePresentationIfLoaded();

            if (refreshTemplateLibrary)
            {
                ViewModel.RefreshTemplateLibraryView();
                TemplatesPanel?.RestoreCurrentTemplateSelection();
            }

            return true;
        }
        finally
        {
            _isPersistingSettings = false;
        }
    }

    Task<bool> ISettingsViewHost.PersistSettingsAsync(bool refreshTemplateLibrary)
    {
        return PersistSettingsAsync(refreshTemplateLibrary);
    }

    Task<bool> IOverviewViewHost.PersistSettingsAsync(bool refreshTemplateLibrary)
    {
        return PersistSettingsAsync(refreshTemplateLibrary);
    }

    Task ISettingsViewHost.HandleAppUpdateAsync()
    {
        return HandleAppUpdateAsync();
    }

    Task ISettingsViewHost.OpenSetupGuideAsync()
    {
        return OpenSetupGuideAsync();
    }

    void IShellNavigationHost.NavigateToShellSection(string tag)
    {
        SelectNavigationItem(tag);
    }

    void IDashboardViewHost.NavigateToShellSection(string tag)
    {
        SelectNavigationItem(tag);
    }

    void ITemplatesViewHost.SetOverviewTemplateSelection(TemplateLibraryItemViewModel? templateItem)
    {
        EnsureShellSectionControl(MainShellSections.Overview);
        OverviewPanel?.SetOverviewTemplateSelection(templateItem);
    }

    void ITemplatesViewHost.SetSavedTemplateQuickSelection(SavedTemplate? template)
    {
        EnsureShellSectionControl(MainShellSections.Overview);
        OverviewPanel?.SetSavedTemplateQuickSelection(template);
    }

    void IOverviewViewHost.SetTemplateLibrarySelection(TemplateLibraryItemViewModel? templateItem)
    {
        EnsureShellSectionControl(MainShellSections.Templates);
        TemplatesPanel?.SetTemplateLibrarySelection(templateItem);
    }

    async Task IOverviewViewHost.SaveCurrentTemplateAsync()
    {
        EnsureShellSectionControl(MainShellSections.Templates);
        if (await WaitForShellSectionMaterializedAsync(MainShellSections.Templates)
            && TemplatesPanel is not null)
        {
            await TemplatesPanel.SaveCurrentTemplateAsync();
        }
    }

    private void ApplyEmbeddedAppIcon()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return;
        }

        var windowHandle = WindowNative.GetWindowHandle(this);
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var largeIcons = new[] { IntPtr.Zero };
        var smallIcons = new[] { IntPtr.Zero };
        var copiedLargeIcon = IntPtr.Zero;
        var copiedSmallIcon = IntPtr.Zero;

        try
        {
            var extractedCount = ExtractIconEx(processPath, 0, largeIcons, smallIcons, 1);
            if (extractedCount == 0 || extractedCount == uint.MaxValue)
            {
                return;
            }

            var iconHandle = smallIcons[0] != IntPtr.Zero
                ? smallIcons[0]
                : largeIcons[0];
            if (iconHandle == IntPtr.Zero)
            {
                return;
            }

            copiedSmallIcon = CopyIcon(smallIcons[0] != IntPtr.Zero ? smallIcons[0] : iconHandle);
            copiedLargeIcon = CopyIcon(largeIcons[0] != IntPtr.Zero ? largeIcons[0] : iconHandle);

            var persistentLargeIcon = copiedLargeIcon != IntPtr.Zero ? copiedLargeIcon : copiedSmallIcon;
            var persistentSmallIcon = copiedSmallIcon != IntPtr.Zero ? copiedSmallIcon : persistentLargeIcon;
            if (persistentLargeIcon == IntPtr.Zero && persistentSmallIcon == IntPtr.Zero)
            {
                return;
            }

            ReleaseWindowIcons();
            _windowLargeIconHandle = persistentLargeIcon;
            _windowSmallIconHandle = persistentSmallIcon;
            copiedLargeIcon = IntPtr.Zero;
            copiedSmallIcon = IntPtr.Zero;

            ApplyWindowIconHandles(windowHandle);
        }
        catch (Exception ex)
        {
            TryWriteWindowException(
                "Apply embedded app icon",
                ex,
                AppDiagnosticSeverity.Warning,
                DiagnosticContext(("processPath", processPath)));
        }
        finally
        {
            DestroyUniqueIconHandles(copiedSmallIcon, copiedLargeIcon, smallIcons[0], largeIcons[0]);
        }
    }

    private void ApplyWindowIconHandles(IntPtr windowHandle)
    {
        var smallIcon = _windowSmallIconHandle != IntPtr.Zero
            ? _windowSmallIconHandle
            : _windowLargeIconHandle;
        var largeIcon = _windowLargeIconHandle != IntPtr.Zero
            ? _windowLargeIconHandle
            : smallIcon;

        if (smallIcon == IntPtr.Zero && largeIcon == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var iconId = Microsoft.UI.Win32Interop.GetIconIdFromIcon(smallIcon != IntPtr.Zero ? smallIcon : largeIcon);
            AppWindow.SetIcon(iconId);
            AppWindow.SetTaskbarIcon(iconId);
        }
        catch (Exception ex)
        {
            TryWriteWindowException("Assign AppWindow icon handles", ex, AppDiagnosticSeverity.Warning);
        }

        if (largeIcon != IntPtr.Zero)
        {
            SendMessage(windowHandle, WindowMessageSetIcon, (IntPtr)WindowIconLarge, largeIcon);
            SetClassLongPtr(windowHandle, WindowClassLongIcon, largeIcon);
        }

        if (smallIcon != IntPtr.Zero)
        {
            SendMessage(windowHandle, WindowMessageSetIcon, (IntPtr)WindowIconSmall, smallIcon);
            SetClassLongPtr(windowHandle, WindowClassLongSmallIcon, smallIcon);
        }
    }

    private void ReleaseWindowIcons()
    {
        DestroyUniqueIconHandles(_windowSmallIconHandle, _windowLargeIconHandle);
        _windowSmallIconHandle = IntPtr.Zero;
        _windowLargeIconHandle = IntPtr.Zero;
    }

    private static void DestroyUniqueIconHandles(params IntPtr[] iconHandles)
    {
        foreach (var iconHandle in iconHandles.Where(handle => handle != IntPtr.Zero).Distinct())
        {
            DestroyIcon(iconHandle);
        }
    }

    private static void TryWriteWindowDiagnostic(
        string message,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Information,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        try
        {
            App.GetService<IAppDiagnostics>().Write(nameof(MainWindow), message, severity, context);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write window diagnostic. {ex}");
        }
    }

    private static void TryWriteWindowException(
        string operationName,
        Exception exception,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Error,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        try
        {
            App.GetService<IAppDiagnostics>().WriteException(nameof(MainWindow), operationName, exception, severity, context);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write window exception diagnostic. {ex}");
        }
    }

    private static IReadOnlyDictionary<string, string?> DiagnosticContext(params (string Key, string? Value)[] fields)
    {
        var context = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!string.IsNullOrWhiteSpace(field.Key))
            {
                context[field.Key] = field.Value;
            }
        }

        return context;
    }

    [DllImport("Shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(
        string fileName,
        int iconIndex,
        IntPtr[] largeIcons,
        IntPtr[] smallIcons,
        uint iconCount);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern IntPtr CopyIcon(IntPtr iconHandle);

    [DllImport("User32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("User32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr(IntPtr windowHandle, int index, IntPtr newLong);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    private void PrepareForClose()
    {
        if (_closeCleanupCompleted)
        {
            return;
        }

        _closeCleanupCompleted = true;
        Activated -= MainWindow_Activated;
        RootLayout.ActualThemeChanged -= RootLayout_ActualThemeChanged;
        RootLayout.SizeChanged -= RootLayout_SizeChanged;
        _uiSettings.ColorValuesChanged -= UiSettings_ColorValuesChanged;
        AppWindow.Closing -= AppWindow_Closing;
        ReleaseWindowIcons();
        _shellSections.ReleaseAll();
        _externalVapourSynthOpenLock.Dispose();
        ViewModel.Dispose();
    }

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    private enum ShellSectionLifetime
    {
        Sticky,
        Recreatable
    }

    private sealed record MainWindowShellSectionDefinition(
        ShellSectionLifetime Lifetime,
        Func<UserControl> CreateControl,
        Action<UserControl, double, Thickness, bool, bool>? ApplyLayout,
        Action<string, MainWindow>? OnLoaded = null,
        Func<string, MainWindow, Task>? OnActivated = null)
    {
    }
}
