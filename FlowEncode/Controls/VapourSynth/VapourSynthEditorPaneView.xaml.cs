using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace FlowEncode.Controls.VapourSynth;

public sealed partial class VapourSynthEditorPaneView : UserControl, IDisposable
{
    private static readonly Uri EditorHostUri = new("https://vapoursynth-editor/index.html");
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly VapourSynthEditorBindingState _bindingState = new();
    private CancellationTokenSource? _readyTimeoutCancellationTokenSource;
    private bool _isCoreInitialized;
    private bool _isDisposed;
    private long _launchVersion;
    private string? _assetsRootPath;
    private string? _userDataFolderPath;

    public VapourSynthEditorPaneView()
    {
        InitializeComponent();
        GotFocus += EditorPane_GotFocus;
        PointerPressed += EditorPane_PointerPressed;
        EditorWebView.GotFocus += EditorPane_GotFocus;
        EditorWebView.PointerPressed += EditorPane_PointerPressed;
    }

    public VapourSynthWorkspacePaneKind PaneKind { get; set; } = VapourSynthWorkspacePaneKind.Left;

    public bool IsEditorReady { get; private set; }

    public VapourSynthEditorDocumentBinding? ConfirmedBinding => _bindingState.ConfirmedBinding;

    public event EventHandler? EditorReady;

    public event EventHandler<string>? LoadFailed;

    public event EventHandler? PaneActivated;

    public event EventHandler<VapourSynthEditorPaneSnapshot>? BufferChanged;

    public event EventHandler<VapourSynthEditorPaneSnapshot>? CursorChanged;

    public event EventHandler<VapourSynthEditorHostCommandRequest>? HostCommandRequested;

    public event EventHandler<JsonElement>? LanguageRequestReceived;

    public event EventHandler<string>? BridgeFailed;

    public async Task InitializeEditorAsync(string assetsRootPath, string userDataFolderPath, bool forceReload = false)
    {
        _assetsRootPath = assetsRootPath;
        _userDataFolderPath = userDataFolderPath;

        await _initializationLock.WaitAsync();
        try
        {
            IsEditorReady = false;
            _bindingState.Invalidate();
            ShowEditorOverlay(showRetryButton: false, showProgress: true);

            if (!_isCoreInitialized)
            {
                Directory.CreateDirectory(userDataFolderPath);
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolderPath, null);
                await EditorWebView.EnsureCoreWebView2Async(environment);
                ConfigureEditorWebView(Path.GetFullPath(assetsRootPath));
                _isCoreInitialized = true;
            }

            var launchVersion = Interlocked.Increment(ref _launchVersion);
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
            IsEditorReady = false;
            ShowEditorOverlay(showRetryButton: true, showProgress: false);
            LoadFailed?.Invoke(this, ex.Message);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<VapourSynthEditorPaneSnapshot?> CaptureStateAsync()
    {
        if (!IsEditorReady)
        {
            return null;
        }

        var scriptResult = await EditorWebView.ExecuteScriptAsync("window.vsWorkspaceHost.captureStateJson();");
        var stateJson = JsonSerializer.Deserialize<string>(scriptResult);
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(stateJson);
        return CreateSnapshot(document.RootElement);
    }

    public VapourSynthEditorDocumentBinding BeginDocumentLoad(string tabId)
    {
        if (!IsEditorReady)
        {
            throw new InvalidOperationException("Editor is not ready.");
        }

        var binding = _bindingState.BeginLoad(tabId);
        ShowEditorOverlay(showRetryButton: false, showProgress: true);
        return binding;
    }

    public async Task<VapourSynthEditorDocumentBinding?> LoadDocumentAsync(
        string text,
        string? filePath,
        VapourSynthEditorDocumentBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (!IsEditorReady)
        {
            return null;
        }

        var payload = JsonSerializer.Serialize(new
        {
            text,
            filePath,
            binding
        }, VapourSynthWorkspaceView.BridgeJsonOptions);

        var scriptResult = await EditorWebView.ExecuteScriptAsync(
            $"window.vsWorkspaceHost.loadDocument({payload}, {{ broadcastState: false }});");
        var result = JsonSerializer.Deserialize<VapourSynthEditorDocumentLoadResult>(
            scriptResult,
            VapourSynthWorkspaceView.BridgeJsonOptions);
        return result is { Loaded: true } ? result.Binding : null;
    }

    public bool TryConfirmDocumentLoad(
        VapourSynthEditorDocumentBinding expectedBinding,
        VapourSynthEditorDocumentBinding? acknowledgedBinding)
    {
        if (!_bindingState.TryConfirm(expectedBinding, acknowledgedBinding))
        {
            return false;
        }

        EditorOverlay.Visibility = Visibility.Collapsed;
        EditorWebView.Visibility = Visibility.Visible;
        return true;
    }

    public bool IsDocumentBindingConfirmed(VapourSynthEditorDocumentBinding? binding)
    {
        return IsEditorReady && _bindingState.IsConfirmed(binding);
    }

    public void FailDocumentLoad(VapourSynthEditorDocumentBinding binding, string message)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (!_bindingState.IsPending(binding))
        {
            return;
        }

        _bindingState.Invalidate();
        ShowEditorOverlay(showRetryButton: true, showProgress: false);
        LoadFailed?.Invoke(this, message);
    }

