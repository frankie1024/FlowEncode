using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public abstract class CliBluRayDemuxBackendAdapterBase : IBluRayDemuxBackendAdapter
{
    private const string TempWorkspaceFolderName = ".flowencode-temp";
    private const int MaxLogLength = 240_000;
    private readonly IToolProbeService _toolProbeService;
    private readonly ConcurrentDictionary<Guid, ManagedProcessExecution> _activeExecutions = new();

    protected CliBluRayDemuxBackendAdapterBase(IToolProbeService toolProbeService)
    {
        _toolProbeService = toolProbeService;
    }

    public abstract BluRayDemuxBackend Backend { get; }

    public abstract Task<IReadOnlyList<BluRayPlaylistItem>> ScanDiscAsync(
        string discPath,
        CancellationToken cancellationToken = default);

    public abstract Task<BluRayPlaylistScanResult> ScanPlaylistAsync(
        string discPath,
        BluRayPlaylistItem playlist,
        CancellationToken cancellationToken = default);

    public abstract Task<BluRayDemuxResult> RunAsync(
        BluRayDemuxRequest request,
        IProgress<BluRayDemuxProgress>? progress = null,
        CancellationToken cancellationToken = default);

    public abstract string BuildDisplayCommand(BluRayDemuxRequest request);

    public void Abort(Guid jobId)
    {
        if (_activeExecutions.TryRemove(jobId, out var execution))
        {
            execution.Terminate();
        }
    }

    protected async Task<string> ResolveToolPathAsync(RegisteredToolKind kind, CancellationToken cancellationToken)
    {
        var result = await _toolProbeService.ProbeAsync(kind, cancellationToken);
        if (result.IsReady && !string.IsNullOrWhiteSpace(result.ExecutablePath))
        {
            return result.ExecutablePath;
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.FailureReason)
            ? $"未找到可用的 {kind.ToDisplayName()}。"
            : result.FailureReason);
    }

    protected static ProcessStartInfo CreateStartInfo(string executablePath, string? workingDirectory = null)
    {
        return new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
                ?? Path.GetDirectoryName(executablePath)
                ?? AppContext.BaseDirectory
        };
    }

    protected async Task<ProcessCaptureResult> CaptureProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();
        using var execution = new ManagedProcessExecution(process);
        using var registration = cancellationToken.Register(static state =>
        {
            if (state is ManagedProcessExecution activeExecution)
            {
                activeExecution.Terminate();
            }
        }, execution);
        var stdOutTask = ReadLinesAsync(process.StandardOutput, outputBuilder, null, cancellationToken);
        var stdErrTask = ReadLinesAsync(process.StandardError, outputBuilder, null, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdOutTask, stdErrTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            execution.Terminate();
            throw;
        }

        return new ProcessCaptureResult(process.ExitCode, outputBuilder.ToString().Trim());
    }

    protected async Task<BluRayDemuxResult> RunProcessAsync(
        BluRayDemuxRequest request,
        string displayCommand,
        ProcessStartInfo startInfo,
        Func<string, double?> progressParser,
        Func<string, bool>? successLineDetector,
        string startSummary,
        string completedSummary,
        string cancelledSummary,
        string failedSummary,
        IProgress<BluRayDemuxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var logBuilder = new StringBuilder();
        var gate = new object();
        var lastReportedProgress = 0.0;
        var hasKnownProgress = false;
        var lastDetailLine = string.Empty;

        void HandleLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (logBuilder.Length < MaxLogLength)
            {
                logBuilder.AppendLine(line);
            }

            lock (gate)
            {
                lastDetailLine = line;
                var parsedProgress = progressParser(line);
                if (parsedProgress.HasValue)
                {
                    hasKnownProgress = true;
                    lastReportedProgress = Math.Max(lastReportedProgress, Math.Clamp(parsedProgress.Value, 0.0, 1.0));
                }
            }

            progress?.Report(new BluRayDemuxProgress(
                request.JobId,
                EncodingJobState.Running,
                hasKnownProgress ? lastReportedProgress : null,
                hasKnownProgress ? $"{startSummary} {lastReportedProgress * 100:0.#}%" : startSummary,
                line));
        }

        Process? process = null;
        ManagedProcessExecution? activeExecution = null;
        Task pumpOutput = Task.CompletedTask;
        Task pumpError = Task.CompletedTask;
        var exitCode = -1;
        var hasExitCode = false;

        progress?.Report(new BluRayDemuxProgress(
            request.JobId,
            EncodingJobState.Running,
            null,
            startSummary,
            string.Empty));

        try
        {
            process = new Process
            {
                StartInfo = startInfo
            };

            process.Start();
            activeExecution = new ManagedProcessExecution(process);
            _activeExecutions[request.JobId] = activeExecution;

            using var registration = cancellationToken.Register(static state =>
            {
                if (state is ManagedProcessExecution execution)
                {
                    execution.Terminate();
                }
            }, activeExecution);
            pumpOutput = ReadLinesAsync(process.StandardOutput, null, HandleLine, cancellationToken);
            pumpError = ReadLinesAsync(process.StandardError, null, HandleLine, cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            activeExecution.Terminate();
            await Task.WhenAll(pumpOutput, pumpError);
            hasExitCode = TryGetExitCode(process, out exitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activeExecution?.Terminate();

            try
            {
                await Task.WhenAll(pumpOutput, pumpError);
            }
            catch (OperationCanceledException)
            {
                // Expected while draining cancelled process output.
            }

            hasExitCode = TryGetExitCode(process, out exitCode);

            var cancelledLog = logBuilder.ToString().Trim();
            progress?.Report(new BluRayDemuxProgress(
                request.JobId,
                EncodingJobState.Cancelled,
                hasKnownProgress ? lastReportedProgress : null,
                cancelledSummary,
                lastDetailLine));

            return new BluRayDemuxResult(
                request.JobId,
                EncodingJobState.Cancelled,
                hasExitCode ? exitCode : -1,
                string.IsNullOrWhiteSpace(lastDetailLine) ? cancelledSummary : lastDetailLine,
                cancelledLog,
                displayCommand,
                request.Selections.Select(static selection => selection.OutputPath).ToList());
        }
        finally
        {
            _activeExecutions.TryRemove(request.JobId, out _);
            if (activeExecution is not null)
            {
                activeExecution.Dispose();
            }
            else
            {
                process?.Dispose();
            }
        }

        var log = logBuilder.ToString().Trim();
        var lastMeaningfulLine = GetLastMeaningfulLine(log);
        var reportedSuccess = HasSuccessfulTerminalLine(log, successLineDetector);

        if ((hasExitCode && exitCode == 0) || (!hasExitCode && reportedSuccess))
        {
            progress?.Report(new BluRayDemuxProgress(
                request.JobId,
                EncodingJobState.Completed,
                1.0,
                completedSummary,
                lastMeaningfulLine));

            return new BluRayDemuxResult(
                request.JobId,
                EncodingJobState.Completed,
                hasExitCode ? exitCode : 0,
                string.IsNullOrWhiteSpace(lastMeaningfulLine) ? completedSummary : lastMeaningfulLine,
                log,
                displayCommand,
                request.Selections.Select(static selection => selection.OutputPath).ToList());
        }

        if (!hasExitCode)
        {
            var exitStateUnavailableDetail = string.IsNullOrWhiteSpace(lastMeaningfulLine)
                ? "无法读取进程退出状态。"
                : $"{lastMeaningfulLine}{Environment.NewLine}无法读取进程退出状态。";

            progress?.Report(new BluRayDemuxProgress(
                request.JobId,
                EncodingJobState.Failed,
                hasKnownProgress ? lastReportedProgress : null,
                failedSummary,
                exitStateUnavailableDetail));

            return new BluRayDemuxResult(
                request.JobId,
                EncodingJobState.Failed,
                -1,
                exitStateUnavailableDetail,
                log,
                displayCommand,
                request.Selections.Select(static selection => selection.OutputPath).ToList());
        }

        progress?.Report(new BluRayDemuxProgress(
            request.JobId,
            EncodingJobState.Failed,
            hasKnownProgress ? lastReportedProgress : null,
            failedSummary,
            lastMeaningfulLine));

        return new BluRayDemuxResult(
            request.JobId,
            EncodingJobState.Failed,
            exitCode,
            string.IsNullOrWhiteSpace(lastMeaningfulLine) ? failedSummary : lastMeaningfulLine,
            log,
            displayCommand,
            request.Selections.Select(static selection => selection.OutputPath).ToList());
    }

    protected static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    protected static string CreateStagedOutputDirectory(string finalOutputDirectory, string scope, Guid jobId)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(finalOutputDirectory)
            ? Environment.CurrentDirectory
            : (Path.GetDirectoryName(finalOutputDirectory) ?? Environment.CurrentDirectory);
        return Path.Combine(baseDirectory, TempWorkspaceFolderName, scope, jobId.ToString("N"));
    }

    protected static void CleanupStagedOutputDirectory(string stagedOutputDirectory)
    {
        ExecutionOutputStaging.CleanupStagedDirectory(
            stagedOutputDirectory,
            emptyParentLevels: 2);
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        StringBuilder? sink,
        Action<string>? lineHandler,
        CancellationToken cancellationToken)
    {
        var buffer = new char[512];
        var segmentBuilder = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                {
                    FlushConsoleSegment(segmentBuilder, sink, lineHandler);
                    continue;
                }

                if (!char.IsControl(character) || character == '\t')
                {
                    segmentBuilder.Append(character);
                }
            }
        }

        FlushConsoleSegment(segmentBuilder, sink, lineHandler);
    }

    private static void FlushConsoleSegment(
        StringBuilder segmentBuilder,
        StringBuilder? sink,
        Action<string>? lineHandler)
    {
        if (segmentBuilder.Length == 0)
        {
            return;
        }

        var normalized = ConsoleOutputLineNormalizer.Normalize(segmentBuilder.ToString());
        segmentBuilder.Clear();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        sink?.AppendLine(normalized);
        lineHandler?.Invoke(normalized);
    }

    private static bool TryGetExitCode(Process? process, out int exitCode)
    {
        exitCode = -1;
        if (process is null)
        {
            return false;
        }

        try
        {
            exitCode = process.ExitCode;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasSuccessfulTerminalLine(string log, Func<string, bool>? successLineDetector)
    {
        if (successLineDetector is null || string.IsNullOrWhiteSpace(log))
        {
            return false;
        }

        var lastMeaningfulLine = GetLastMeaningfulLine(log);
        return !string.IsNullOrWhiteSpace(lastMeaningfulLine) && successLineDetector(lastMeaningfulLine);
    }

    private static string GetLastMeaningfulLine(string log)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return string.Empty;
        }

        return log
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? string.Empty;
    }

    protected sealed record ProcessCaptureResult(int ExitCode, string Output);
}
