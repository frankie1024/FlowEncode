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
using System.Threading;
using System.Threading.Tasks;

namespace FlowEncode;

public partial class App : Microsoft.UI.Xaml.Application
{
    private const string AppUserModelId = "frankie1024.FlowEncode";
    private const string SingleInstanceKey = "FlowEncode.Main";
    private const string SingleInstancePipeName = "FlowEncode.VapourSynth.Open.v1";
    private const string SingleInstanceActivateMessage = "__FLOWENCODE_ACTIVATE__";
    private readonly ServiceProvider _services;
    private AppInstance? _mainAppInstance;
    private CancellationTokenSource? _singleInstancePipeCancellationTokenSource;
    private Task? _singleInstancePipeServerTask;
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
        services.AddSingleton<IAutoCompressionRunner, Av1anAutoCompressionRunner>();
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
        _mainAppInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
        if (_mainAppInstance.IsCurrent)
        {
            StartSingleInstancePipeServer();
            return true;
        }

        await TrySendExternalOpenRequestAsync(ResolveRequestedVapourSynthFilePath());
        ShutdownServices();
        Environment.Exit(0);
        return false;
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
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                TryWriteActivationErrorLog(ex, "Receive single-instance activation request");

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
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

    private async Task TrySendExternalOpenRequestAsync(string? filePath)
    {
        var normalizedPath = AppLaunchActivation.NormalizeSupportedScriptPath(filePath);
        var pipePayload = string.IsNullOrWhiteSpace(normalizedPath)
            ? SingleInstanceActivateMessage
            : normalizedPath;
        Exception? lastException = null;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    SingleInstancePipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await client.ConnectAsync(200);

                using var writer = new StreamWriter(client)
                {
                    AutoFlush = true
                };

                await writer.WriteAsync(pipePayload);
                return;
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
                await Task.Delay(150);
            }
            catch (IOException ex)
            {
                lastException = ex;
                await Task.Delay(150);
            }
        }

        var context = DiagnosticContext(("payload", pipePayload));
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
                "Failed to forward single-instance activation request to main instance after 20 attempts.",
                AppDiagnosticSeverity.Warning,
                context);
        }
    }

    private void TryWriteActivationErrorLog(Exception exception, string operationName)
    {
        TryWriteAppExceptionDiagnostic(operationName, exception, AppDiagnosticSeverity.Error);
        TryWriteExceptionFile("activation-error.log", exception, "activation error");
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
