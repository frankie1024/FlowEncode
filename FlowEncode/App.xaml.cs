using FlowEncode.Application;
using FlowEncode.Infrastructure;
using FlowEncode.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace FlowEncode;

public partial class App : Microsoft.UI.Xaml.Application
{
    private const string AppUserModelId = "frankie1024.FlowEncode";
    private const string SingleInstanceKey = "FlowEncode.Main";
    private const string SingleInstancePipeName = "FlowEncode.VapourSynth.Open.v1";
    private const string SingleInstanceActivateMessage = "__FLOWENCODE_ACTIVATE__";
    private static readonly TimeSpan SingleInstanceForwardTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SingleInstanceForwardConnectTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SingleInstanceForwardInitialDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SingleInstanceForwardMaxDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SingleInstancePipeRestartInitialDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SingleInstancePipeRestartMaxDelay = TimeSpan.FromSeconds(5);
    private readonly ServiceProvider _services;
    private AppInstance? _mainAppInstance;
    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _singleInstancePipeCancellationTokenSource;
    private Task? _singleInstancePipeServerTask;
    private bool _ownsSingleInstanceMutex;
    private bool _isShuttingDown;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;

        _services = BuildServices();
    }

    public static T GetService<T>() where T : notnull
    {
        var app = (App)Microsoft.UI.Xaml.Application.Current;
        return app._services.GetRequiredService<T>();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            await LaunchAsync();
        }
        catch (Exception ex)
        {
            TryWriteAppExceptionDiagnostic("Launch application", ex, AppDiagnosticSeverity.Critical);
            TryWriteExceptionFile("startup-crash.log", ex, "startup crash");
            throw;
        }
    }

    private async Task LaunchAsync()
    {
        TrySetProcessAppUserModelId();

        if (!await TryConfigureSingleInstanceAsync())
        {
            return;
        }

        var launchActivation = GetService<AppLaunchActivation>();
        launchActivation.SetRequestedVapourSynthFilePath(ResolveRequestedVapourSynthFilePath());
        _window = GetService<MainWindow>();
        _window.Closed += MainWindow_Closed;

        try
        {
            var shellIntegration = GetService<IVapourSynthShellIntegrationService>();
            shellIntegration.RegisterNewVpyFileMenu();
        }
        catch (Exception ex)
        {
            TryWriteAppExceptionDiagnostic("Register .vpy ShellNew menu", ex, AppDiagnosticSeverity.Warning);
        }

        _window.Activate();
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        TryWriteAppExceptionDiagnostic("Unhandled XAML exception", e.Exception, AppDiagnosticSeverity.Critical);
        TryWriteExceptionFile("startup-crash.log", e.Exception, "startup crash");
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<LocalAppPaths>();
        services.AddSingleton<IFlowEncodeHttpClientFactory, FlowEncodeHttpClientFactory>();
        services.AddSingleton<IAppDiagnostics, LocalAppDiagnostics>();
        services.AddSingleton<AppLaunchActivation>();
        services.AddSingleton<LocalAppSettingsService>();
        services.AddSingleton<IAppSettingsService>(static provider => provider.GetRequiredService<LocalAppSettingsService>());
        services.AddSingleton<IQueueCompletionActionService, WindowsQueueCompletionActionService>();
        services.AddSingleton<ISystemIdleService, WindowsSystemIdleService>();
        services.AddSingleton<ISetupGuideCacheService, LocalSetupGuideCacheService>();
        services.AddSingleton<IToolRegistryService, DefaultToolRegistryService>();
        services.AddSingleton<IToolProbeService, ProcessToolProbeService>();
        services.AddSingleton<IEnvironmentReadinessService, EnvironmentReadinessService>();
        services.AddSingleton<IEncoderDiscoveryService, LocalEncoderDiscoveryService>();
        services.AddSingleton<ISetupBootstrapService, SetupBootstrapService>();
        services.AddSingleton<IEncoderToolchainService, LocalEncoderToolchainService>();
        services.AddSingleton<IExternalToolService, LocalExternalToolService>();
        services.AddSingleton<IAppUpdateService, GitHubAppUpdateService>();
        services.AddSingleton<IAudioSourceInfoService, FfprobeAudioSourceInfoService>();
        services.AddSingleton<IAudioProcessingRunner, CliAudioProcessingRunner>();
        services.AddSingleton<IBluRayDemuxBackendAdapter, DgDemuxBackendAdapter>();
        services.AddSingleton<IBluRayDemuxBackendAdapter, Eac3ToBackendAdapter>();
        services.AddSingleton<IBluRayDiscProbeService, CliBluRayDiscProbeService>();
        services.AddSingleton<IBluRayDemuxRunner, CliBluRayDemuxRunner>();
        services.AddSingleton<IProfileLibraryService, LocalProfileLibraryService>();
        services.AddSingleton<IEncodingJobRunner, LocalEncodingJobRunner>();
        services.AddSingleton<IAutoCompressionRunner, StructuredAv1anRunner>();
        services.AddSingleton<IEncoderUpdateService, GitHubReleaseEncoderUpdateService>();
        services.AddSingleton<IVapourSynthWorkspaceService, VapourSynthWorkspaceService>();
        services.AddSingleton<IVapourSynthWorkspaceLanguageService, VapourSynthWorkspaceLanguageService>();
        services.AddSingleton<IVapourSynthPreviewService, VapourSynthPreviewService>();
        services.AddSingleton<IVapourSynthShellIntegrationService, WindowsShellIntegrationService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<VapourSynthWorkspaceViewModel>();
        services.AddTransient<VapourSynthPreviewWindowViewModel>();
        services.AddTransient<VapourSynthPreviewWindow>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private async Task<bool> TryConfigureSingleInstanceAsync()
    {
        var requestedFilePath = ResolveRequestedVapourSynthFilePath();
        var mutexResult = TryAcquireSingleInstanceMutex();
        if (mutexResult == false)
        {
            await ForwardExternalActivationAndExitAsync(requestedFilePath, "single-instance mutex is owned by another process");
            return false;
        }

        _mainAppInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
        if (mutexResult is null && !_mainAppInstance.IsCurrent)
        {
            await ForwardExternalActivationAndExitAsync(requestedFilePath, "AppInstance is owned by another process");
            return false;
        }

        if (mutexResult == true && !_mainAppInstance.IsCurrent)
        {
            WriteLifecycleDiagnostic(
                "AppInstance owner differs from the single-instance mutex owner; trying to forward to existing AppInstance.",
                AppDiagnosticSeverity.Warning);

            if (await TrySendExternalOpenRequestAsync(requestedFilePath))
            {
                ShutdownServices();
                Environment.Exit(0);
                return false;
            }

            WriteLifecycleDiagnostic(
                "Existing AppInstance did not accept activation forwarding; continuing with mutex ownership.",
                AppDiagnosticSeverity.Warning);
        }

        StartSingleInstancePipeServer();
        return true;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        ShutdownServices();
    }

    private void TrySetProcessAppUserModelId()
    {
        _services
            .GetRequiredService<IAppDiagnostics>()
            .TryRun(
                nameof(App),
                "Set process AppUserModelID",
                () => SetCurrentProcessExplicitAppUserModelID(AppUserModelId),
                AppDiagnosticSeverity.Warning,
                DiagnosticContext(("appUserModelId", AppUserModelId)));
    }

    private static string? ResolveRequestedVapourSynthFilePath()
    {
        return Environment.GetCommandLineArgs()
            .Skip(1)
            .Select(AppLaunchActivation.NormalizeSupportedScriptPath)
            .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path));
    }

    private void StartSingleInstancePipeServer()
    {
        if (_singleInstancePipeServerTask is not null)
        {
            return;
        }

        _singleInstancePipeCancellationTokenSource = new CancellationTokenSource();
        _singleInstancePipeServerTask = _services
            .GetRequiredService<IAppDiagnostics>()
            .RunLoggedAsync(
                nameof(App),
                "Run single-instance pipe server",
                () => RunSingleInstancePipeServerAsync(_singleInstancePipeCancellationTokenSource.Token),
                AppDiagnosticSeverity.Error);
    }

    private void ShutdownServices()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        UnhandledException -= App_UnhandledException;

        if (_window is not null)
        {
            _window.Closed -= MainWindow_Closed;
            _window = null;
        }

        StopSingleInstancePipeServer();
        ReleaseSingleInstanceMutex();

        try
        {
            _services.Dispose();
        }
        catch (Exception ex)
        {
            TryWriteAppExceptionDiagnostic("Dispose application services", ex, AppDiagnosticSeverity.Error);
            TryWriteExceptionFile("shutdown-error.log", ex, "shutdown error");
        }
    }

    private void StopSingleInstancePipeServer()
    {
        var cancellationTokenSource = _singleInstancePipeCancellationTokenSource;
        _singleInstancePipeCancellationTokenSource = null;

        if (cancellationTokenSource is null)
        {
            return;
        }

        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            TryWriteAppExceptionDiagnostic(
                "Cancel single-instance pipe server",
                ex,
                AppDiagnosticSeverity.Warning);
        }

        var pipeServerTask = _singleInstancePipeServerTask;
        if (pipeServerTask is null || pipeServerTask.IsCompleted)
        {
            cancellationTokenSource.Dispose();
            return;
        }

        _ = pipeServerTask.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            cancellationTokenSource,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunSingleInstancePipeServerAsync(CancellationToken cancellationToken)
    {
        var retryDelay = SingleInstancePipeRestartInitialDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    SingleInstancePipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(server);
                var filePath = (await reader.ReadToEndAsync()).Trim();
                DispatchExternalOpenRequest(filePath);
                retryDelay = SingleInstancePipeRestartInitialDelay;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                var isPipeBusy = ex is IOException ioException && IsPipeBusy(ioException);
                TryWriteActivationErrorLog(
                    ex,
                    "Receive single-instance activation request",
                    isPipeBusy ? AppDiagnosticSeverity.Warning : AppDiagnosticSeverity.Error,
                    persistExceptionFile: !isPipeBusy);

                try
                {
                    await Task.Delay(retryDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                retryDelay = TimeSpan.FromMilliseconds(Math.Min(
                    retryDelay.TotalMilliseconds * 2,
                    SingleInstancePipeRestartMaxDelay.TotalMilliseconds));
            }
        }
    }

    private void DispatchExternalOpenRequest(string? pipePayload)
    {
        if (string.IsNullOrWhiteSpace(pipePayload))
        {
            return;
        }

        if (string.Equals(pipePayload, SingleInstanceActivateMessage, StringComparison.Ordinal))
        {
            if (_window is MainWindow windowToActivate)
            {
                if (!windowToActivate.DispatcherQueue.TryEnqueue(windowToActivate.BringToFront))
                {
                    WriteLifecycleDiagnostic(
                        "Failed to enqueue single-instance activation request.",
                        AppDiagnosticSeverity.Warning,
                        DiagnosticContext(("payload", pipePayload)));
                }
            }

            return;
        }

        var filePath = AppLaunchActivation.NormalizeSupportedScriptPath(pipePayload);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (_window is not MainWindow mainWindow)
        {
            GetService<AppLaunchActivation>().SetRequestedVapourSynthFilePath(filePath);
            return;
        }

        if (!mainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            _services
                .GetRequiredService<IAppDiagnostics>()
                .RunFireAndForget(
                    nameof(App),
                    "Handle external VapourSynth open request",
                    () => mainWindow.HandleExternalVapourSynthOpenAsync(filePath),
                    AppDiagnosticSeverity.Error,
                    DiagnosticContext(("filePath", filePath)));
        }))
        {
            WriteLifecycleDiagnostic(
                "Failed to enqueue external VapourSynth open request.",
                AppDiagnosticSeverity.Warning,
                DiagnosticContext(("filePath", filePath)));
        }
    }

    private async Task<bool> TrySendExternalOpenRequestAsync(string? filePath)
    {
        var normalizedPath = AppLaunchActivation.NormalizeSupportedScriptPath(filePath);
        var pipePayload = string.IsNullOrWhiteSpace(normalizedPath)
            ? SingleInstanceActivateMessage
            : normalizedPath;
        Exception? lastException = null;
        var startedAt = Stopwatch.GetTimestamp();
        var attempts = 0;
        var retryDelay = SingleInstanceForwardInitialDelay;

        while (Stopwatch.GetElapsedTime(startedAt) < SingleInstanceForwardTimeout)
        {
            try
            {
                attempts++;
                var remaining = SingleInstanceForwardTimeout - Stopwatch.GetElapsedTime(startedAt);
                var connectTimeout = remaining < SingleInstanceForwardConnectTimeout
                    ? remaining
                    : SingleInstanceForwardConnectTimeout;
                var connectTimeoutMilliseconds = Math.Max(1, (int)Math.Ceiling(connectTimeout.TotalMilliseconds));

                using var client = new NamedPipeClientStream(
                    ".",
                    SingleInstancePipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await client.ConnectAsync(connectTimeoutMilliseconds);

                using var writer = new StreamWriter(client)
                {
                    AutoFlush = true
                };

                await writer.WriteAsync(pipePayload);
                return true;
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
            }
            catch (IOException ex)
            {
                lastException = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastException = ex;
            }

            var remainingDelay = SingleInstanceForwardTimeout - Stopwatch.GetElapsedTime(startedAt);
            if (remainingDelay <= TimeSpan.Zero)
            {
                break;
            }

            var delay = retryDelay < remainingDelay
                ? retryDelay
                : remainingDelay;
            await Task.Delay(delay);

            retryDelay = TimeSpan.FromMilliseconds(Math.Min(
                retryDelay.TotalMilliseconds * 2,
                SingleInstanceForwardMaxDelay.TotalMilliseconds));
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        var context = DiagnosticContext(
            ("payload", pipePayload),
            ("attempts", attempts.ToString()),
            ("timeoutMs", ((int)SingleInstanceForwardTimeout.TotalMilliseconds).ToString()),
            ("elapsedMs", ((int)elapsed.TotalMilliseconds).ToString()));

        if (lastException is not null)
        {
            TryWriteAppExceptionDiagnostic(
                "Forward single-instance activation request",
                lastException,
                AppDiagnosticSeverity.Warning,
                context);
        }
        else
        {
            WriteLifecycleDiagnostic(
                "Failed to forward single-instance activation request to main instance within the retry window.",
                AppDiagnosticSeverity.Warning,
                context);
        }

        return false;
    }

    private async Task ForwardExternalActivationAndExitAsync(string? filePath, string reason)
    {
        try
        {
            _ = await TrySendExternalOpenRequestAsync(filePath);
        }
        catch (Exception ex)
        {
            TryWriteAppExceptionDiagnostic(
                "Forward external activation before exit",
                ex,
                AppDiagnosticSeverity.Warning,
                DiagnosticContext(("reason", reason)));
        }
        finally
        {
            WriteLifecycleDiagnostic(
                "Exiting secondary application instance.",
                AppDiagnosticSeverity.Information,
                DiagnosticContext(("reason", reason)));
            ShutdownServices();
            Environment.Exit(0);
        }
    }

    private bool? TryAcquireSingleInstanceMutex()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: false, BuildSingleInstanceMutexName());
            var acquired = false;

            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException ex)
            {
                acquired = true;
                TryWriteAppExceptionDiagnostic(
                    "Recover abandoned single-instance mutex",
                    ex,
                    AppDiagnosticSeverity.Warning);
            }

            if (!acquired)
            {
                mutex.Dispose();
                return false;
            }

            _singleInstanceMutex = mutex;
            _ownsSingleInstanceMutex = true;
            return true;
        }
        catch (Exception ex)
        {
            TryWriteAppExceptionDiagnostic(
                "Acquire single-instance mutex",
                ex,
                AppDiagnosticSeverity.Warning);
            return null;
        }
    }

    private void ReleaseSingleInstanceMutex()
    {
        var mutex = _singleInstanceMutex;
        _singleInstanceMutex = null;

        if (mutex is null)
        {
            return;
        }

        try
        {
            if (_ownsSingleInstanceMutex)
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            TryWriteAppExceptionDiagnostic(
                "Release single-instance mutex",
                ex,
                AppDiagnosticSeverity.Warning);
        }
        finally
        {
            _ownsSingleInstanceMutex = false;
            mutex.Dispose();
        }
    }

    private static string BuildSingleInstanceMutexName()
    {
        var userKey = Environment.UserName;
        try
        {
            userKey = WindowsIdentity.GetCurrent().User?.Value ?? userKey;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to resolve current user SID for single-instance mutex. {ex}");
        }

        var normalizedUserKey = new string(userKey
            .Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
            .ToArray());

        return $@"Local\FlowEncode.SingleInstance.frankie1024.{normalizedUserKey}";
    }

    private static bool IsPipeBusy(IOException exception)
    {
        const int ErrorPipeBusy = 231;
        return (exception.HResult & 0xFFFF) == ErrorPipeBusy;
    }

    private void TryWriteActivationErrorLog(
        Exception exception,
        string operationName,
        AppDiagnosticSeverity severity,
        bool persistExceptionFile)
    {
        TryWriteAppExceptionDiagnostic(operationName, exception, severity);
        if (persistExceptionFile)
        {
            TryWriteExceptionFile("activation-error.log", exception, "activation error");
        }
    }

    private void WriteLifecycleDiagnostic(
        string message,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Information,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            var diagnostics = _services.GetService<IAppDiagnostics>();
            if (diagnostics is not null)
            {
                diagnostics.Write(nameof(App), message, severity, context);
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write lifecycle diagnostic. {ex}");
        }

        TryWriteMessageFile("diagnostics-fallback.log", message, "lifecycle diagnostic");
    }

    private void TryWriteAppExceptionDiagnostic(
        string operationName,
        Exception exception,
        AppDiagnosticSeverity severity,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        try
        {
            var diagnostics = _services.GetService<IAppDiagnostics>();
            if (diagnostics is not null)
            {
                diagnostics.WriteException(nameof(App), operationName, exception, severity, context);
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write app exception diagnostic. {ex}");
        }

        TryWriteExceptionFile("diagnostics-fallback.log", exception, operationName);
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

    private static void TryWriteExceptionFile(string fileName, Exception exception, string description)
    {
        try
        {
            var crashPath = Path.Combine(GetFallbackCrashRoot(), fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
            File.AppendAllText(
                crashPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {description}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to persist {description} log. {ex}");
        }
    }

    private static void TryWriteMessageFile(string fileName, string message, string description)
    {
        try
        {
            var crashPath = Path.Combine(GetFallbackCrashRoot(), fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
            File.AppendAllText(
                crashPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to persist {description} log. {ex}");
        }
    }

    private static string GetFallbackCrashRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowEncode",
            "data",
            "logs");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}
