using System.Diagnostics;
using System.Text;

namespace FlowEncode.Infrastructure;

internal static class ProcessProbeRunner
{
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    public static ProcessProbeResult Run(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        string timeoutMessage,
        CancellationToken cancellationToken = default,
        Func<string?, string>? standardErrorLineNormalizer = null,
        Action<string>? standardErrorProgress = null)
    {
        return RunAsync(
                startInfo,
                timeout,
                timeoutMessage,
                cancellationToken,
                standardErrorLineNormalizer,
                standardErrorProgress)
            .GetAwaiter()
            .GetResult();
    }

    public static async Task<ProcessProbeResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        string timeoutMessage,
        CancellationToken cancellationToken = default,
        Func<string?, string>? standardErrorLineNormalizer = null,
        Action<string>? standardErrorProgress = null)
    {
        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
        {
            throw new InvalidOperationException("Process probe requires redirected stdout and stderr.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Process probe timeout must be positive.");
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = standardErrorLineNormalizer is null && standardErrorProgress is null
            ? process.StandardError.ReadToEndAsync()
            : ReadStandardErrorLinesAsync(
                process.StandardError,
                standardErrorLineNormalizer,
                standardErrorProgress);

        try
        {
            await WaitForExitOrTerminateAsync(process, timeout, timeoutMessage, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryObserveOutputTasksAsync(startInfo.FileName, outputTask, errorTask).ConfigureAwait(false);
            throw;
        }

        await AwaitOutputTasksAsync(startInfo.FileName, outputTask, errorTask).ConfigureAwait(false);
        return new ProcessProbeResult(outputTask.Result, errorTask.Result, process.ExitCode);
    }

    private static async Task<string> ReadStandardErrorLinesAsync(
        StreamReader reader,
        Func<string?, string>? lineNormalizer,
        Action<string>? progress)
    {
        var builder = new StringBuilder();

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var normalized = lineNormalizer?.Invoke(line) ?? line;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(normalized);
            progress?.Invoke(normalized);
        }

        return builder.ToString();
    }

    private static async Task WaitForExitOrTerminateAsync(
        Process process,
        TimeSpan timeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await TryWaitAfterKillAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            TryKillProcessTree(process);
            await TryWaitAfterKillAsync(process).ConfigureAwait(false);
            throw new InvalidOperationException(timeoutMessage, ex);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to terminate process '{process.StartInfo.FileName}'. {ex}");
        }
    }

    private static async Task TryWaitAfterKillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(OutputDrainTimeout)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to observe terminated process '{process.StartInfo.FileName}'. {ex}");
        }
    }

    private static async Task AwaitOutputTasksAsync(
        string fileName,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask)
                .WaitAsync(OutputDrainTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"进程输出读取超时：{fileName}", ex);
        }
    }

    private static async Task TryObserveOutputTasksAsync(
        string fileName,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask)
                .WaitAsync(OutputDrainTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to drain process output for '{fileName}'. {ex}");
        }
    }
}

internal sealed record ProcessProbeResult(
    string StandardOutput,
    string StandardError,
    int ExitCode);
