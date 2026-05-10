using FlowEncode.Application;
using FlowEncode.Infrastructure;
using FlowEncode.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml;
using System;
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
            WriteLifecycleDiagnostic($"Failed to register .vpy ShellNew menu. {ex.GetType().Name}: {ex.Message}");
        }

        _window.Activate();
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var paths = _services.GetService<LocalAppPaths>();
            var crashRoot = paths?.LogsRootPath
                ?? GetFallbackCrashRoot();
            var crashPath = Path.Combine(crashRoot, "startup-crash.log");

            Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
            File.WriteAllText(crashPath, e.Exception.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to persist startup crash log. {ex}");
        }
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<LocalAppPaths>();
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
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (Exception ex)
        {
            WriteLifecycleDiagnostic($"Failed to set AppUserModelID '{AppUserModelId}'. {ex.GetType().Name}: {ex.Message}");
        }
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
        _singleInstancePipeServerTask = RunSingleInstancePipeServerAsync(_singleInstancePipeCancellationTokenSource.Token);
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
            TryWriteShutdownErrorLog(ex);
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
            WriteLifecycleDiagnostic($"Single-instance pipe cancellation source was already disposed during shutdown. {ex.GetType().Name}: {ex.Message}");
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
                TryWriteActivationErrorLog(ex);

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

    private void DispatchExternalOpenRequest(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (_window is not MainWindow mainWindow)
        {
            GetService<AppLaunchActivation>().SetRequestedVapourSynthFilePath(filePath);
            return;
        }

        mainWindow.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await mainWindow.HandleExternalVapourSynthOpenAsync(filePath);
            }
            catch (Exception ex)
            {
                TryWriteActivationErrorLog(ex);
            }
        });
    }

    private static async Task TrySendExternalOpenRequestAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var normalizedPath = AppLaunchActivation.NormalizeSupportedScriptPath(filePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

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

                await writer.WriteAsync(normalizedPath);
                return;
            }
            catch (TimeoutException)
            {
                await Task.Delay(150);
            }
            catch (IOException)
            {
                await Task.Delay(150);
            }
        }

        TryWriteSecondaryActivationDiagnostic(
            $"Failed to forward external open request to main instance after 20 attempts. Path='{normalizedPath}'.");
    }

    private void TryWriteActivationErrorLog(Exception exception)
    {
        try
        {
            var paths = _services.GetService<LocalAppPaths>();
            var crashRoot = paths?.LogsRootPath
                ?? GetFallbackCrashRoot();
            var crashPath = Path.Combine(crashRoot, "activation-error.log");

            Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
            File.WriteAllText(crashPath, exception.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to persist activation error log. {ex}");
        }
    }

    private void WriteLifecycleDiagnostic(string message)
    {
        try
        {
            var paths = _services.GetService<LocalAppPaths>();
            if (paths is not null)
            {
                AppDiagnosticsLog.Write(paths, nameof(App), message);
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write lifecycle diagnostic. {ex}");
        }

        TryWriteShutdownErrorLog(new InvalidOperationException(message));
    }

    private static void TryWriteShutdownErrorLog(Exception exception)
    {
        try
        {
            var crashPath = Path.Combine(GetFallbackCrashRoot(), "shutdown-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
            File.WriteAllText(crashPath, exception.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to persist shutdown error log. {ex}");
        }
    }

    private static void TryWriteSecondaryActivationDiagnostic(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            var crashPath = Path.Combine(GetFallbackCrashRoot(), "activation-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
            File.AppendAllText(
                crashPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to persist secondary activation diagnostic. {ex}");
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
