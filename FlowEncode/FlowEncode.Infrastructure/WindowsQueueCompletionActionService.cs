using System.Diagnostics;
using System.Runtime.InteropServices;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class WindowsQueueCompletionActionService : IQueueCompletionActionService
{
    private const int SmShuttingDown = 0x2000;
    private static readonly TimeSpan ShutdownHandoffTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly LocalAppPaths _appPaths;

    public WindowsQueueCompletionActionService(LocalAppPaths appPaths)
    {
        _appPaths = appPaths;
    }

    public async Task<string?> ExecuteAsync(QueueCompletionAction action)
    {
        try
        {
            switch (action)
            {
                case QueueCompletionAction.None:
                    return null;

                case QueueCompletionAction.Sleep:
                    return SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false)
                        ? null
                        : "Failed to enter sleep mode.";

                case QueueCompletionAction.Shutdown:
                {
                    return await ExecuteShutdownAsync(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/s /t 0")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    }, () => GetSystemMetrics(SmShuttingDown) != 0, ShutdownHandoffTimeout).ConfigureAwait(false);
                }

                default:
                    return $"Unsupported queue completion action: {action}.";
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Write(
                _appPaths,
                nameof(WindowsQueueCompletionActionService),
                $"Failed to execute queue completion action '{action}'. {ex.GetType().Name}: {ex.Message}");
            return ex.Message;
        }
    }

    internal static async Task<string?> ExecuteShutdownAsync(ProcessStartInfo startInfo, Func<bool> isShuttingDown, TimeSpan handoffTimeout)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Windows shutdown command.");
        var processExit = process.WaitForExitAsync();
        var sawShutdown = false;
        while (!processExit.IsCompleted)
        {
            sawShutdown |= isShuttingDown();
            await Task.WhenAny(processExit, Task.Delay(ShutdownPollInterval)).ConfigureAwait(false);
        }

        await processExit.ConfigureAwait(false);
        if (process.ExitCode != 0)
            return $"The Windows shutdown command failed (exit code {process.ExitCode}).";

        var handoffStarted = Stopwatch.GetTimestamp();
        while (!isShuttingDown())
        {
            if (sawShutdown)
                return "Windows cancelled shutdown.";
            if (Stopwatch.GetElapsedTime(handoffStarted) >= handoffTimeout)
                return "Windows did not start shutting down.";
            await Task.Delay(ShutdownPollInterval).ConfigureAwait(false);
        }

        // A successful command can exit before Windows closes the desktop session.
        // Keep the queue closed while Windows completes shutdown or the user cancels it.
        do
        {
            await Task.Delay(ShutdownPollInterval).ConfigureAwait(false);
        }
        while (isShuttingDown());
        return "Windows cancelled shutdown.";
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
