using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Controls.Shared;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRT.Interop;

namespace FlowEncode.Controls.VapourSynth;

public sealed partial class VapourSynthWorkspaceView : UserControl, IDisposable
{
    internal static readonly JsonSerializerOptions BridgeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _editorPaneLayoutTransitionTimer;
    private DateTimeOffset _editorPaneLayoutTransitionStartedAt;
    private double _editorPaneLayoutTransitionStartWidth;
    private double _editorPaneLayoutTransitionTargetWidth;
    private bool _editorPaneLayoutTransitionShowsRightPane;

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
        StopEditorPaneLayoutTransition();
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
        ScheduleDiagnostics(pane);
    }

    private async void EditorPane_EditorReady(object? sender, EventArgs e)
    {
        if (sender is not VapourSynthEditorPaneView pane)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            ViewModel.SetWorkspaceStatus(static texts => texts.VapourSynthEditorReadyStatus);
            await pane.ApplyThemeAsync(ActualTheme);
            await PushDocumentToEditorAsync(pane);
            await LoadLanguageFeaturesAsync(pane);
            if (ReferenceEquals(pane, GetActiveEditorPane()))
            {
                await FocusEditorAsync();
            }

            _ = WarmupPythonLanguageServerAsync();
        });
    }

    private void EditorPane_LoadFailed(object? sender, string message)
    {
        ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorLoadFailedStatus(message));
    }

    private void EditorPane_BufferChanged(object? sender, VapourSynthEditorPaneSnapshot snapshot)
    {
        if (sender is not VapourSynthEditorPaneView pane
            || !TryResolveBoundTab(pane, snapshot.Binding, out var tab))
        {
            return;
        }

        ViewModel.ApplyEditorBuffer(tab, snapshot.Text, snapshot.Line, snapshot.Column, snapshot.LineCount, snapshot.CharCount);
        ScheduleDiagnostics(pane);
    }

    private void EditorPane_CursorChanged(object? sender, VapourSynthEditorPaneSnapshot snapshot)
    {
        if (sender is not VapourSynthEditorPaneView pane
            || !TryResolveBoundTab(pane, snapshot.Binding, out var tab))
        {
            return;
        }

        ViewModel.ApplyCursorState(tab, snapshot.Line, snapshot.Column, snapshot.LineCount, snapshot.CharCount);
    }

    private async void EditorPane_HostCommandRequested(object? sender, VapourSynthEditorHostCommandRequest request)
    {
        if (sender is not VapourSynthEditorPaneView pane
            || !TryResolveBoundTab(pane, request.Binding, out var tab))
        {
            return;
        }

        await RunUiActionAsync(() => HandleHostCommandAsync(request.Command, pane, tab));
    }

    private async void EditorPane_LanguageRequestReceived(object? sender, JsonElement root)
    {
        if (sender is not VapourSynthEditorPaneView pane
            || !TryResolveBoundTab(pane, GetBinding(root), out _))
        {
            return;
        }

        await RunUiActionAsync(() => HandleLanguageRequestAsync(pane, root));
    }

    private void EditorPane_BridgeFailed(object? sender, string message)
    {
        ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorBridgeFailedStatus(message));
    }

    private async Task HandleHostCommandAsync(
        string command,
        VapourSynthEditorPaneView pane,
        VapourSynthWorkspaceTabViewModel tab)
    {
        switch (command)
        {
            case "new":
                await StartNewDocumentAsync(pane);
                break;
            case "open":
                await OpenDocumentAsync(pane);
                break;
            case "save":
                await SaveDocumentAsync(pane, tab);
                break;
            case "saveAs":
                await SaveDocumentAsAsync(pane, tab);
                break;
            case "preview":
                await ShowPreviewDeferredAsync(pane, tab);
                break;
            case "encode":
                await StartEncodeAsync(pane, tab);
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

    private void WorkspaceTabViewItem_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var tab = ResolveTabFromContextRequest(sender, args.OriginalSource as DependencyObject);
        if (tab is null)
        {
            return;
        }

        var anchor = ResolveTabContextMenuAnchor(sender, args.OriginalSource as DependencyObject, tab);
        var flyout = BuildTabContextMenu(tab);
        if (args.TryGetPosition(anchor, out var position))
        {
            flyout.ShowAt(anchor, new FlyoutShowOptions { Position = position });
        }
        else
        {
            flyout.ShowAt(anchor);
        }

        args.Handled = true;
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

    private VapourSynthWorkspaceTabViewModel? ResolveTabFromContextRequest(UIElement sender, DependencyObject? source)
    {
        if (sender is FrameworkElement { DataContext: VapourSynthWorkspaceTabViewModel senderTab })
        {
            return senderTab;
        }

        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: VapourSynthWorkspaceTabViewModel tab })
            {
                return tab;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return WorkspaceTabView.SelectedItem as VapourSynthWorkspaceTabViewModel ?? ViewModel.ActiveTab;
    }

    private FrameworkElement ResolveTabContextMenuAnchor(UIElement sender, DependencyObject? source, VapourSynthWorkspaceTabViewModel tab)
    {
        if (sender is FrameworkElement senderElement)
        {
            return senderElement;
        }

        while (source is not null)
        {
            if (source is TabViewItem tabViewItem)
            {
                return tabViewItem;
            }

            if (source is FrameworkElement { DataContext: VapourSynthWorkspaceTabViewModel } element)
            {
                return element;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return WorkspaceTabView.ContainerFromItem(tab) as FrameworkElement ?? WorkspaceTabView;
    }

    private MenuFlyout BuildTabContextMenu(VapourSynthWorkspaceTabViewModel tab)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(ViewModel.IsCompareMode
            ? CreateTabMenuItem(tab.ExitSideBySideMenuText, ExitSideBySideMenuItem_Click, tab)
            : CreateTabMenuItem(tab.ShowSideBySideMenuText, ShowTabSideBySideMenuItem_Click, tab));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateTabMenuItem(tab.PinMenuText, PinTabMenuItem_Click, tab));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateTabMenuItem(tab.CloseOtherTabsMenuText, CloseOtherTabsMenuItem_Click, tab));
        return flyout;
    }

    private static MenuFlyoutItem CreateTabMenuItem(string text, RoutedEventHandler click, VapourSynthWorkspaceTabViewModel tab)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            CommandParameter = tab
        };
        item.Click += click;
        return item;
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
        if (!TryGetActiveEditorContext(out var pane, out var tab))
        {
            return;
        }

        await StartEncodeAsync(pane, tab);
    }

    private async Task StartEncodeAsync(
        VapourSynthEditorPaneView pane,
        VapourSynthWorkspaceTabViewModel tab)
    {
        await CaptureEditorStateAsync(pane);
        if (!ViewModel.Tabs.Contains(tab))
        {
            return;
        }

        var sourcePath = tab.CurrentFilePath;
        if (string.IsNullOrWhiteSpace(sourcePath) || tab.HasUnsavedChanges)
        {
            if (!await SaveDocumentAsync(pane, tab, captureEditorState: false))
            {
                return;
            }
        }

        sourcePath = tab.CurrentFilePath;
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
        await StartNewDocumentAsync(GetActiveEditorPane());
    }

    private async Task StartNewDocumentAsync(VapourSynthEditorPaneView pane)
    {
        await CaptureEditorStateAsync(pane);
        await RunWithWorkspaceTabSelectionSuppressedAsync(async () =>
        {
            await ViewModel.CreateNewTabAsync();
            await RefreshWorkspaceTabsAsync();
        });
    }

    private async Task OpenDocumentAsync()
    {
        await OpenDocumentAsync(GetActiveEditorPane());
    }

    private async Task OpenDocumentAsync(VapourSynthEditorPaneView pane)
    {
        var filePath = PickOpenFilePath();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        await CaptureEditorStateAsync(pane);
        await RunWithWorkspaceTabSelectionSuppressedAsync(async () =>
        {
            await ViewModel.OpenDocumentAsync(filePath);
            await RefreshWorkspaceTabsAsync();
        });
    }

    private async Task<bool> SaveCurrentDocumentAsync(bool captureEditorState = true)
    {
        if (!TryGetActiveEditorContext(out var pane, out var tab))
        {
            return false;
        }

        return await SaveDocumentAsync(pane, tab, captureEditorState);
    }

    private async Task<bool> SaveDocumentAsync(
        VapourSynthEditorPaneView pane,
        VapourSynthWorkspaceTabViewModel tab,
        bool captureEditorState = true)
    {
        if (captureEditorState)
        {
            await CaptureEditorStateAsync(pane);
        }

        if (!ViewModel.Tabs.Contains(tab))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(tab.CurrentFilePath))
        {
            return await SaveDocumentAsAsync(pane, tab, captureEditorState: false);
        }

        await ViewModel.SaveTabAsync(tab);
        await FocusEditorIfStillBoundAsync(pane, tab);
        return true;
    }

    private async Task<bool> SaveCurrentDocumentAsAsync(bool captureEditorState = true)
    {
        if (!TryGetActiveEditorContext(out var pane, out var tab))
        {
            return false;
        }

        return await SaveDocumentAsAsync(pane, tab, captureEditorState);
    }

    private async Task<bool> SaveDocumentAsAsync(
        VapourSynthEditorPaneView pane,
        VapourSynthWorkspaceTabViewModel tab,
        bool captureEditorState = true)
    {
        if (captureEditorState)
        {
            await CaptureEditorStateAsync(pane);
        }

        if (!ViewModel.Tabs.Contains(tab))
        {
            return false;
        }

        var filePath = PickSaveFilePath(tab);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        await ViewModel.SaveTabAsAsync(tab, filePath);
        await FocusEditorIfStillBoundAsync(pane, tab);
        return true;
    }

    private async Task ShowPreviewDeferredAsync()
    {
        if (!TryGetActiveEditorContext(out var pane, out var tab))
        {
            return;
        }

        await ShowPreviewDeferredAsync(pane, tab);
    }

    private async Task ShowPreviewDeferredAsync(
        VapourSynthEditorPaneView pane,
        VapourSynthWorkspaceTabViewModel previewTab)
    {
        await CaptureEditorStateAsync(pane);
        if (!ViewModel.Tabs.Contains(previewTab))
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
        await FocusEditorIfStillBoundAsync(pane, previewTab);
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

        await RunUiActionAsync(async () =>
        {
            DetachPreviewWindow(window);
            var mainWindow = App.GetService<MainWindow>();
            mainWindow.BringToFront();
            await Task.Yield();
            await FocusEditorAsync();
        });
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
        catch (OperationCanceledException ex)
        {
            TryWriteDiagnosticException("RunUiAction", ex, AppDiagnosticSeverity.Warning);
        }
        catch (Exception ex)
        {
            TryWriteDiagnosticException("RunUiAction", ex);
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
        await CaptureEditorStateAsync(GetActiveEditorPane());
    }

    private async Task CaptureVisibleEditorStatesAsync()
    {
        await CaptureEditorStateAsync(LeftEditorPane);

        if (ViewModel.IsCompareMode && ViewModel.RightTab is not null)
        {
            await CaptureEditorStateAsync(RightEditorPane);
        }
    }

    private async Task<bool> CaptureEditorStateAsync(VapourSynthEditorPaneView pane)
    {
        if (!pane.IsEditorReady)
        {
            return false;
        }

        try
        {
            var snapshot = await pane.CaptureStateAsync();
            if (snapshot is null)
            {
                return false;
            }

            return ApplyEditorSnapshot(pane, snapshot);
        }
        catch (Exception ex)
        {
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorBridgeFailedStatus(ex.Message));
            return false;
        }
    }

    private bool ApplyEditorSnapshot(VapourSynthEditorPaneView pane, VapourSynthEditorPaneSnapshot snapshot)
    {
        if (!TryResolveBoundTab(pane, snapshot.Binding, out var tab))
        {
            return false;
        }

        ViewModel.ApplyEditorBuffer(
            tab,
            snapshot.Text,
            snapshot.Line,
            snapshot.Column,
            snapshot.LineCount,
            snapshot.CharCount);
        return true;
    }

    private static bool HasEditorTextChanged(
        VapourSynthEditorPaneSnapshot? beforeSnapshot,
        VapourSynthEditorPaneSnapshot? afterSnapshot)
    {
        return afterSnapshot is not null
            && !string.Equals(beforeSnapshot?.Text ?? string.Empty, afterSnapshot.Text, StringComparison.Ordinal);
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

        var binding = pane.BeginDocumentLoad(tab.Id);
        try
        {
            var acknowledgedBinding = await pane.LoadDocumentAsync(
                tab.CurrentContent,
                tab.CurrentFilePath,
                binding);
            if (acknowledgedBinding is null)
            {
                pane.FailDocumentLoad(binding, "Editor did not confirm the document load.");
                return;
            }

            if (!ViewModel.Tabs.Contains(tab)
                || !ReferenceEquals(tab, ViewModel.GetPaneTab(pane.PaneKind))
                || !pane.TryConfirmDocumentLoad(binding, acknowledgedBinding))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            pane.FailDocumentLoad(binding, ex.Message);
            throw;
        }

        if (ReferenceEquals(pane, GetActiveEditorPane()))
        {
            ScheduleDiagnostics(pane);
        }
    }

    private async Task FocusEditorAsync()
    {
        await GetActiveEditorPane().FocusEditorAsync();
    }

    private async Task FocusEditorIfStillBoundAsync(
        VapourSynthEditorPaneView pane,
        VapourSynthWorkspaceTabViewModel tab)
    {
        if (ReferenceEquals(tab, ViewModel.GetPaneTab(pane.PaneKind))
            && pane.ConfirmedBinding is { } binding
            && string.Equals(binding.TabId, tab.Id, StringComparison.Ordinal))
        {
            await pane.FocusEditorAsync();
        }
    }

    public async Task InsertTextIntoEditorAsync(string text, bool onNewLine = false)
    {
        if (TryGetActiveEditorContext(out var pane, out var tab)
            && IsPaneBoundToTab(pane, tab))
        {
            await pane.InsertTextAsync(text, onNewLine);
            await pane.FocusEditorAsync();
        }
    }

    public async Task InsertSnippetIntoEditorAsync(string snippet, bool onNewLine = false)
    {
        if (TryGetActiveEditorContext(out var pane, out var tab)
            && IsPaneBoundToTab(pane, tab))
        {
            await pane.InsertSnippetAsync(snippet, onNewLine);
            await pane.FocusEditorAsync();
        }
    }

    private async Task ExecuteEditorCommandAsync(string command)
    {
        if (TryGetActiveEditorContext(out var pane, out var tab)
            && IsPaneBoundToTab(pane, tab))
        {
            await pane.ExecuteEditorCommandAsync(command);
        }
    }

    private void ScheduleDiagnostics(VapourSynthEditorPaneView? pane = null)
    {
        var targetPane = pane ?? GetActiveEditorPane();
        if (!targetPane.IsEditorReady || _isDisposed)
        {
            return;
        }

        var binding = targetPane.ConfirmedBinding;
        if (!TryResolveBoundTab(targetPane, binding, out var tab))
        {
            return;
        }

        CancelPendingDiagnostics();
        _diagnosticsCancellationTokenSource = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _diagnosticsVersion);
        _ = UpdateDiagnosticsAfterDelayAsync(
            version,
            targetPane,
            binding!,
            tab.CurrentFilePath,
            tab.CurrentContent,
            _diagnosticsCancellationTokenSource.Token);
    }

    private async Task HandleLanguageRequestAsync(VapourSynthEditorPaneView pane, JsonElement root)
    {
        var requestId = GetString(root, "requestId");
        var binding = GetBinding(root);
        if (string.IsNullOrWhiteSpace(requestId)
            || !TryResolveBoundTab(pane, binding, out _))
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
                _ => throw new InvalidOperationException($"Unsupported language request: {method}")
            };

            await SendLanguageResponseAsync(pane, binding!, new
            {
                requestId,
                success = true,
                result
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SendLanguageResponseAsync(pane, binding!, new
            {
                requestId,
                success = false,
                error = ex.Message
            });
        }
    }

    private async Task SendLanguageResponseAsync(
        VapourSynthEditorPaneView pane,
        VapourSynthEditorDocumentBinding binding,
        object payload)
    {
        if (_isDisposed || !TryResolveBoundTab(pane, binding, out _))
        {
            return;
        }

        await pane.SendLanguageResponseAsync(payload);
    }

    private async Task UpdateDiagnosticsAfterDelayAsync(
        long version,
        VapourSynthEditorPaneView pane,
        VapourSynthEditorDocumentBinding binding,
        string? filePath,
        string content,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

            var diagnostics = await _languageService.DiagnoseScriptAsync(
                filePath,
                content,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested
                || version != Interlocked.Read(ref _diagnosticsVersion)
                || !TryResolveBoundTab(pane, binding, out _))
            {
                return;
            }

            await pane.ApplyDiagnosticsAsync(diagnostics);
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer edit supersedes pending diagnostics.
        }
        catch (Exception ex)
        {
            TryWriteDiagnosticException("UpdateDiagnosticsAfterDelay", ex, AppDiagnosticSeverity.Warning);
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
        if (!UiMotionPolicy.AreCustomAnimationsEnabled() || EditorSurfaceHost.ActualWidth <= 0)
        {
            ApplyEditorPaneLayout(showRightPane);
            return;
        }

        var rightPaneIsVisible = RightEditorPane.Visibility == Visibility.Visible;
        if (showRightPane == rightPaneIsVisible && _editorPaneLayoutTransitionTimer is null)
        {
            return;
        }

        StartEditorPaneLayoutTransition(showRightPane);
    }

    private void ApplyEditorPaneLayout(bool showRightPane)
    {
        StopEditorPaneLayoutTransition();
        RightEditorPane.Visibility = showRightPane ? Visibility.Visible : Visibility.Collapsed;
        RightEditorColumnDefinition.Width = showRightPane
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
    }

    private void StartEditorPaneLayoutTransition(bool showRightPane)
    {
        StopEditorPaneLayoutTransition();

        var currentWidth = RightEditorPane.Visibility == Visibility.Visible
            ? Math.Max(0, RightEditorColumnDefinition.ActualWidth)
            : 0;
        var targetWidth = showRightPane
            ? Math.Max(0, (EditorSurfaceHost.ActualWidth - EditorSurfaceHost.ColumnSpacing) / 2)
            : 0;

        if (Math.Abs(currentWidth - targetWidth) < 0.5)
        {
            ApplyEditorPaneLayout(showRightPane);
            return;
        }

        RightEditorPane.Visibility = Visibility.Visible;
        RightEditorColumnDefinition.Width = new GridLength(currentWidth, GridUnitType.Pixel);
        _editorPaneLayoutTransitionStartWidth = currentWidth;
        _editorPaneLayoutTransitionTargetWidth = targetWidth;
        _editorPaneLayoutTransitionShowsRightPane = showRightPane;
        _editorPaneLayoutTransitionStartedAt = DateTimeOffset.UtcNow;
        _editorPaneLayoutTransitionTimer = DispatcherQueue.CreateTimer();
        _editorPaneLayoutTransitionTimer.Interval = TimeSpan.FromMilliseconds(16);
        _editorPaneLayoutTransitionTimer.Tick += EditorPaneLayoutTransitionTimer_Tick;
        _editorPaneLayoutTransitionTimer.Start();
    }

    private void EditorPaneLayoutTransitionTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        var elapsedMilliseconds = (DateTimeOffset.UtcNow - _editorPaneLayoutTransitionStartedAt).TotalMilliseconds;
        var progress = Math.Clamp(elapsedMilliseconds / UiTokens.MotionNormal, 0, 1);
        var easedProgress = progress * progress * (3 - (2 * progress));
        var width = _editorPaneLayoutTransitionStartWidth
            + ((_editorPaneLayoutTransitionTargetWidth - _editorPaneLayoutTransitionStartWidth) * easedProgress);
        RightEditorColumnDefinition.Width = new GridLength(Math.Max(0, width), GridUnitType.Pixel);

        if (progress < 1)
        {
            return;
        }

        var showRightPane = _editorPaneLayoutTransitionShowsRightPane;
        StopEditorPaneLayoutTransition();
        RightEditorPane.Visibility = showRightPane ? Visibility.Visible : Visibility.Collapsed;
        RightEditorColumnDefinition.Width = showRightPane
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
    }

    private void StopEditorPaneLayoutTransition()
    {
        if (_editorPaneLayoutTransitionTimer is null)
        {
            return;
        }

        _editorPaneLayoutTransitionTimer.Stop();
        _editorPaneLayoutTransitionTimer.Tick -= EditorPaneLayoutTransitionTimer_Tick;
        _editorPaneLayoutTransitionTimer = null;
    }

    private VapourSynthEditorPaneView GetActiveEditorPane()
    {
        return ViewModel.ActivePane == VapourSynthWorkspacePaneKind.Right
            && ViewModel.IsCompareMode
            && ViewModel.RightTab is not null
            ? RightEditorPane
            : LeftEditorPane;
    }

    private bool TryGetActiveEditorContext(
        out VapourSynthEditorPaneView pane,
        out VapourSynthWorkspaceTabViewModel tab)
    {
        pane = GetActiveEditorPane();
        tab = ViewModel.GetPaneTab(pane.PaneKind)!;
        return tab is not null && ViewModel.Tabs.Contains(tab);
    }

    private bool TryResolveBoundTab(
        VapourSynthEditorPaneView pane,
        VapourSynthEditorDocumentBinding? binding,
        out VapourSynthWorkspaceTabViewModel tab)
    {
        tab = null!;
        if (!pane.IsDocumentBindingConfirmed(binding))
        {
            return false;
        }

        var candidate = ViewModel.Tabs.FirstOrDefault(item =>
            string.Equals(item.Id, binding!.TabId, StringComparison.Ordinal));
        if (candidate is null
            || !ReferenceEquals(candidate, ViewModel.GetPaneTab(pane.PaneKind)))
        {
            return false;
        }

        tab = candidate;
        return true;
    }

    private static bool IsPaneBoundToTab(
        VapourSynthEditorPaneView pane,
        VapourSynthWorkspaceTabViewModel tab)
    {
        return pane.ConfirmedBinding is { } binding
            && pane.IsDocumentBindingConfirmed(binding)
            && string.Equals(binding.TabId, tab.Id, StringComparison.Ordinal);
    }

    private async Task<bool> ConfirmTabCloseAsync(VapourSynthWorkspaceTabViewModel tab)
    {
        if (!await CaptureTabEditorStateAsync(tab))
        {
            return false;
        }

        if (!tab.HasUnsavedChanges)
        {
            return true;
        }

        var choice = await ShowUnsavedChangesDialogAsync(this.XamlRoot);
        return choice switch
        {
            UnsavedChangesChoice.Save => await SaveTabAsync(tab),
            UnsavedChangesChoice.Discard => true,
            _ => false
        };
    }

    private async Task<bool> CaptureTabEditorStateAsync(VapourSynthWorkspaceTabViewModel tab)
    {
        if (IsPaneBoundToTab(LeftEditorPane, tab)
            && !await CaptureEditorStateAsync(LeftEditorPane))
        {
            return false;
        }

        if (ViewModel.IsCompareMode
            && IsPaneBoundToTab(RightEditorPane, tab)
            && !await CaptureEditorStateAsync(RightEditorPane))
        {
            return false;
        }

        return true;
    }

    private async Task<bool> SaveTabAsync(VapourSynthWorkspaceTabViewModel tab)
    {
        if (string.IsNullOrWhiteSpace(tab.CurrentFilePath))
        {
            var filePath = PickSaveFilePath(tab);

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            await ViewModel.SaveTabAsAsync(tab, filePath);
            return !tab.HasUnsavedChanges;
        }

        await ViewModel.SaveTabAsync(tab);
        return !tab.HasUnsavedChanges;
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

    private string? PickSaveFilePath(VapourSynthWorkspaceTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var currentFilePath = tab.CurrentFilePath;
        var suggestedExtension = string.Equals(Path.GetExtension(currentFilePath), ".py", StringComparison.OrdinalIgnoreCase)
            ? ".py"
            : ".vpy";
        var suggestedName = string.IsNullOrWhiteSpace(currentFilePath)
            ? Path.GetFileNameWithoutExtension(ViewModel.Texts.VapourSynthUntitledDocument)
            : Path.GetFileNameWithoutExtension(currentFilePath);

        var result = WindowInteractionHelper.PickSaveFilePath(
            GetWindowHandle(),
            ViewModel.Texts.SaveAsButton,
            currentFilePath ?? string.Empty,
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

    private static VapourSynthEditorDocumentBinding? GetBinding(JsonElement element)
    {
        if (!element.TryGetProperty("binding", out var property)
            || property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var binding = property.Deserialize<VapourSynthEditorDocumentBinding>(BridgeJsonOptions);
            return VapourSynthEditorBindingState.IsValid(binding) ? binding : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
        StopEditorPaneLayoutTransition();
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
            catch (Exception ex)
            {
                TryWriteDiagnosticException("ClosePreviewWindow", ex, AppDiagnosticSeverity.Warning);
            }
        }
    }

    private static void TryWriteDiagnosticException(
        string operationName,
        Exception exception,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Error)
    {
        try
        {
            App.GetService<IAppDiagnostics>().WriteException(
                nameof(VapourSynthWorkspaceView),
                operationName,
                exception,
                severity);
        }
        catch (Exception logException)
        {
            Debug.WriteLine($"Failed to write VapourSynth workspace diagnostic. {logException}");
            Debug.WriteLine(exception);
        }
    }

    private enum UnsavedChangesChoice
    {
        Save,
        Discard,
        Cancel
    }
}
