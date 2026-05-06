using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinRT.Interop;

namespace FlowEncode.Controls.VapourSynth;

public sealed partial class VapourSynthWorkspaceView : UserControl, IDisposable
{
    private static readonly Uri EditorHostUri = new("https://vapoursynth-editor/index.html");
    private static readonly JsonSerializerOptions BridgeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private const double LogSectionMinHeight = 112;
    private const double LogSectionMaxHeight = 180;

    private readonly SemaphoreSlim _editorInitializationLock = new(1, 1);
    private readonly TaskCompletionSource<bool> _workspaceInitializedCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IVapourSynthWorkspaceLanguageService _languageService;
    private readonly IVapourSynthPreviewService _previewService;
    private readonly string _editorWebViewUserDataFolderPath;
    private VapourSynthPreviewWindow? _previewWindow;
    private CancellationTokenSource? _editorReadyTimeoutCancellationTokenSource;
    private CancellationTokenSource? _diagnosticsCancellationTokenSource;
    private bool _isLoaded;
    private bool _isCoreInitialized;
    private bool _isEditorReady;
    private long _editorLaunchVersion;
    private long _diagnosticsVersion;
    private bool _isDisposed;

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
        Unloaded += UserControl_Unloaded;
        _previewService.LogEmitted += PreviewService_LogEmitted;
    }

    public async Task<bool> PrepareForAppCloseAsync(XamlRoot xamlRoot)
    {
        await CaptureEditorStateAsync();

        if (!ViewModel.HasUnsavedChanges)
        {
            await ViewModel.FlushSessionAsync();
            return true;
        }

        var choice = await ShowUnsavedChangesDialogAsync(xamlRoot);
        return choice switch
        {
            UnsavedChangesChoice.Save => await SaveCurrentDocumentAsync(captureEditorState: false),
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

            if (!await TryConfirmDocumentSwitchAsync())
            {
                return;
            }

            await ViewModel.OpenDocumentAsync(normalizedPath);
            await PushDocumentToEditorAsync();
            await FocusEditorAsync();
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
            _workspaceInitializedCompletionSource.TrySetResult(true);
            await InitializeEditorAsync();
        }
        catch (Exception ex)
        {
            _workspaceInitializedCompletionSource.TrySetException(ex);
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorLoadFailedStatus(ex.Message));
            ShowEditorOverlay(showRetryButton: true, showProgress: false);
        }
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelEditorReadyTimeout();
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
            ShowEditorOverlay(showRetryButton: false, showProgress: false);
            return;
        }

        await _editorInitializationLock.WaitAsync();

        try
        {
            _isEditorReady = false;
            ViewModel.SetWorkspaceStatus(static texts => texts.VapourSynthEditorBootingStatus);
            ShowEditorOverlay(showRetryButton: false, showProgress: true);

            if (!_isCoreInitialized)
            {
                Directory.CreateDirectory(_editorWebViewUserDataFolderPath);
                var editorEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(
                    null,
                    _editorWebViewUserDataFolderPath,
                    null);
                await EditorWebView.EnsureCoreWebView2Async(editorEnvironment);
                ConfigureEditorWebView(Path.GetFullPath(assetsRootPath));
                _isCoreInitialized = true;
            }

            var launchVersion = Interlocked.Increment(ref _editorLaunchVersion);
            StartEditorReadyTimeout(launchVersion);

            if (forceReload && EditorWebView.CoreWebView2 is not null)
            {
                EditorWebView.CoreWebView2.Navigate(EditorHostUri.ToString());
            }
            else
            {
                EditorWebView.Source = EditorHostUri;
            }
        }
        catch (Exception ex)
        {
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorLoadFailedStatus(ex.Message));
            ShowEditorOverlay(showRetryButton: true, showProgress: false);
        }
        finally
        {
            _editorInitializationLock.Release();
        }
    }

    private void ConfigureEditorWebView(string assetsRootPath)
    {
        EditorWebView.NavigationCompleted += EditorWebView_NavigationCompleted;

        var coreWebView2 = EditorWebView.CoreWebView2;
        if (coreWebView2 is null)
        {
            throw new InvalidOperationException("WebView2 core was not created.");
        }

        coreWebView2.SetVirtualHostNameToFolderMapping(
            "vapoursynth-editor",
            assetsRootPath,
            CoreWebView2HostResourceAccessKind.Allow);

        coreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        coreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        coreWebView2.Settings.AreDevToolsEnabled = false;
        coreWebView2.Settings.IsStatusBarEnabled = false;
        coreWebView2.Settings.IsZoomControlEnabled = false;
        coreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
    }

    private void EditorWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            return;
        }

        CancelEditorReadyTimeout();
        _isEditorReady = false;
        ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorLoadFailedStatus(args.WebErrorStatus.ToString()));
        ShowEditorOverlay(showRetryButton: true, showProgress: false);
    }

    private async void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            var messageType = GetString(root, "type");

            switch (messageType)
            {
                case "ready":
                    await OnEditorReadyAsync();
                    break;
                case "bufferChanged":
                    ViewModel.ApplyEditorBuffer(
                        GetString(root, "text"),
                        GetInt(root, "line", 1),
                        GetInt(root, "column", 1),
                        GetInt(root, "lineCount", 1),
                        GetInt(root, "charCount", 0));
                    ScheduleDiagnostics();
                    break;
                case "cursorChanged":
                    ViewModel.ApplyCursorState(
                        GetInt(root, "line", 1),
                        GetInt(root, "column", 1),
                        GetInt(root, "lineCount", 1),
                        GetInt(root, "charCount", 0));
                    break;
                case "hostCommand":
                    await RunUiActionAsync(() => HandleHostCommandAsync(GetString(root, "command")));
                    break;
                case "languageRequest":
                    await HandleLanguageRequestAsync(root);
                    break;
                case "bridgeError":
                    ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorBridgeFailedStatus(GetString(root, "message")));
                    break;
            }
        }
        catch (Exception ex)
        {
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorBridgeFailedStatus(ex.Message));
        }
    }

    private async Task OnEditorReadyAsync()
    {
        _isEditorReady = true;
        CancelEditorReadyTimeout();
        EditorOverlay.Visibility = Visibility.Collapsed;
        EditorWebView.Visibility = Visibility.Visible;
        ViewModel.SetWorkspaceStatus(static texts => texts.VapourSynthEditorReadyStatus);
        await ApplyEditorThemeAsync(ActualTheme);
        await LoadLanguageFeaturesAsync();
        await PushDocumentToEditorAsync();
        await FocusEditorAsync();
        _ = WarmupPythonLanguageServerAsync();
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

    private async void RetryEditorButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => InitializeEditorAsync(forceReload: true));
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

    private async void ReloadDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            if (!await TryConfirmDocumentSwitchAsync())
            {
                return;
            }

            await ViewModel.ReloadDocumentAsync();
            await PushDocumentToEditorAsync();
        });
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
        await CaptureEditorStateAsync();

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
        if (!await TryConfirmDocumentSwitchAsync())
        {
            return;
        }

        await ViewModel.CreateNewDocumentAsync();
        await PushDocumentToEditorAsync();
    }

    private async Task OpenDocumentAsync()
    {
        if (!await TryConfirmDocumentSwitchAsync())
        {
            return;
        }

        var filePath = PickOpenFilePath();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        await ViewModel.OpenDocumentAsync(filePath);
        await PushDocumentToEditorAsync();
    }

    private async Task<bool> SaveCurrentDocumentAsync(bool captureEditorState = true)
    {
        if (captureEditorState)
        {
            await CaptureEditorStateAsync();
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
            await CaptureEditorStateAsync();
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
        await CaptureEditorStateAsync();
        ViewModel.ClearPreviewLog();
        var request = CreatePreviewOpenRequest(out var displayName);
        var previewWindow = GetOrCreatePreviewWindow();

        var mainWindowViewModel = App.GetService<MainWindow>().ViewModel;
        var opened = await previewWindow.OpenOrRefreshAsync(
            request,
            mainWindowViewModel.SettingsModule.CurrentLanguagePreference,
            mainWindowViewModel.SettingsModule.CurrentThemePreference);

        if (opened)
        {
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthPreviewOpenedStatus(displayName));
            return;
        }

        ViewModel.SetWorkspaceStatus(static texts => texts.VapourSynthPreviewEvaluationFailedStatus);
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

    private async Task<bool> TryConfirmDocumentSwitchAsync()
    {
        await CaptureEditorStateAsync();

        if (!ViewModel.HasUnsavedChanges)
        {
            return true;
        }

        var choice = await ShowUnsavedChangesDialogAsync(this.XamlRoot);
        return choice switch
        {
            UnsavedChangesChoice.Save => await SaveCurrentDocumentAsync(captureEditorState: false),
            UnsavedChangesChoice.Discard => await FlushDiscardedStateAsync(),
            _ => false
        };
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

    private VapourSynthPreviewOpenRequest CreatePreviewOpenRequest(out string displayName)
    {
        var sourceFilePath = ViewModel.CurrentFilePath;
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
            ViewModel.CurrentContent,
            normalizedWorkingDirectory);
    }

    private void DetachPreviewWindow(VapourSynthPreviewWindow previewWindow)
    {
        previewWindow.PreviewWindowClosed -= PreviewWindow_PreviewWindowClosed;
        if (ReferenceEquals(_previewWindow, previewWindow))
        {
            _previewWindow = null;
        }
    }

    private void PreviewService_LogEmitted(object? sender, VapourSynthPreviewLogEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed)
            {
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

    private async Task CaptureEditorStateAsync()
    {
        if (!_isEditorReady)
        {
            return;
        }

        try
        {
            var scriptResult = await EditorWebView.ExecuteScriptAsync("window.vsWorkspaceHost.captureStateJson();");
            var stateJson = JsonSerializer.Deserialize<string>(scriptResult);
            if (string.IsNullOrWhiteSpace(stateJson))
            {
                return;
            }

            using var document = JsonDocument.Parse(stateJson);
            var root = document.RootElement;
            ViewModel.ApplyEditorBuffer(
                GetString(root, "text"),
                GetInt(root, "line", 1),
                GetInt(root, "column", 1),
                GetInt(root, "lineCount", 1),
                GetInt(root, "charCount", 0));
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

    private async Task LoadLanguageFeaturesAsync(bool forceRefresh = false)
    {
        if (!_isEditorReady)
        {
            return;
        }

        var snapshot = await _languageService.GetLanguageFeaturesAsync(forceRefresh, CancellationToken.None);
        if (!snapshot.IsRuntimeReady)
        {
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthLanguageRuntimeUnavailableStatus(snapshot.RuntimeSummary));
        }

        var payload = JsonSerializer.Serialize(snapshot, BridgeJsonOptions);
        await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.loadLanguageFeatures({payload});");
    }

    private async Task ApplyEditorThemeAsync(ElementTheme actualTheme)
    {
        if (!_isEditorReady)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                theme = actualTheme == ElementTheme.Dark ? "dark" : "light"
            });

            await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.applyHostTheme({payload});");
        }
        catch (Exception ex)
        {
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorBridgeFailedStatus(ex.Message));
        }
    }

    private async Task PushDocumentToEditorAsync()
    {
        if (!_isEditorReady)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            text = ViewModel.CurrentContent,
            filePath = ViewModel.CurrentFilePath
        });

        await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.loadDocument({payload});");
    }

    private async Task FocusEditorAsync()
    {
        if (!_isEditorReady)
        {
            return;
        }

        await ExecuteEditorCommandAsync("focus");
    }

    public async Task InsertTextIntoEditorAsync(string text, bool onNewLine = false)
    {
        if (!_isEditorReady)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            text,
            target = onNewLine ? "newLine" : "cursor"
        });

        await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.insertText({payload});");
        await FocusEditorAsync();
    }

    public async Task InsertSnippetIntoEditorAsync(string snippet, bool onNewLine = false)
    {
        if (!_isEditorReady)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            snippet,
            target = onNewLine ? "newLine" : "cursor"
        });

        await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.insertSnippet({payload});");
        await FocusEditorAsync();
    }

    private async Task ExecuteEditorCommandAsync(string command)
    {
        if (!_isEditorReady)
        {
            return;
        }

        var commandJson = JsonSerializer.Serialize(command);
        await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.executeCommand({commandJson});");
    }

    private void ScheduleDiagnostics()
    {
        if (!_isEditorReady || _isDisposed)
        {
            return;
        }

        CancelPendingDiagnostics();
        _diagnosticsCancellationTokenSource = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _diagnosticsVersion);
        _ = UpdateDiagnosticsAfterDelayAsync(version, _diagnosticsCancellationTokenSource.Token);
    }

    private async Task HandleLanguageRequestAsync(JsonElement root)
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

            await SendLanguageResponseAsync(new
            {
                requestId,
                success = true,
                result
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SendLanguageResponseAsync(new
            {
                requestId,
                success = false,
                error = ex.Message
            });
        }
    }

    private async Task SendLanguageResponseAsync(object payload)
    {
        if (!_isEditorReady || _isDisposed)
        {
            return;
        }

        var responseJson = JsonSerializer.Serialize(payload, BridgeJsonOptions);
        await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.resolveLanguageRequest({responseJson});");
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
                || !_isEditorReady)
            {
                return;
            }

            var payload = JsonSerializer.Serialize(diagnostics, BridgeJsonOptions);
            await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.applyDiagnostics({payload});");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ViewModel.SetWorkspaceStatus(texts => texts.VapourSynthEditorBridgeFailedStatus(ex.Message));
        }
    }

    private void StartEditorReadyTimeout(long launchVersion)
    {
        CancelEditorReadyTimeout();
        _editorReadyTimeoutCancellationTokenSource = new CancellationTokenSource();
        _ = WaitForEditorReadyAsync(launchVersion, _editorReadyTimeoutCancellationTokenSource.Token);
    }

    private async Task WaitForEditorReadyAsync(long launchVersion, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

            if (launchVersion != Interlocked.Read(ref _editorLaunchVersion) || _isEditorReady)
            {
                return;
            }

            ViewModel.SetWorkspaceStatus(static texts => texts.VapourSynthEditorTimeoutStatus);
            ShowEditorOverlay(showRetryButton: true, showProgress: false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelEditorReadyTimeout()
    {
        if (_editorReadyTimeoutCancellationTokenSource is null)
        {
            return;
        }

        _editorReadyTimeoutCancellationTokenSource.Cancel();
        _editorReadyTimeoutCancellationTokenSource.Dispose();
        _editorReadyTimeoutCancellationTokenSource = null;
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

    private void ShowEditorOverlay(bool showRetryButton, bool showProgress)
    {
        EditorOverlay.Visibility = Visibility.Visible;
        EditorWebView.Visibility = Visibility.Collapsed;
        RetryEditorButton.Visibility = showRetryButton ? Visibility.Visible : Visibility.Collapsed;
        EditorLoadingProgressRing.IsActive = showProgress;
        EditorLoadingProgressRing.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
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
        CancelEditorReadyTimeout();
        CancelPendingDiagnostics();
        _previewService.LogEmitted -= PreviewService_LogEmitted;

        if (_isCoreInitialized && EditorWebView.CoreWebView2 is not null)
        {
            EditorWebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
        }

        EditorWebView.NavigationCompleted -= EditorWebView_NavigationCompleted;
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

        _editorInitializationLock.Dispose();
    }

    private enum UnsavedChangesChoice
    {
        Save,
        Discard,
        Cancel
    }
}
