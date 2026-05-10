using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT.Interop;

namespace FlowEncode.Controls.VapourSynth;

public sealed partial class VapourSynthWorkspaceView : UserControl, IDisposable
{
    internal static readonly JsonSerializerOptions BridgeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private const double LogSectionMinHeight = 112;
    private const double LogSectionMaxHeight = 180;

    private readonly TaskCompletionSource<bool> _workspaceInitializedCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IVapourSynthWorkspaceLanguageService _languageService;
    private readonly IVapourSynthPreviewService _previewService;
    private readonly string _editorWebViewUserDataFolderPath;
    private VapourSynthPreviewWindow? _previewWindow;
    private VapourSynthWorkspaceTabViewModel? _previewLogTab;
    private CancellationTokenSource? _diagnosticsCancellationTokenSource;
    private bool _isLoaded;
    private long _diagnosticsVersion;
    private bool _isDisposed;
    private int _workspaceTabSelectionSuppressionCount;

    public VapourSynthWorkspaceViewModel ViewModel { get; }

    public VapourSynthWorkspaceView()
    {
        ViewModel = App.GetService<VapourSynthWorkspaceViewModel>();
        _languageService = App.GetService<IVapourSynthWorkspaceLanguageService>();
        _previewService = App.GetService<IVapourSynthPreviewService>();
        var appPaths = App.GetService<LocalAppPaths>();
        _editorWebViewUserDataFolderPath = Path.Combine(appPaths.DataRootPath, "vapoursynth-workspace", "webview2");
        InitializeComponent();

        DataContext = ViewModel;
        LeftEditorPane.PaneKind = VapourSynthWorkspacePaneKind.Left;
        RightEditorPane.PaneKind = VapourSynthWorkspacePaneKind.Right;
        WorkspaceRoot.KeyDown += WorkspaceRoot_KeyDown;
        AttachEditorPane(LeftEditorPane);
        AttachEditorPane(RightEditorPane);
        Unloaded += UserControl_Unloaded;
        _previewService.LogEmitted += PreviewService_LogEmitted;
    }

    public async Task<bool> PrepareForAppCloseAsync(XamlRoot xamlRoot)
    {
        await CaptureVisibleEditorStatesAsync();

        if (!ViewModel.HasAnyUnsavedChanges)
        {
            await ViewModel.FlushSessionAsync();
            return true;
        }

        var choice = await ShowUnsavedChangesDialogAsync(xamlRoot);
        return choice switch
        {
            UnsavedChangesChoice.Save => await SaveDirtyTabsAsync(),
            UnsavedChangesChoice.Discard => await FlushDiscardedStateAsync(),
            _ => false
        };
    }

    public async Task<bool> OpenExternalDocumentAsync(string filePath)
    {
        var normalizedPath = AppLaunchActivation.NormalizeSupportedScriptPath(filePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        var opened = false;
        await RunUiActionAsync(async () =>
        {
            await EnsureWorkspaceInitializedAsync();

            await RunWithWorkspaceTabSelectionSuppressedAsync(async () =>
            {
                await ViewModel.OpenDocumentAsync(normalizedPath);
                await RefreshWorkspaceTabsAsync();
                await FocusEditorAsync();
            });
            opened = true;
        });

        return opened;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;

        try
        {
            UpdateWorkspaceLayout(ActualHeight);
            await ViewModel.InitializeAsync();
            SelectActiveTabViewItem();
            _workspaceInitializedCompletionSource.TrySetResult(true);
            await InitializeEditorAsync();
        }
        catch (Exception ex)
        {
            _workspaceInitializedCompletionSource.TrySetException(ex);
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorLoadFailedStatus(ex.Message));
        }
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelPendingDiagnostics();
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWorkspaceLayout(e.NewSize.Height);
    }

    private async Task InitializeEditorAsync(bool forceReload = false)
    {
        var assetsRootPath = ViewModel.EditorAssetsRootPath;
        var indexPath = Path.Combine(assetsRootPath, "index.html");
        if (!Directory.Exists(assetsRootPath) || !File.Exists(indexPath))
        {
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorAssetsMissingStatus(indexPath));
            return;
        }

        UpdateEditorPaneLayout();
        await InitializePaneAsync(LeftEditorPane, forceReload);

