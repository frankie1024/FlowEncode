using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class StructuredAv1anRunner : IAutoCompressionRunner
{
    private readonly LegacyAv1anCliFallbackRunner _legacyRunner;
    private readonly ExternalToolLocator _toolLocator;
    private readonly IAppSettingsService _settingsService;
    private readonly IToolProbeService _toolProbeService;
    private readonly LocalAppPaths _appPaths;
    private readonly ConcurrentDictionary<Guid, ManagedProcessExecution> _activeExecutions = new();

    public StructuredAv1anRunner(
        LocalAppPaths paths,
        IAppSettingsService settingsService,
        IToolProbeService toolProbeService)
    {
        _appPaths = paths;
        _settingsService = settingsService;
        _toolProbeService = toolProbeService;
        _toolLocator = new ExternalToolLocator(paths, settingsService);
        _legacyRunner = new LegacyAv1anCliFallbackRunner(paths, settingsService);
    }

    public async Task<AutoCompressionResult> RunAsync(
        AutoCompressionRequest request,
        IProgress<AutoCompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAv1anAsync(cancellationToken);
        if (probe.State != ReadinessState.Ready || !probe.IsProtocolCompatible)
        {
            return await _legacyRunner.RunAsync(request, progress, cancellationToken);
        }

        var language = GetLanguage();
        RequestValidation.ValidateAutoCompressionRequest(request);
        if (!File.Exists(request.SourcePath))
        {
            throw new FileNotFoundException(
                T(language, "Auto encode source file was not found.", "未找到自动压制输入源文件。"),
                request.SourcePath);
        }

        EnsureProtocolSupportsRequest(probe.Av1anCapabilities, request, language);

        var av1anPath = _toolLocator.ResolveAv1an();
        await LegacyAv1anCliFallbackRunner.EnsureAv1anRuntimeReadyAsync(av1anPath, language, cancellationToken);

        var tempDirectory = LegacyAv1anCliFallbackRunner.GetTempDirectory(request);
        Directory.CreateDirectory(tempDirectory);
        var stagedOutputPath = LegacyAv1anCliFallbackRunner.CreateStagedOutputPath(request, tempDirectory);
        var executionRequest = request with { OutputPath = stagedOutputPath };
        var arguments = LegacyAv1anCliFallbackRunner.BuildArgumentParts(
            executionRequest,
            tempDirectory,
            language,
            useStructuredProgress: true);
        var displayCommand =
            $"{LegacyAv1anCliFallbackRunner.Quote(av1anPath)} " +
            $"{CommandLineDisplay.JoinArguments(LegacyAv1anCliFallbackRunner.BuildArgumentParts(request, tempDirectory, language, useStructuredProgress: true))}";
        var backendCapabilities = CreateBackendCapabilities(probe.Av1anCapabilities);
        var logBuilder = new StringBuilder();
        var outputDirectory = Path.GetDirectoryName(request.OutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        progress?.Report(new AutoCompressionProgress(
            request.JobId,
            EncodingJobState.Running,
            AutoCompressionExecutionStage.Preparing,
            null,
            T(language, "Auto encode started", "自动压制已启动"),
            displayCommand));

        var gate = new object();
        var currentState = EncodingJobState.Running;
        var currentStage = AutoCompressionExecutionStage.Preparing;
        double? currentProgress = null;
        var currentSummary = T(language, "Auto encode started", "自动压制已启动");
        var lastFailureMessage = string.Empty;

        void HandleStructuredStdoutLine(string line)
        {
            var normalizedLine = ConsoleOutputLineNormalizer.Normalize(line);
            if (string.IsNullOrWhiteSpace(normalizedLine))
            {
                return;
            }

            if (!JsonlEventParser.TryParse(normalizedLine, out var parsedEvent))
            {
                HandleDiagnosticLine(normalizedLine);
                return;
            }

            AutoCompressionProgress? update = null;
            lock (gate)
            {
                var parsedStructuredEvent = parsedEvent!;
                currentStage = JsonlEventParser.MapStage(parsedStructuredEvent.Type);
                var eventProgress = JsonlEventParser.TryGetProgressFraction(parsedStructuredEvent);
                if (eventProgress.HasValue)
                {
                    currentProgress = currentProgress.HasValue
                        ? Math.Max(currentProgress.Value, eventProgress.Value)
                        : eventProgress.Value;
                }

                var detailLine = JsonlEventParser.BuildDetailLine(parsedStructuredEvent);
                LegacyAv1anCliFallbackRunner.AppendVisibleLogLine(logBuilder, detailLine);

                switch (currentStage)
                {
                    case AutoCompressionExecutionStage.Completed:
                        currentState = EncodingJobState.Completed;
                        currentProgress = 1.0;
                        currentSummary = T(language, "Auto encode completed", "自动压制完成");
                        break;
                    case AutoCompressionExecutionStage.Failed:
                        currentState = EncodingJobState.Failed;
                        lastFailureMessage = JsonlEventParser.TryGetFailureMessage(parsedStructuredEvent) ?? string.Empty;
                        currentSummary = string.IsNullOrWhiteSpace(lastFailureMessage)
                            ? T(language, "Auto encode failed", "自动压制失败")
                            : T(language, $"Auto encode failed: {lastFailureMessage}", $"自动压制失败：{lastFailureMessage}");
                        break;
                    case AutoCompressionExecutionStage.Cancelled:
                        currentState = EncodingJobState.Cancelled;
                        currentSummary = T(language, "Auto encode cancelled", "自动压制已取消");
                        break;
                    default:
                        currentState = EncodingJobState.Running;
                        currentSummary = BuildRunningSummary(language, currentStage, currentProgress);
                        break;
                }

                update = new AutoCompressionProgress(
                    request.JobId,
                    currentState,
                    currentStage,
                    currentProgress,
                    currentSummary,
                    detailLine);
            }

            if (update is not null)
            {
                progress?.Report(update);
            }
        }

        void HandleDiagnosticLine(string line)
        {
            var normalizedLine = ConsoleOutputLineNormalizer.Normalize(line);
            if (string.IsNullOrWhiteSpace(normalizedLine))
            {
                return;
            }

            AutoCompressionProgress? update = null;
            lock (gate)
            {
                LegacyAv1anCliFallbackRunner.AppendVisibleLogLine(logBuilder, normalizedLine);
                update = new AutoCompressionProgress(
                    request.JobId,
                    currentState,
                    currentStage,
                    currentProgress,
                    currentSummary,
                    normalizedLine);
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
        var finalExitCode = -1;
        var outputFinalized = false;

        try
        {
            process = LegacyAv1anCliFallbackRunner.CreateProcess(av1anPath, arguments);
            process.Start();
            activeExecution = new ManagedProcessExecution(
                message => WriteDiagnostic($"Structured auto compression job {request.JobId}: {message}"),
                process);
            _activeExecutions[request.JobId] = activeExecution;

            pumpOutput = PumpAsync(process.StandardOutput, HandleStructuredStdoutLine, cancellationToken);
            pumpError = PumpAsync(process.StandardError, HandleDiagnosticLine, cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            activeExecution.Terminate();
            await Task.WhenAll(pumpOutput, pumpError);
            finalExitCode = process.ExitCode;

            _activeExecutions.TryRemove(request.JobId, out _);

            var log = logBuilder.ToString();
            if (finalExitCode == 0)
            {
                if (!AutoCompressionOutputFinalizer.TryFinalizeOutput(
                        request.JobId,
                        stagedOutputPath,
                        request.OutputPath,
                        language,
                        WriteDiagnostic,
                        out var finalizationFailureSummary))
                {
                    currentState = EncodingJobState.Failed;
                    currentStage = AutoCompressionExecutionStage.Failed;
                    currentSummary = finalizationFailureSummary;
                    progress?.Report(new AutoCompressionProgress(
                        request.JobId,
                        currentState,
                        currentStage,
                        currentProgress,
                        finalizationFailureSummary,
                        finalizationFailureSummary));

                    return new AutoCompressionResult(
                        request.JobId,
                        EncodingJobState.Failed,
                        finalExitCode,
                        finalizationFailureSummary,
                        log,
                        displayCommand,
                        backendCapabilities);
                }

                outputFinalized = true;
                if (currentState != EncodingJobState.Completed)
                {
                    currentState = EncodingJobState.Completed;
                    currentStage = AutoCompressionExecutionStage.Completed;
                    currentProgress = 1.0;
                    currentSummary = T(language, "Auto encode completed", "自动压制完成");
                    progress?.Report(new AutoCompressionProgress(
                        request.JobId,
                        currentState,
                        currentStage,
                        currentProgress,
                        currentSummary,
                        T(language, "run completed", "任务完成")));
                }

                return new AutoCompressionResult(
                    request.JobId,
                    EncodingJobState.Completed,
                    0,
                    T(language, "Auto encode completed", "自动压制完成"),
                    log,
                    displayCommand,
                    backendCapabilities);
            }

            if (currentState != EncodingJobState.Failed)
            {
                currentState = EncodingJobState.Failed;
                currentStage = AutoCompressionExecutionStage.Failed;
                var summary = !string.IsNullOrWhiteSpace(lastFailureMessage)
                    ? lastFailureMessage
                    : LegacyAv1anCliFallbackRunner.LastMeaningfulLine(log);
                if (string.IsNullOrWhiteSpace(summary))
                {
                    summary = T(language, $"Auto encode failed (exit code {finalExitCode})", $"自动压制失败，退出代码 {finalExitCode}");
                }

                currentSummary = summary;
                progress?.Report(new AutoCompressionProgress(
                    request.JobId,
                    currentState,
                    currentStage,
                    currentProgress,
                    summary,
                    summary));
            }

            var failureSummary = !string.IsNullOrWhiteSpace(lastFailureMessage)
                ? lastFailureMessage
                : LegacyAv1anCliFallbackRunner.LastMeaningfulLine(log);
            if (string.IsNullOrWhiteSpace(failureSummary))
            {
                failureSummary = T(language, $"Auto encode failed (exit code {finalExitCode})", $"自动压制失败，退出代码 {finalExitCode}");
            }

            return new AutoCompressionResult(
                request.JobId,
                EncodingJobState.Failed,
                finalExitCode,
                failureSummary,
                log,
                displayCommand,
                backendCapabilities);
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
                WriteDiagnostic($"Structured auto compression job {request.JobId}: failed to drain process output after cancellation. {ex.GetType().Name}: {ex.Message}");
            }

            var log = logBuilder.ToString();
            progress?.Report(new AutoCompressionProgress(
                request.JobId,
                EncodingJobState.Cancelled,
                AutoCompressionExecutionStage.Cancelled,
                currentProgress,
                T(language, "Auto encode cancelled", "自动压制已取消"),
                T(language, "The task was cancelled.", "任务已取消。")));

            return new AutoCompressionResult(
                request.JobId,
                EncodingJobState.Cancelled,
                -1,
                T(language, "Auto encode cancelled", "自动压制已取消"),
                log,
                displayCommand,
                backendCapabilities);
        }
        finally
        {
            _activeExecutions.TryRemove(request.JobId, out _);
            activeExecution?.Dispose();
            ExecutionOutputStaging.CleanupStagedFile(stagedOutputPath, request.OutputPath, request.JobId, WriteDiagnostic);
            LegacyAv1anCliFallbackRunner.CleanupPartialOutputFile(request, outputFinalized, WriteDiagnostic);
            LegacyAv1anCliFallbackRunner.CleanupJobTempDirectory(request, WriteDiagnostic);
        }
    }

    public string BuildDisplayCommand(AutoCompressionRequest request)
    {
        var av1anPath = _toolLocator.ResolveAv1an();
        return
            $"{LegacyAv1anCliFallbackRunner.Quote(av1anPath)} " +
            $"{CommandLineDisplay.JoinArguments(LegacyAv1anCliFallbackRunner.BuildArgumentParts(request, LegacyAv1anCliFallbackRunner.GetTempDirectory(request), GetLanguage(), useStructuredProgress: true))}";
    }

    public void Abort(Guid jobId)
    {
        if (_activeExecutions.TryRemove(jobId, out var execution))
        {
            execution.Terminate();
            execution.Dispose();
        }

        _legacyRunner.Abort(jobId);
    }

    private async Task<ToolProbeResult> ProbeAv1anAsync(CancellationToken cancellationToken)
    {
        _toolProbeService.InvalidateCache();
        return await _toolProbeService.ProbeAsync(RegisteredToolKind.Av1an, cancellationToken);
    }

    private static async Task PumpAsync(
        StreamReader reader,
        Action<string> onLine,
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
                    FlushConsoleSegment(segmentBuilder, onLine);
                    continue;
                }

                if (!char.IsControl(character) || character == '\t')
                {
                    segmentBuilder.Append(character);
                }
            }
        }

        FlushConsoleSegment(segmentBuilder, onLine);
    }

    private static void FlushConsoleSegment(StringBuilder segmentBuilder, Action<string> onLine)
    {
        if (segmentBuilder.Length == 0)
        {
            return;
        }

        var normalized = ConsoleOutputLineNormalizer.Normalize(segmentBuilder.ToString());
        segmentBuilder.Clear();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            onLine(normalized);
        }
    }

    private static void EnsureProtocolSupportsRequest(
        Av1anCapabilitiesSnapshot? capabilities,
        AutoCompressionRequest request,
        AppLanguage language)
    {
        if (capabilities is null)
        {
            return;
        }

        if (capabilities.SupportedMetrics.Count > 0 && !capabilities.SupportedMetrics.Contains(request.Metric))
        {
            throw new InvalidOperationException(
                T(
                    language,
                    $"The current Av1an backend does not advertise support for metric '{LegacyAv1anCliFallbackRunner.MapMetric(request.Metric)}'.",
                    $"当前 Av1an 后端未声明支持指标 '{LegacyAv1anCliFallbackRunner.MapMetric(request.Metric)}'。"));
        }

        if (capabilities.SupportedEncoders.Count > 0 && !capabilities.SupportedEncoders.Contains(request.EncoderKind))
        {
            throw new InvalidOperationException(
                T(
                    language,
                    $"The current Av1an backend does not advertise support for encoder '{LegacyAv1anCliFallbackRunner.MapEncoder(request.EncoderKind, language)}'.",
                    $"当前 Av1an 后端未声明支持编码器 '{LegacyAv1anCliFallbackRunner.MapEncoder(request.EncoderKind, language)}'。"));
        }
    }

    private static AutoCompressionBackendCapabilities? CreateBackendCapabilities(Av1anCapabilitiesSnapshot? capabilities)
    {
        if (capabilities is null)
        {
            return null;
        }

        return new AutoCompressionBackendCapabilities(
            capabilities.Protocol,
            capabilities.BackendVersion,
            capabilities.SupportedMetrics
                .Select(metric => new AutoCompressionMetricCapability(metric, MetricAvailability.Supported))
                .ToArray(),
            capabilities.SupportedEncoders.ToArray(),
            capabilities.InterpolationMethods.ToArray(),
            capabilities.ProbingStatistics.ToArray());
    }

    private static string BuildRunningSummary(
        AppLanguage language,
        AutoCompressionExecutionStage stage,
        double? progressFraction)
    {
        var stageText = stage switch
        {
            AutoCompressionExecutionStage.InputProbing => T(language, "Input probing", "输入探测中"),
            AutoCompressionExecutionStage.SceneDetection => T(language, "Scene detection", "场景检测中"),
            AutoCompressionExecutionStage.ChunkPlanning => T(language, "Chunk planning", "分块规划中"),
            AutoCompressionExecutionStage.Probing => T(language, "Target probing", "目标探测中"),
            AutoCompressionExecutionStage.Encoding => T(language, "Encoding", "编码中"),
            AutoCompressionExecutionStage.Concatenating => T(language, "Finalizing", "收尾处理中"),
            _ => T(language, "Preparing", "预处理中")
        };

        return progressFraction is > 0
            ? $"{stageText} {progressFraction.Value:P0}"
            : stageText;
    }

    private void WriteDiagnostic(string message)
    {
        AppDiagnosticsLog.Write(_appPaths, nameof(StructuredAv1anRunner), message);
    }

    private AppLanguage GetLanguage() => _settingsService.Load().Language;

    private static string T(AppLanguage language, string en, string zh) =>
        language == AppLanguage.English ? en : zh;
}
