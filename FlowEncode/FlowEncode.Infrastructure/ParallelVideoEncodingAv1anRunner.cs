using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal sealed class ParallelVideoEncodingAv1anRunner
{
    private const string TempWorkspaceFolderName = ".flowencode-temp";
    private readonly ExternalToolLocator _toolLocator;
    private readonly EncodingJobLogWriter _logWriter;
    private readonly ParallelVideoEncodingOutputVerifier _outputVerifier;
    private readonly SourceVideoInfoProbe _sourceInfoProbe;
    private readonly IAppSettingsService _settingsService;
    private readonly LocalAppPaths _appPaths;
    private readonly ConcurrentDictionary<Guid, ManagedProcessExecution> _activeExecutions = new();

    public ParallelVideoEncodingAv1anRunner(
        LocalAppPaths paths,
        IAppSettingsService settingsService)
    {
        _appPaths = paths;
        _settingsService = settingsService;
        _toolLocator = new ExternalToolLocator(paths, settingsService);
        _logWriter = new EncodingJobLogWriter(paths, WriteDiagnostic);
        _outputVerifier = new ParallelVideoEncodingOutputVerifier(_toolLocator);
        _sourceInfoProbe = new SourceVideoInfoProbe(_toolLocator);
    }

    public string BuildDisplayCommand(EncodingJobRequest request)
    {
        var parallelRequest = RequestValidation.CreateParallelVideoEncodingRequest(request);
        var av1anPath = _toolLocator.ResolveAv1an();
        var tempDirectory = GetTempDirectory(request);
        var sourceInfo = ProbeSourceInfo(
            request,
            includeSourceMetadata: true,
            required: false,
            cancellationToken: CancellationToken.None);
        return ParallelVideoAv1anArgumentBuilder
            .BuildCommand(parallelRequest, av1anPath, tempDirectory, request.OutputPath, sourceInfo)
            .DisplayCommand;
    }

    public void Abort(Guid jobId)
    {
        if (_activeExecutions.TryRemove(jobId, out var execution))
        {
            execution.Terminate();
            execution.Dispose();
        }
    }

    public async Task<EncodingJobResult> RunAsync(
        EncodingJobRequest request,
        IProgress<EncodingJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var language = GetLanguage();
        RequestValidation.ValidateEncodingJobRequest(request);
        var parallelRequest = RequestValidation.CreateParallelVideoEncodingRequest(request);
        if (!File.Exists(request.SourcePath))
        {
            throw new FileNotFoundException(T(language, "Encoding source file was not found.", "未找到压制输入源文件。"), request.SourcePath);
        }

        var av1anPath = _toolLocator.ResolveAv1an();
        await LegacyAv1anCliFallbackRunner.EnsureAv1anRuntimeReadyAsync(av1anPath, language, cancellationToken);

        var tempDirectory = GetTempDirectory(request);
        Directory.CreateDirectory(tempDirectory);
        var stagedOutputPath = CreateStagedOutputPath(request, tempDirectory);
        var outputDirectory = Path.GetDirectoryName(request.OutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var sourceInfo = ProbeSourceInfo(
            request,
            includeSourceMetadata: true,
            required: RequiresSourceMetadata(parallelRequest),
            cancellationToken);
        var displayCommand = ParallelVideoAv1anArgumentBuilder.BuildCommand(
            parallelRequest,
            av1anPath,
            tempDirectory,
            request.OutputPath,
            sourceInfo).DisplayCommand;

        var rawLogPath = CreateTemporaryRawLogPath(request, tempDirectory);
        var av1anLogPath = CreateAv1anLogPath(request, tempDirectory);
        var executionCommand = ParallelVideoAv1anArgumentBuilder.BuildCommand(
            parallelRequest,
            av1anPath,
            tempDirectory,
            stagedOutputPath,
            sourceInfo,
            logFilePath: av1anLogPath);
        var visibleLogBuilder = new StringBuilder();
        var rawLogWriter = EncodingJobLogWriter.CreateRawLogWriter(rawLogPath);
        var rawLogWriterDisposed = false;
        var gate = new object();
        var currentState = EncodingJobState.Running;
        double? currentProgress = null;
        var currentSummary = T(language, "Av1an parallel encoding started", "Av1an 并行压制已启动");
        var outputFinalized = false;
        var finalExitCode = -1;
        var startedAt = Stopwatch.GetTimestamp();

        void AppendLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            rawLogWriter.WriteLine(line);
            if (visibleLogBuilder.Length > 0)
            {
                visibleLogBuilder.AppendLine();
            }

            visibleLogBuilder.Append(line);
            EncodingJobLogWriter.TrimVisibleLogIfNeeded(visibleLogBuilder);
        }

        void AppendRawLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            rawLogWriter.WriteLine(line);
        }

        async Task CloseRawLogWriterAsync()
        {
            if (rawLogWriterDisposed)
            {
                return;
            }

            await rawLogWriter.FlushAsync();
            await rawLogWriter.DisposeAsync();
            rawLogWriterDisposed = true;
        }

        void HandleLine(string line)
        {
            var normalizedLine = ConsoleOutputLineNormalizer.Normalize(line);
            if (string.IsNullOrWhiteSpace(normalizedLine))
            {
                return;
            }

            EncodingJobProgress? update = null;
            lock (gate)
            {
                if (JsonlEventParser.TryParse(normalizedLine, out var parsedEvent) && parsedEvent is not null)
                {
                    var encoderLogLines = JsonlEventParser.BuildEncoderLogLines(parsedEvent);
                    foreach (var encoderLogLine in encoderLogLines)
                    {
                        AppendRawLine(encoderLogLine);
                    }

                    var eventProgress = JsonlEventParser.TryGetProgressFraction(parsedEvent);
                    if (eventProgress.HasValue)
                    {
                        currentProgress = currentProgress.HasValue
                            ? Math.Max(currentProgress.Value, eventProgress.Value)
                            : eventProgress.Value;
                    }

                    var detailLine = JsonlEventParser.BuildDetailLine(parsedEvent);
                    AppendLine(detailLine);
                    currentState = MapStructuredState(JsonlEventParser.MapStage(parsedEvent.Type));
                    currentSummary = BuildRunningSummary(language, currentProgress);
                    update = new EncodingJobProgress(
                        request.JobId,
                        currentState,
                        currentProgress,
                        currentSummary,
                        detailLine);
                }
                else
                {
                    AppendLine(normalizedLine);
                    update = new EncodingJobProgress(
                        request.JobId,
                        currentState,
                        currentProgress,
                        currentSummary,
                        normalizedLine);
                }
            }

            if (update is not null)
            {
                progress?.Report(update);
            }
        }

        Process? process = null;
        ManagedProcessExecution? activeExecution = null;
        Task pumpOutput = Task.CompletedTask;
        Task pumpError = Task.CompletedTask;

        try
        {
            progress?.Report(new EncodingJobProgress(
                request.JobId,
                EncodingJobState.Running,
                0.0,
                currentSummary,
                displayCommand));

            process = LegacyAv1anCliFallbackRunner.CreateProcess(
                av1anPath,
                executionCommand.Arguments,
                GetWorkingDirectory(request, av1anPath));
            process.Start();
            activeExecution = new ManagedProcessExecution(
                message => WriteDiagnostic($"Parallel video encoding job {request.JobId}: {message}"),
                process);
            _activeExecutions[request.JobId] = activeExecution;

            pumpOutput = PumpAsync(process.StandardOutput, HandleLine, cancellationToken);
            pumpError = PumpAsync(process.StandardError, HandleLine, cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            activeExecution.Terminate();
            await Task.WhenAll(pumpOutput, pumpError);
            finalExitCode = process.ExitCode;
            _activeExecutions.TryRemove(request.JobId, out _);

            await rawLogWriter.FlushAsync();
            var visibleLog = visibleLogBuilder.ToString();
            if (finalExitCode == 0)
            {
                try
                {
                    _outputVerifier.VerifyVideoOutput(stagedOutputPath, language, cancellationToken);
                    var completionSnapshot = BuildCompletionSnapshot(request, stagedOutputPath, startedAt, cancellationToken);
                    var reportLine = BuildPostEncodeReportLine(language, completionSnapshot);
                    AppendLine(reportLine);
                    AppendAv1anFileLog(av1anLogPath, AppendLine);
                    await rawLogWriter.FlushAsync();
                    visibleLog = visibleLogBuilder.ToString();
                    FinalizeOutputFile(stagedOutputPath, request.OutputPath, request.JobId);

                    currentProgress = 1.0;
                    progress?.Report(new EncodingJobProgress(
                        request.JobId,
                        EncodingJobState.Running,
                        currentProgress,
                        T(language, "Av1an parallel encoding finalizing", "Av1an 并行压制收尾中"),
                        reportLine,
                        completionSnapshot));
                }
                catch (Exception ex)
                {
                    currentState = EncodingJobState.Failed;
                    var finalizationFailureSummary = T(
                        language,
                        $"Av1an parallel encoding finished but output verification failed: {ex.Message}",
                        $"Av1an 并行压制已结束，但输出校验失败：{ex.Message}");
                    AppendLine(finalizationFailureSummary);
                    await CloseRawLogWriterAsync();
                    visibleLog = visibleLogBuilder.ToString();
                    var outputFailureLogPath = await _logWriter.WriteSidecarLogAsync(request, displayCommand, currentState, finalExitCode, rawLogPath);
                    progress?.Report(new EncodingJobProgress(
                        request.JobId,
                        currentState,
                        currentProgress,
                        finalizationFailureSummary,
                        finalizationFailureSummary));

                    return new EncodingJobResult(
                        request.JobId,
                        currentState,
                        finalExitCode,
                        finalizationFailureSummary,
                        visibleLog,
                        outputFailureLogPath);
                }

                outputFinalized = true;
                currentState = EncodingJobState.Completed;
                currentProgress = 1.0;
                var summary = T(language, "Av1an parallel encoding completed", "Av1an 并行压制完成");
                await CloseRawLogWriterAsync();
                var sidecarLogPath = await _logWriter.WriteSidecarLogAsync(request, displayCommand, currentState, 0, rawLogPath);
                progress?.Report(new EncodingJobProgress(
                    request.JobId,
                    currentState,
                    currentProgress,
                    summary,
                    EncodingJobLogWriter.LastMeaningfulLine(visibleLog)));

                return new EncodingJobResult(
                    request.JobId,
                    currentState,
                    0,
                    summary,
                    visibleLog,
                    sidecarLogPath);
            }

            currentState = EncodingJobState.Failed;
            var failureSummary = BuildFailureSummary(language, finalExitCode, visibleLog);
            await CloseRawLogWriterAsync();
            var failedLogPath = await _logWriter.WriteSidecarLogAsync(request, displayCommand, currentState, finalExitCode, rawLogPath);
            progress?.Report(new EncodingJobProgress(
                request.JobId,
                currentState,
                currentProgress,
                failureSummary,
                EncodingJobLogWriter.LastMeaningfulLine(visibleLog)));

            return new EncodingJobResult(
                request.JobId,
                currentState,
                finalExitCode,
                failureSummary,
                visibleLog,
                failedLogPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activeExecution?.Terminate();
            try
            {
                await Task.WhenAll(pumpOutput, pumpError);
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"Parallel video encoding job {request.JobId}: failed to drain process output after cancellation. {ex.GetType().Name}: {ex.Message}");
            }

            currentState = EncodingJobState.Cancelled;
            await CloseRawLogWriterAsync();
            var visibleLog = visibleLogBuilder.ToString();
            var sidecarLogPath = await _logWriter.WriteSidecarLogAsync(request, displayCommand, currentState, -1, rawLogPath);
            var summary = T(language, "Av1an parallel encoding cancelled", "Av1an 并行压制已取消");
            progress?.Report(new EncodingJobProgress(
                request.JobId,
                currentState,
                currentProgress,
                summary,
                T(language, "The task was cancelled.", "任务已取消。")));

            return new EncodingJobResult(
                request.JobId,
                currentState,
                -1,
                summary,
                visibleLog,
                sidecarLogPath);
        }
        finally
        {
            _activeExecutions.TryRemove(request.JobId, out _);
            activeExecution?.Dispose();
            process?.Dispose();
            await CloseRawLogWriterAsync();
            if (!outputFinalized)
            {
                ExecutionOutputStaging.CleanupStagedFile(stagedOutputPath, request.OutputPath, request.JobId, WriteDiagnostic);
            }

            CleanupTemporaryRawLog(rawLogPath);
            CleanupTemporaryRawLog(av1anLogPath);
            CleanupJobTempDirectory(tempDirectory);
        }
    }

    private static Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        return ProcessOutputPump.PumpLinesAsync(
            reader,
            onLine,
            cancellationToken,
            new ProcessOutputPumpOptions(
                StripControlCharacters: true,
                NormalizeLine: ConsoleOutputLineNormalizer.Normalize));
    }

    private static EncodingJobState MapStructuredState(AutoCompressionExecutionStage stage)
    {
        return stage switch
        {
            AutoCompressionExecutionStage.Completed => EncodingJobState.Completed,
            AutoCompressionExecutionStage.Failed => EncodingJobState.Failed,
            AutoCompressionExecutionStage.Cancelled => EncodingJobState.Cancelled,
            _ => EncodingJobState.Running
        };
    }

    private static string BuildRunningSummary(AppLanguage language, double? progressFraction)
    {
        return progressFraction is { } value
            ? T(language, $"Av1an parallel encoding {value:P0}", $"Av1an 并行压制中 {value:P0}")
            : T(language, "Av1an parallel encoding", "Av1an 并行压制中");
    }

    private static string BuildFailureSummary(AppLanguage language, int exitCode, string visibleLog)
    {
        var lastLine = EncodingJobLogWriter.LastMeaningfulLine(visibleLog);
        if (!string.IsNullOrWhiteSpace(lastLine))
        {
            return T(
                language,
                $"Av1an parallel encoding failed: {lastLine}",
                $"Av1an 并行压制失败：{lastLine}");
        }

        return T(
            language,
            $"Av1an parallel encoding failed (exit code {exitCode})",
            $"Av1an 并行压制失败，退出代码 {exitCode}");
    }

    private SourceVideoInfo? ProbeSourceInfo(
        EncodingJobRequest request,
        bool includeSourceMetadata,
        bool required,
        CancellationToken cancellationToken)
    {
        if (!includeSourceMetadata)
        {
            return null;
        }

        try
        {
            var sourceInfo = _sourceInfoProbe.Probe(
                request.SourcePath,
                request.PipelineKind,
                cancellationToken: cancellationToken,
                allowCached: true);

            if (sourceInfo is null && required)
            {
                throw new InvalidOperationException(T(
                    GetLanguage(),
                    "SVT-AV1 requires detectable source metadata. Make sure the current input can be recognized by ffprobe or vspipe.",
                    "SVT-AV1 需要可探测的源信息。请确保当前输入可被 ffprobe / vspipe 正常识别。"));
            }

            return sourceInfo;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!required && IsOptionalSourceMetadataFailure(ex))
        {
            return null;
        }
    }

    private static bool IsOptionalSourceMetadataFailure(Exception ex) =>
        ex is InvalidOperationException or JsonException;

    private static bool RequiresSourceMetadata(ParallelVideoEncodingRequest request) =>
        request.EncoderKind == EncoderKind.SvtAv1 && request.PipelineKind != InputPipelineKind.RawYuvFile;

    private static string GetTempDirectory(EncodingJobRequest request)
    {
        var outputDirectory = Path.GetDirectoryName(request.OutputPath);
        var baseDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Environment.CurrentDirectory
            : outputDirectory;
        return Path.Combine(baseDirectory, TempWorkspaceFolderName, "av1an-parallel", request.JobId.ToString("N"));
    }

    private static string CreateStagedOutputPath(EncodingJobRequest request, string tempDirectory)
    {
        return Path.Combine(tempDirectory, Path.GetFileName(request.OutputPath));
    }

    private EncodingProgressSnapshot BuildCompletionSnapshot(
        EncodingJobRequest request,
        string outputPath,
        long startedAt,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        SourceVideoInfo? sourceInfo = null;
        try
        {
            sourceInfo = _sourceInfoProbe.Probe(
                request.SourcePath,
                request.PipelineKind,
                cancellationToken: cancellationToken,
                allowCached: true);
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"Failed to probe source info for parallel video completion report. {ex.GetType().Name}: {ex.Message}");
        }

        var outputSizeBytes = File.Exists(outputPath)
            ? new FileInfo(outputPath).Length
            : (long?)null;
        var totalFrames = sourceInfo?.TotalFrames is > 0 ? sourceInfo.TotalFrames.Value : (long?)null;
        var effectiveFps = totalFrames is > 0 && elapsed.TotalSeconds > 0
            ? totalFrames.Value / elapsed.TotalSeconds
            : (double?)null;
        var durationSeconds = TryGetDurationSeconds(sourceInfo);
        var bitrateKbps = outputSizeBytes is > 0 && durationSeconds is > 0
            ? outputSizeBytes.Value * 8.0 / durationSeconds.Value / 1000.0
            : (double?)null;

        return new EncodingProgressSnapshot(
            totalFrames,
            totalFrames,
            effectiveFps,
            bitrateKbps,
            TimeSpan.Zero,
            outputSizeBytes);
    }

    private static string BuildPostEncodeReportLine(AppLanguage language, EncodingProgressSnapshot snapshot)
    {
        var frames = snapshot.TotalFrames?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var fps = snapshot.FramesPerSecond is > 0
            ? snapshot.FramesPerSecond.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : "--";
        var bitrate = snapshot.BitrateKbps is > 0
            ? snapshot.BitrateKbps.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : "--";
        var size = snapshot.EstimatedFileSizeBytes is > 0
            ? FormatByteSize(snapshot.EstimatedFileSizeBytes.Value)
            : "--";

        return T(
            language,
            $"post-encode report: {frames} frames, {fps} fps, {bitrate} kb/s, output {size}",
            $"压制后报告：{frames} frames，{fps} fps，{bitrate} kb/s，输出 {size}");
    }

    private static double? TryGetDurationSeconds(SourceVideoInfo? sourceInfo)
    {
        if (sourceInfo?.TotalFrames is not > 0
            || sourceInfo.FpsNumerator is not > 0
            || sourceInfo.FpsDenominator is not > 0)
        {
            return null;
        }

        return sourceInfo.TotalFrames.Value * sourceInfo.FpsDenominator.Value / (double)sourceInfo.FpsNumerator.Value;
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }

    internal static string CreateTemporaryRawLogPath(EncodingJobRequest request, string tempDirectory)
    {
        var tempParentDirectory = Path.GetDirectoryName(tempDirectory) ?? tempDirectory;
        var directory = Path.Combine(tempParentDirectory, "logs", request.JobId.ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{request.JobId:N}.raw.log");
    }

    private static string CreateAv1anLogPath(EncodingJobRequest request, string tempDirectory)
    {
        var tempParentDirectory = Path.GetDirectoryName(tempDirectory) ?? tempDirectory;
        var directory = Path.Combine(tempParentDirectory, "logs", request.JobId.ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{request.JobId:N}.av1an.log");
    }

    private void AppendAv1anFileLog(string av1anLogPath, Action<string> appendLine)
    {
        if (!File.Exists(av1anLogPath))
        {
            return;
        }

        try
        {
            appendLine("--- AV1AN LOG ---");
            foreach (var line in File.ReadLines(av1anLogPath))
            {
                appendLine(line);
            }
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"Failed to append Av1an log file '{av1anLogPath}'. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string GetWorkingDirectory(EncodingJobRequest request, string fileName)
    {
        var sourceDirectory = Path.GetDirectoryName(request.SourcePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return sourceDirectory;
        }

        return Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory;
    }

    private static void FinalizeOutputFile(string stagedOutputPath, string finalOutputPath, Guid jobId)
    {
        try
        {
            ExecutionOutputStaging.FinalizeFile(stagedOutputPath, finalOutputPath, jobId);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Encoding completed but the output file could not be finalized: {finalOutputPath}", ex);
        }
    }

    private void CleanupJobTempDirectory(string tempDirectory)
    {
        BestEffortCleanup.DeleteDirectoryRecursively(
            tempDirectory,
            $"parallel video encoding temp directory '{tempDirectory}'",
            WriteDiagnostic);
        BestEffortCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(tempDirectory), WriteDiagnostic);
        BestEffortCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(Path.GetDirectoryName(tempDirectory)), WriteDiagnostic);
    }

    private void CleanupTemporaryRawLog(string path)
    {
        BestEffortCleanup.DeleteFile(
            path,
            $"temporary raw log '{path}'",
            WriteDiagnostic);
        BestEffortCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(path), WriteDiagnostic);
        BestEffortCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(Path.GetDirectoryName(path)), WriteDiagnostic);
    }

    private void WriteDiagnostic(string message)
    {
        AppDiagnosticsLog.Write(_appPaths, nameof(ParallelVideoEncodingAv1anRunner), message);
    }

    private AppLanguage GetLanguage() => _settingsService.Load().Language;

    private static string T(AppLanguage language, string en, string zh) =>
        language == AppLanguage.English ? en : zh;
}