        if (ViewModel.IsCompareMode && ViewModel.RightTab is not null)
        {
            await InitializePaneAsync(RightEditorPane, forceReload);
        }
    }

    private async Task InitializePaneAsync(VapourSynthEditorPaneView pane, bool forceReload)
    {
        if (!forceReload && pane.IsEditorReady)
        {
            return;
        }

        await pane.InitializeEditorAsync(
            ViewModel.EditorAssetsRootPath,
            Path.Combine(_editorWebViewUserDataFolderPath, pane.PaneKind.ToString().ToLowerInvariant()),
            forceReload);
    }

    private void AttachEditorPane(VapourSynthEditorPaneView pane)
    {
        pane.EditorReady += EditorPane_EditorReady;
        pane.LoadFailed += EditorPane_LoadFailed;
        pane.BufferChanged += EditorPane_BufferChanged;
        pane.CursorChanged += EditorPane_CursorChanged;
        pane.PaneActivated += EditorPane_PaneActivated;
        pane.HostCommandRequested += EditorPane_HostCommandRequested;
        pane.LanguageRequestReceived += EditorPane_LanguageRequestReceived;
        pane.BridgeFailed += EditorPane_BridgeFailed;
    }

    private void EditorPane_PaneActivated(object? sender, EventArgs e)
    {
        if (sender is not VapourSynthEditorPaneView pane)
        {
            return;
        }

        ViewModel.ActivatePane(pane.PaneKind);
        SelectActiveTabViewItem();
    }

    private async void EditorPane_EditorReady(object? sender, EventArgs e)
    {
        if (sender is not VapourSynthEditorPaneView pane)
        {
            return;
        }

        ViewModel.SetWorkspaceStatus(static texts => texts.VapourSynthEditorReadyStatus);
        await pane.ApplyThemeAsync(ActualTheme);
        await LoadLanguageFeaturesAsync(pane);
        await PushDocumentToEditorAsync(pane);
        if (ReferenceEquals(pane, GetActiveEditorPane()))
        {
            await FocusEditorAsync();
        }

        _ = WarmupPythonLanguageServerAsync();
    }

    private void EditorPane_LoadFailed(object? sender, string message)
    {
        ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorLoadFailedStatus(message));
    }

    private void EditorPane_BufferChanged(object? sender, VapourSynthEditorPaneSnapshot snapshot)
    {
        if (sender is not VapourSynthEditorPaneView pane)
        {
            return;
        }

        ViewModel.ActivatePane(pane.PaneKind);
        ViewModel.ApplyEditorBuffer(snapshot.Text, snapshot.Line, snapshot.Column, snapshot.LineCount, snapshot.CharCount);
        SelectActiveTabViewItem();
        ScheduleDiagnostics();
    }

    private void EditorPane_CursorChanged(object? sender, VapourSynthEditorPaneSnapshot snapshot)
    {
        if (sender is not VapourSynthEditorPaneView pane)
        {
            return;
        }

        ViewModel.ActivatePane(pane.PaneKind);
        ViewModel.ApplyCursorState(snapshot.Line, snapshot.Column, snapshot.LineCount, snapshot.CharCount);
        SelectActiveTabViewItem();
    }

    private async void EditorPane_HostCommandRequested(object? sender, string command)
    {
        if (sender is VapourSynthEditorPaneView pane)
        {
            ViewModel.ActivatePane(pane.PaneKind);
        }

        await RunUiActionAsync(() => HandleHostCommandAsync(command));
    }

    private async void EditorPane_LanguageRequestReceived(object? sender, JsonElement root)
    {
        if (sender is not VapourSynthEditorPaneView pane)
        {
            return;
        }

        ViewModel.ActivatePane(pane.PaneKind);
        await HandleLanguageRequestAsync(pane, root);
    }

    private void EditorPane_BridgeFailed(object? sender, string message)
    {
        ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorBridgeFailedStatus(message));
    }

    private async Task HandleHostCommandAsync(string command)
    {
        switch (command)
        {
            case "new":
                await StartNewDocumentAsync();
                break;
            case "open":
                await OpenDocumentAsync();
                break;
            case "save":
                await SaveCurrentDocumentAsync();
                break;
            case "saveAs":
                await SaveCurrentDocumentAsAsync();
                break;
            case "preview":
                await ShowPreviewDeferredAsync();
                break;
            case "encode":
                await StartEncodeAsync();
                break;
        }
    }

    private async void WorkspaceTabView_AddTabButtonClick(TabView sender, object args)
    {
        await RunUiActionAsync(StartNewDocumentAsync);
    }

    private async void WorkspaceRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isDisposed || e.Handled)
        {
            return;
        }

        Func<Task>? action = e.Key switch
        {
            VirtualKey.F5 => ShowPreviewDeferredAsync,
            VirtualKey.F9 => StartEncodeAsync,
            _ => null
        };

        if (action is null)
        {
            return;
        }

        e.Handled = true;
        await RunUiActionAsync(action);
    }

    private async void WorkspaceTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_workspaceTabSelectionSuppressionCount > 0
            || WorkspaceTabView.SelectedItem is not VapourSynthWorkspaceTabViewModel tab
            || ReferenceEquals(tab, ViewModel.ActiveTab))
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await CaptureActiveEditorStateAsync();
            ViewModel.ActivateTab(tab);
            await RefreshWorkspaceTabsAsync();
        });
    }

    private async void WorkspaceTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is not VapourSynthWorkspaceTabViewModel tab)
        {
            return;
        }

        await RunUiActionAsync(() => CloseTabAsync(tab));
    }

    private async void WorkspaceTabViewItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not TabViewItem { DataContext: VapourSynthWorkspaceTabViewModel tab })
        {
            return;
        }

        e.Handled = true;
        await RunUiActionAsync(() => CloseTabAsync(tab));
    }

    private async Task CloseTabAsync(VapourSynthWorkspaceTabViewModel tab)
    {
        if (!await ConfirmTabCloseAsync(tab))
        {
            return;
        }

        await RunWithWorkspaceTabSelectionSuppressedAsync(async () =>
        {
            ViewModel.CloseTab(tab);

            if (ViewModel.Tabs.Count == 0)
            {
                if (await TryCloseAppAfterLastTabClosedAsync())
                {
                    return;
                }

                await ViewModel.CreateNewTabAsync();
            }

            await RefreshWorkspaceTabsAsync();
        });
    }

    private async void ShowTabSideBySideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { CommandParameter: VapourSynthWorkspaceTabViewModel tab })
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await CaptureVisibleEditorStatesAsync();
            await RunWithWorkspaceTabSelectionSuppressedAsync(async () =>
            {
                ViewModel.ShowTabSideBySide(tab);
                await RefreshWorkspaceTabsAsync();
            });
        });
    }

    private async void ExitSideBySideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            await CaptureVisibleEditorStatesAsync();
            await RunWithWorkspaceTabSelectionSuppressedAsync(async () =>
            {
                ViewModel.SetCompareMode(false);
                await RefreshWorkspaceTabsAsync();
            });
        });
    }

    private async void PinTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { CommandParameter: VapourSynthWorkspaceTabViewModel tab })
        {
            return;
        }

        await RunUiActionAsync(() =>
        {
            RunWithWorkspaceTabSelectionSuppressed(() =>
            {
                ViewModel.PinTab(tab, !tab.IsPinned);
                SelectActiveTabViewItem();
            });
            return Task.CompletedTask;
        });
    }

    private async void CloseOtherTabsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { CommandParameter: VapourSynthWorkspaceTabViewModel tab })
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await CaptureVisibleEditorStatesAsync();
            var tabsToClose = ViewModel.Tabs
                .Where(item => !ReferenceEquals(item, tab) && !item.IsPinned)
                .ToArray();

            foreach (var item in tabsToClose)
            {
                if (!await ConfirmTabCloseAsync(item))
                {
                    return;
                }
            }

            await RunWithWorkspaceTabSelectionSuppressedAsync(async () =>
            {
                foreach (var item in tabsToClose)
                {
                    ViewModel.CloseTab(item);
                }

                ViewModel.ActivateTab(tab);
                await RefreshWorkspaceTabsAsync();
            });
        });
    }

    private async Task<bool> TryCloseAppAfterLastTabClosedAsync()
    {
        var mainWindow = App.GetService<MainWindow>();
        if (mainWindow.ViewModel.HasRunningAppWork)
        {
            return false;
        }

        await ViewModel.FlushSessionAsync();
        await ClosePreviewWindowForAppShutdownAsync();
        mainWindow.CloseWithoutPrompt();
        return true;
    }

    private async void NewDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(StartNewDocumentAsync);
    }

    private async void OpenDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(OpenDocumentAsync);
    }

    private async void SaveDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => SaveCurrentDocumentAsync());
    }

    private async void SaveDocumentAsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => SaveCurrentDocumentAsAsync());
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(ShowPreviewDeferredAsync);
    }

    private async void EncodeButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(StartEncodeAsync);
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearPreviewLog();
    }

    private async Task StartEncodeAsync()
    {
        await CaptureActiveEditorStateAsync();

        var sourcePath = ViewModel.CurrentFilePath;
        if (string.IsNullOrWhiteSpace(sourcePath) || ViewModel.HasUnsavedChanges)
        {
            if (!await SaveCurrentDocumentAsync(captureEditorState: false))
            {
                return;
            }
        }

        sourcePath = ViewModel.CurrentFilePath;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var mainWindow = App.GetService<MainWindow>();
        mainWindow.NavigateToEncodingPage(sourcePath);
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ExecuteEditorCommandAsync("undo"));
    }

    private async void RedoButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ExecuteEditorCommandAsync("redo"));
    }

    private async void FindButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ExecuteEditorCommandAsync("find"));
    }

    private async void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ExecuteEditorCommandAsync("replace"));
    }

    private async void GoToButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ExecuteEditorCommandAsync("goto"));
    }

    private async Task StartNewDocumentAsync()
    {
        await CaptureActiveEditorStateAsync();
        await RunWithWorkspaceTabSelectionSuppressedAsync(async () =>
        {
            await ViewModel.CreateNewTabAsync();
            await RefreshWorkspaceTabsAsync();
        });
    }

    private async Task OpenDocumentAsync()
    {
        var filePath = PickOpenFilePath();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        await CaptureActiveEditorStateAsync();
        await RunWithWorkspaceTabSelectionSuppressedAsync(async () =>
        {
            await ViewModel.OpenDocumentAsync(filePath);
            await RefreshWorkspaceTabsAsync();
        });
    }

    private async Task<bool> SaveCurrentDocumentAsync(bool captureEditorState = true)
    {
        if (captureEditorState)
        {
            await CaptureActiveEditorStateAsync();
        }

        if (string.IsNullOrWhiteSpace(ViewModel.CurrentFilePath))
        {
            return await SaveCurrentDocumentAsAsync(captureEditorState: false);
        }

        await ViewModel.SaveAsync();
        await FocusEditorAsync();
        return true;
    }

    private async Task<bool> SaveCurrentDocumentAsAsync(bool captureEditorState = true)
    {
        if (captureEditorState)
        {
            await CaptureActiveEditorStateAsync();
        }

        var filePath = PickSaveFilePath();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        await ViewModel.SaveAsAsync(filePath);
        await FocusEditorAsync();
        return true;
    }

    private async Task ShowPreviewDeferredAsync()
    {
        await CaptureActiveEditorStateAsync();
        var previewTab = ViewModel.ActiveTab;
        if (previewTab is null)
        {
            return;
        }

        _previewLogTab = previewTab;
        ViewModel.ClearPreviewLog(previewTab);
        var request = CreatePreviewOpenRequest(previewTab, out var displayName);
        var previewWindow = GetOrCreatePreviewWindow();

        var mainWindowViewModel = App.GetService<MainWindow>().ViewModel;
        var opened = await previewWindow.OpenOrRefreshAsync(
            request,
            mainWindowViewModel.SettingsModule.CurrentLanguagePreference,
            mainWindowViewModel.SettingsModule.CurrentThemePreference);

        if (opened)
        {
            ViewModel.SetWorkspaceStatus(previewTab, texts => texts.VapourSynthPreviewOpenedStatus(displayName));
            return;
        }

        ViewModel.SetWorkspaceStatus(previewTab, static texts => texts.VapourSynthPreviewEvaluationFailedStatus);
        await FocusEditorAsync();
    }

    public void UpdatePreviewPresentation(AppLanguage language, AppThemePreference themePreference)
    {
        _previewWindow?.ApplyPresentation(language, themePreference);
    }

    public void UpdateEditorPresentation(ElementTheme actualTheme)
    {
        if (_isDisposed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() => _ = ApplyEditorThemeAsync(actualTheme));
    }

    private async void PreviewWindow_PreviewWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not VapourSynthPreviewWindow window)
        {
            return;
        }

        DetachPreviewWindow(window);
        var mainWindow = App.GetService<MainWindow>();
        mainWindow.BringToFront();
        await Task.Yield();
        await FocusEditorAsync();
    }

    public async Task ClosePreviewWindowForAppShutdownAsync()
    {
        if (_previewWindow is null)
        {
            return;
        }

        var previewWindow = _previewWindow;
        DetachPreviewWindow(previewWindow);
        await previewWindow.CloseForOwnerShutdownAsync();
    }

    private VapourSynthPreviewWindow GetOrCreatePreviewWindow()
    {
        if (_previewWindow is not null)
        {
            return _previewWindow;
        }

        _previewWindow = App.GetService<VapourSynthPreviewWindow>();
        _previewWindow.PreviewWindowClosed += PreviewWindow_PreviewWindowClosed;
        return _previewWindow;
    }

    private VapourSynthPreviewOpenRequest CreatePreviewOpenRequest(VapourSynthWorkspaceTabViewModel tab, out string displayName)
    {
        var sourceFilePath = tab.CurrentFilePath;
        displayName = string.IsNullOrWhiteSpace(sourceFilePath)
            ? ViewModel.Texts.VapourSynthUntitledDocument
            : Path.GetFileName(sourceFilePath);

        var workingDirectory = !string.IsNullOrWhiteSpace(sourceFilePath)
            ? Path.GetDirectoryName(sourceFilePath)
            : null;
        var normalizedWorkingDirectory = Directory.Exists(workingDirectory)
            ? workingDirectory!
            : AppContext.BaseDirectory;

        return new VapourSynthPreviewOpenRequest(
            sourceFilePath,
            displayName,
            tab.CurrentContent,
            normalizedWorkingDirectory);
    }

    private void DetachPreviewWindow(VapourSynthPreviewWindow previewWindow)
    {
        previewWindow.PreviewWindowClosed -= PreviewWindow_PreviewWindowClosed;
        if (ReferenceEquals(_previewWindow, previewWindow))
        {
            _previewWindow = null;
            _previewLogTab = null;
        }
    }

    private void PreviewService_LogEmitted(object? sender, VapourSynthPreviewLogEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        var targetTab = _previewLogTab;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            if (targetTab is not null && ViewModel.Tabs.Contains(targetTab))
            {
                ViewModel.AppendPreviewLog(targetTab, e.Entry);
                return;
            }

            ViewModel.AppendPreviewLog(e.Entry);
        });
    }

    private async Task<bool> FlushDiscardedStateAsync()
    {
        await ViewModel.FlushSessionAsync(discardUnsavedChanges: true);
        return true;
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ViewModel.SetWorkspaceStatus(ex.Message);
            await ShowMessageAsync(ViewModel.Texts.VapourSynthWorkspaceTitle, ex.Message, this.XamlRoot);
        }
    }

    private async Task EnsureWorkspaceInitializedAsync()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(VapourSynthWorkspaceView));
        }

        await _workspaceInitializedCompletionSource.Task;
    }

    private async Task CaptureActiveEditorStateAsync()
    {
        await CaptureEditorStateAsync(GetActiveEditorPane(), preserveActivePane: true);
    }

    private async Task CaptureVisibleEditorStatesAsync()
    {
        var activePane = ViewModel.ActivePane;
        await CaptureEditorStateAsync(LeftEditorPane, preserveActivePane: false);

        if (ViewModel.IsCompareMode && ViewModel.RightTab is not null)
        {
            await CaptureEditorStateAsync(RightEditorPane, preserveActivePane: false);
        }

        ViewModel.ActivatePane(activePane);
    }

    private async Task CaptureEditorStateAsync(VapourSynthEditorPaneView pane, bool preserveActivePane)
    {
        if (!pane.IsEditorReady)
        {
            return;
        }

        try
        {
            var snapshot = await pane.CaptureStateAsync();
            if (snapshot is null)
            {
                return;
            }

            var previousPane = ViewModel.ActivePane;
            ViewModel.ActivatePane(pane.PaneKind);
            ViewModel.ApplyEditorBuffer(
                snapshot.Text,
                snapshot.Line,
                snapshot.Column,
                snapshot.LineCount,
                snapshot.CharCount);

            if (preserveActivePane)
            {
                ViewModel.ActivatePane(previousPane);
            }
        }
        catch (Exception ex)
        {
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorBridgeFailedStatus(ex.Message));
        }
    }

    private async Task WarmupPythonLanguageServerAsync()
    {
        try
        {
            await _languageService.WarmupPythonLanguageServerAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task LoadLanguageFeaturesAsync(VapourSynthEditorPaneView pane, bool forceRefresh = false)
    {
        if (!pane.IsEditorReady)
        {
            return;
        }

        var snapshot = await _languageService.GetLanguageFeaturesAsync(forceRefresh, CancellationToken.None);
        if (!snapshot.IsRuntimeReady)
        {
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthLanguageRuntimeUnavailableStatus(snapshot.RuntimeSummary));
        }

        await pane.LoadLanguageFeaturesAsync(snapshot);
    }

    private async Task ApplyEditorThemeAsync(ElementTheme actualTheme)
    {
        await LeftEditorPane.ApplyThemeAsync(actualTheme);
        await RightEditorPane.ApplyThemeAsync(actualTheme);
    }

    private async Task PushDocumentToEditorAsync(VapourSynthEditorPaneView pane)
    {
        if (!pane.IsEditorReady)
        {
            return;
        }

        var tab = ViewModel.GetPaneTab(pane.PaneKind);
        if (tab is null)
        {
            return;
        }

        await pane.LoadDocumentAsync(tab.CurrentContent, tab.CurrentFilePath);
    }

    private async Task FocusEditorAsync()
    {
        await GetActiveEditorPane().FocusEditorAsync();
    }

    public async Task InsertTextIntoEditorAsync(string text, bool onNewLine = false)
    {
        await GetActiveEditorPane().InsertTextAsync(text, onNewLine);
        await FocusEditorAsync();
    }

    public async Task InsertSnippetIntoEditorAsync(string snippet, bool onNewLine = false)
    {
        await GetActiveEditorPane().InsertSnippetAsync(snippet, onNewLine);
        await FocusEditorAsync();
    }

    private async Task ExecuteEditorCommandAsync(string command)
    {
        await GetActiveEditorPane().ExecuteEditorCommandAsync(command);
    }

    private void ScheduleDiagnostics()
    {
        if (!GetActiveEditorPane().IsEditorReady || _isDisposed)
        {
            return;
        }

        CancelPendingDiagnostics();
        _diagnosticsCancellationTokenSource = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _diagnosticsVersion);
        _ = UpdateDiagnosticsAfterDelayAsync(version, _diagnosticsCancellationTokenSource.Token);
    }

    private async Task HandleLanguageRequestAsync(VapourSynthEditorPaneView pane, JsonElement root)
    {
        var requestId = GetString(root, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        try
        {
            var method = GetString(root, "method");
            var document = new VapourSynthTextDocumentContext(
                string.IsNullOrWhiteSpace(GetString(root, "filePath")) ? null : GetString(root, "filePath"),
                GetString(root, "text"));
            var position = new VapourSynthTextDocumentPosition(
                GetInt(root, "line", 1),
                GetInt(root, "column", 1));

            object? result = method switch
            {
                "completion" => await _languageService.GetPythonCompletionsAsync(
                    document,
                    position,
                    GetString(root, "triggerCharacter"),
                    CancellationToken.None),
                "hover" => await _languageService.GetPythonHoverAsync(
                    document,
                    position,
                    CancellationToken.None),
                "signatureHelp" => await _languageService.GetPythonSignatureHelpAsync(
                    document,
                    position,
                    CancellationToken.None),
                _ => throw new InvalidOperationException($"Unsupported language request: {method}")
            };

            await SendLanguageResponseAsync(pane, new
            {
                requestId,
                success = true,
                result
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SendLanguageResponseAsync(pane, new
            {
                requestId,
                success = false,
                error = ex.Message
            });
        }
    }

    private async Task SendLanguageResponseAsync(VapourSynthEditorPaneView pane, object payload)
    {
        if (!pane.IsEditorReady || _isDisposed)
        {
            return;
        }

        await pane.SendLanguageResponseAsync(payload);
    }

    private async Task UpdateDiagnosticsAfterDelayAsync(long version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);

            var diagnostics = await _languageService.DiagnoseScriptAsync(
                ViewModel.CurrentFilePath,
                ViewModel.CurrentContent,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested
                || version != Interlocked.Read(ref _diagnosticsVersion)
                || !GetActiveEditorPane().IsEditorReady)
            {
                return;
            }

            await GetActiveEditorPane().ApplyDiagnosticsAsync(diagnostics);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorBridgeFailedStatus(ex.Message));
        }
    }

    private void CancelPendingDiagnostics()
    {
        if (_diagnosticsCancellationTokenSource is null)
        {
            return;
        }

        _diagnosticsCancellationTokenSource.Cancel();
        _diagnosticsCancellationTokenSource.Dispose();
        _diagnosticsCancellationTokenSource = null;
    }

    private void UpdateWorkspaceLayout(double availableHeight)
    {
        if (availableHeight <= 0)
        {
            return;
        }

        var targetLogHeight = Math.Clamp(Math.Round(availableHeight * 0.22), LogSectionMinHeight, LogSectionMaxHeight);
        if (Math.Abs(LogRowDefinition.Height.Value - targetLogHeight) > 0.5)
        {
            LogRowDefinition.Height = new GridLength(targetLogHeight);
        }
    }

    private async Task RefreshWorkspaceTabsAsync()
    {
        CancelPendingDiagnostics();
        RunWithWorkspaceTabSelectionSuppressed(SelectActiveTabViewItem);
        UpdateEditorPaneLayout();
        await InitializeEditorAsync();
        await PushDocumentToEditorAsync(LeftEditorPane);

        if (ViewModel.IsCompareMode && ViewModel.RightTab is not null)
        {
            await PushDocumentToEditorAsync(RightEditorPane);
        }

        await FocusEditorAsync();
    }

    private async Task RunWithWorkspaceTabSelectionSuppressedAsync(Func<Task> action)
    {
        _workspaceTabSelectionSuppressionCount++;
        try
        {
            await action();
        }
        finally
        {
            _workspaceTabSelectionSuppressionCount--;
        }
    }

    private void RunWithWorkspaceTabSelectionSuppressed(Action action)
    {
        _workspaceTabSelectionSuppressionCount++;
        try
        {
            action();
        }
        finally
        {
            _workspaceTabSelectionSuppressionCount--;
        }
    }

    private void SelectActiveTabViewItem()
    {
        if (!ReferenceEquals(WorkspaceTabView.SelectedItem, ViewModel.ActiveTab))
        {
            WorkspaceTabView.SelectedItem = ViewModel.ActiveTab;
        }
    }

    private void UpdateEditorPaneLayout()
    {
        var showRightPane = ViewModel.IsCompareMode && ViewModel.RightTab is not null;
        RightEditorColumnDefinition.Width = showRightPane ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RightEditorPane.Visibility = showRightPane ? Visibility.Visible : Visibility.Collapsed;
    }

    private VapourSynthEditorPaneView GetActiveEditorPane()
    {
        return ViewModel.ActivePane == VapourSynthWorkspacePaneKind.Right
            && ViewModel.IsCompareMode
            && ViewModel.RightTab is not null
            ? RightEditorPane
            : LeftEditorPane;
    }

    private async Task<bool> ConfirmTabCloseAsync(VapourSynthWorkspaceTabViewModel tab)
    {
        if (!tab.HasUnsavedChanges)
        {
            return true;
        }

        if (ReferenceEquals(tab, ViewModel.ActiveTab))
        {
            await CaptureActiveEditorStateAsync();
        }

        var choice = await ShowUnsavedChangesDialogAsync(this.XamlRoot);
        return choice switch
        {
            UnsavedChangesChoice.Save => await SaveTabAsync(tab),
            UnsavedChangesChoice.Discard => true,
            _ => false
        };
    }

    private async Task<bool> SaveTabAsync(VapourSynthWorkspaceTabViewModel tab)
    {
        if (string.IsNullOrWhiteSpace(tab.CurrentFilePath))
        {
            var previousTab = ViewModel.ActiveTab;
            RunWithWorkspaceTabSelectionSuppressed(() =>
            {
                ViewModel.ActivateTab(tab);
                SelectActiveTabViewItem();
            });
            var filePath = PickSaveFilePath();
            if (previousTab is not null && !ReferenceEquals(previousTab, tab))
            {
                RunWithWorkspaceTabSelectionSuppressed(() =>
                {
                    ViewModel.ActivateTab(previousTab);
                    SelectActiveTabViewItem();
                });
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            await ViewModel.SaveTabAsAsync(tab, filePath);
            return true;
        }

        await ViewModel.SaveTabAsync(tab);
        return true;
    }

    private async Task<bool> SaveDirtyTabsAsync()
    {
        foreach (var tab in ViewModel.DirtyTabs)
        {
            if (!await SaveTabAsync(tab))
            {
                return false;
            }
        }

        await ViewModel.FlushSessionAsync();
        return true;
    }

    private string? PickOpenFilePath()
    {
        return WindowInteractionHelper.PickOpenFilePath(
            GetWindowHandle(),
            ViewModel.Texts.VapourSynthOpenButton,
            ViewModel.CurrentFilePath ?? string.Empty,
            new NativeFileDialogHelper.FileDialogFilter(ViewModel.Texts.VapourSynthFileTypeDescription, "*.vpy"),
            new NativeFileDialogHelper.FileDialogFilter(ViewModel.Texts.VapourSynthPythonFileTypeDescription, "*.py"));
    }

    private string? PickSaveFilePath()
    {
        var suggestedExtension = string.Equals(Path.GetExtension(ViewModel.CurrentFilePath), ".py", StringComparison.OrdinalIgnoreCase)
            ? ".py"
            : ".vpy";
        var suggestedName = string.IsNullOrWhiteSpace(ViewModel.CurrentFilePath)
            ? Path.GetFileNameWithoutExtension(ViewModel.Texts.VapourSynthUntitledDocument)
            : Path.GetFileNameWithoutExtension(ViewModel.CurrentFilePath);

        var result = WindowInteractionHelper.PickSaveFilePath(
            GetWindowHandle(),
            ViewModel.Texts.SaveAsButton,
            ViewModel.CurrentFilePath ?? string.Empty,
            suggestedName,
            suggestedExtension,
            new NativeFileDialogHelper.FileDialogFilter(ViewModel.Texts.VapourSynthFileTypeDescription, "*.vpy"),
            new NativeFileDialogHelper.FileDialogFilter(ViewModel.Texts.VapourSynthPythonFileTypeDescription, "*.py"));
        if (result is null)
        {
            return null;
        }

        var targetExtension = result.Value.SelectedFilterIndex == 2 ? ".py" : ".vpy";
        return Path.ChangeExtension(result.Value.Path, targetExtension);
    }

    private async Task ShowMessageAsync(string title, string message, XamlRoot? xamlRoot)
    {
        if (xamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = ViewModel.Texts.OkButton,
            XamlRoot = xamlRoot,
            RequestedTheme = ActualTheme
        };

        await dialog.ShowAsync();
    }

    private async Task<UnsavedChangesChoice> ShowUnsavedChangesDialogAsync(XamlRoot? xamlRoot)
    {
        if (xamlRoot is null)
        {
            return UnsavedChangesChoice.Cancel;
        }

        var dialog = new ContentDialog
        {
            Title = ViewModel.Texts.VapourSynthUnsavedChangesTitle,
            Content = ViewModel.Texts.VapourSynthUnsavedChangesMessage,
            PrimaryButtonText = ViewModel.Texts.SaveButton,
            SecondaryButtonText = ViewModel.Texts.DontSaveButton,
            CloseButtonText = ViewModel.Texts.CancelButton,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            RequestedTheme = ActualTheme
        };

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => UnsavedChangesChoice.Save,
            ContentDialogResult.Secondary => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel
        };
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(JsonElement element, string propertyName, int fallback)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : fallback;
    }

    private static nint GetWindowHandle()
    {
        return WindowNative.GetWindowHandle(App.GetService<MainWindow>());
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Unloaded -= UserControl_Unloaded;
        WorkspaceRoot.KeyDown -= WorkspaceRoot_KeyDown;
        CancelPendingDiagnostics();
        _previewService.LogEmitted -= PreviewService_LogEmitted;
        LeftEditorPane.Dispose();
        RightEditorPane.Dispose();
        if (_previewWindow is not null)
        {
            var previewWindow = _previewWindow;
            DetachPreviewWindow(previewWindow);

            try
            {
                previewWindow.Close();
            }
            catch
            {
            }
        }
    }

    private enum UnsavedChangesChoice
    {
        Save,
        Discard,
        Cancel
    }
}