    public async Task LoadLanguageFeaturesAsync(object snapshot)
    {
        if (!IsEditorReady)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(snapshot, VapourSynthWorkspaceView.BridgeJsonOptions);
        await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.loadLanguageFeatures({payload});");
    }

    public async Task ApplyThemeAsync(ElementTheme actualTheme)
    {
        if (!IsEditorReady)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            theme = actualTheme == ElementTheme.Dark ? "dark" : "light"
        });

        await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.applyHostTheme({payload});");
    }

    public async Task ExecuteEditorCommandAsync(string command)
    {
        if (!IsEditorReady)
        {
            return;
        }

        var commandJson = JsonSerializer.Serialize(command);
        await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.executeCommand({commandJson});");
    }

    public async Task FocusEditorAsync()
    {
        if (!IsEditorReady)
        {
            return;
        }

        EditorWebView.Focus(FocusState.Programmatic);
        await ExecuteEditorCommandAsync("focus");
    }

    public async Task<bool> InsertTextAsync(string text, bool onNewLine)
    {
        if (!IsEditorReady)
        {
            return false;
        }

        var payload = JsonSerializer.Serialize(new
        {
            text,
            target = onNewLine ? "newLine" : "cursor"
        });

        var resultJson = await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.insertText({payload});");
        return JsonSerializer.Deserialize<bool>(resultJson);
    }

    public async Task<bool> InsertSnippetAsync(string snippet, bool onNewLine)
    {
        if (!IsEditorReady)
        {
            return false;
        }

        var payload = JsonSerializer.Serialize(new
        {
            snippet,
            target = onNewLine ? "newLine" : "cursor"
        });

        var resultJson = await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.insertSnippet({payload});");
        return JsonSerializer.Deserialize<bool>(resultJson);
    }

    public async Task SendLanguageResponseAsync(object payload)
    {
        if (!IsEditorReady)
        {
            return;
        }

        var responseJson = JsonSerializer.Serialize(payload, VapourSynthWorkspaceView.BridgeJsonOptions);
        await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.resolveLanguageRequest({responseJson});");
    }

    public async Task ApplyDiagnosticsAsync(object diagnostics)
    {
        if (!IsEditorReady)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(diagnostics, VapourSynthWorkspaceView.BridgeJsonOptions);
        await EditorWebView.ExecuteScriptAsync($"window.vsWorkspaceHost.applyDiagnostics({payload});");
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancelEditorReadyTimeout();
        _bindingState.Invalidate();

        if (_isCoreInitialized && EditorWebView.CoreWebView2 is not null)
        {
            EditorWebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
        }

        EditorWebView.NavigationCompleted -= EditorWebView_NavigationCompleted;
        GotFocus -= EditorPane_GotFocus;
        PointerPressed -= EditorPane_PointerPressed;
        EditorWebView.GotFocus -= EditorPane_GotFocus;
        EditorWebView.PointerPressed -= EditorPane_PointerPressed;
        try
        {
            EditorWebView.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to close VapourSynth editor WebView2. {ex}");
        }

        _initializationLock.Dispose();
    }

    private void ConfigureEditorWebView(string assetsRootPath)
    {
        EditorWebView.NavigationCompleted += EditorWebView_NavigationCompleted;

        var coreWebView2 = EditorWebView.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 core was not created.");

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
        IsEditorReady = false;
        _bindingState.Invalidate();
        ShowEditorOverlay(showRetryButton: true, showProgress: false);
        LoadFailed?.Invoke(this, args.WebErrorStatus.ToString());
    }

    private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            var messageType = GetString(root, "type");

            switch (messageType)
            {
                case "ready":
                    OnEditorReady();
                    break;
                case "bufferChanged":
                    BufferChanged?.Invoke(this, CreateSnapshot(root));
                    break;
                case "cursorChanged":
                    CursorChanged?.Invoke(this, CreateSnapshot(root));
                    break;
                case "hostCommand":
                    HostCommandRequested?.Invoke(
                        this,
                        new VapourSynthEditorHostCommandRequest(
                            GetString(root, "command"),
                            GetBinding(root)));
                    break;
                case "languageRequest":
                    LanguageRequestReceived?.Invoke(this, root.Clone());
                    break;
                case "bridgeError":
                    BridgeFailed?.Invoke(this, GetString(root, "message"));
                    break;
            }
        }
        catch (Exception ex)
        {
            BridgeFailed?.Invoke(this, ex.Message);
        }
    }

    private void OnEditorReady()
    {
        IsEditorReady = true;
        CancelEditorReadyTimeout();
        EditorReady?.Invoke(this, EventArgs.Empty);
    }

    private void RetryEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_assetsRootPath) && !string.IsNullOrWhiteSpace(_userDataFolderPath))
        {
            _ = InitializeEditorAsync(_assetsRootPath, _userDataFolderPath, forceReload: true);
        }
    }

    private void EditorPane_GotFocus(object sender, RoutedEventArgs e)
    {
        PaneActivated?.Invoke(this, EventArgs.Empty);
    }

    private void EditorPane_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PaneActivated?.Invoke(this, EventArgs.Empty);
    }

    private void ShowEditorOverlay(bool showRetryButton, bool showProgress)
    {
        EditorOverlay.Visibility = Visibility.Visible;
        EditorWebView.Visibility = Visibility.Collapsed;
        RetryEditorButton.Visibility = showRetryButton ? Visibility.Visible : Visibility.Collapsed;
        EditorLoadingProgressRing.IsActive = showProgress;
        EditorLoadingProgressRing.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartEditorReadyTimeout(long launchVersion)
    {
        CancelEditorReadyTimeout();
        _readyTimeoutCancellationTokenSource = new CancellationTokenSource();
        _ = WaitForEditorReadyAsync(launchVersion, _readyTimeoutCancellationTokenSource.Token);
    }

    private async Task WaitForEditorReadyAsync(long launchVersion, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

            if (launchVersion != Interlocked.Read(ref _launchVersion) || IsEditorReady)
            {
                return;
            }

            ShowEditorOverlay(showRetryButton: true, showProgress: false);
            LoadFailed?.Invoke(this, "Editor startup timed out.");
        }
        catch (OperationCanceledException)
        {
            // Expected when reload or disposal cancels the readiness timeout.
        }
    }

    private void CancelEditorReadyTimeout()
    {
        if (_readyTimeoutCancellationTokenSource is null)
        {
            return;
        }

        _readyTimeoutCancellationTokenSource.Cancel();
        _readyTimeoutCancellationTokenSource.Dispose();
        _readyTimeoutCancellationTokenSource = null;
    }

    private static VapourSynthEditorPaneSnapshot CreateSnapshot(JsonElement root)
    {
        return new VapourSynthEditorPaneSnapshot(
            GetString(root, "text"),
            GetInt(root, "line", 1),
            GetInt(root, "column", 1),
            GetInt(root, "lineCount", 1),
            GetInt(root, "charCount", 0),
            GetBinding(root));
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
            var binding = property.Deserialize<VapourSynthEditorDocumentBinding>(
                VapourSynthWorkspaceView.BridgeJsonOptions);
            return VapourSynthEditorBindingState.IsValid(binding) ? binding : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
}

public sealed record VapourSynthEditorPaneSnapshot(
    string Text,
    int Line,
    int Column,
    int LineCount,
    int CharCount,
    VapourSynthEditorDocumentBinding? Binding);

public sealed record VapourSynthEditorHostCommandRequest(
    string Command,
    VapourSynthEditorDocumentBinding? Binding);

internal sealed record VapourSynthEditorDocumentLoadResult(
    bool Loaded,
    VapourSynthEditorDocumentBinding? Binding);
