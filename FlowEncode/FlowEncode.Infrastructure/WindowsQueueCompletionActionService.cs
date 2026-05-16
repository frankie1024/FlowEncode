using System.Diagnostics;
using System.Runtime.InteropServices;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class WindowsQueueCompletionActionService : IQueueCompletionActionService
{
    private readonly LocalAppPaths _appPaths;

    public WindowsQueueCompletionActionService(LocalAppPaths appPaths)
    {
        _appPaths = appPaths;
    }

    public Task<string?> ExecuteAsync(QueueCompletionAction action)
    {
        try
        {
            switch (action)
            {
                case QueueCompletionAction.None:
                    return Task.FromResult<string?>(null);

                case QueueCompletionAction.Sleep:
                    return Task.FromResult(SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false)
                        ? null
                        : "Failed to enter sleep mode.");

                case QueueCompletionAction.Shutdown:
                {
                    using var shutdownProcess = Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    return Task.FromResult<string?>(null);
                }

                default:
                    return Task.FromResult<string?>($"Unsupported queue completion action: {action}.");
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Write(
                _appPaths,
                nameof(WindowsQueueCompletionActionService),
                $"Failed to execute queue completion action '{action}'. {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult<string?>(ex.Message);
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
