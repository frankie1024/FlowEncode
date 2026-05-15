using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class LocalEncodingJobRunner : IEncodingJobRunner
{
    private const string TempWorkspaceFolderName = ".flowencode-temp";
    private const int MaxVisibleLogLength = 200_000;
    private const int RetainedVisibleLogLength = 120_000;
    private const string VisibleLogTruncationMarker = "[Log truncated; only latest output is kept]";
    private static readonly TimeSpan TransientProgressReportInterval = TimeSpan.FromMilliseconds(125);
    private static readonly Regex X26xProgressRegex = new(@"(?<progress>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex X265PipeMetricsRegex = new(@"^\[\s*(?<progress>\d{1,3}(?:\.\d+)?)\s*%\]\s+(?<current>\d+)\s*\/\s*(?<total>\d+)\s+Frames\s+@\s+(?<fps>\d+(?:\.\d+)?)\s+FPS\s+\|\s+(?<bitrate>\d+(?:\.\d+)?)\s+kb\/s\s+\|\s+(?<eta>\d+:\d{2}:\d{2})(?:\s+\[(?<remainingeta>-?\d+:\d{2}:\d{2})\])?\s+\|\s+(?<currentsize>\d+(?:\.\d+)?)\s*(?<currentunit>[KMGTP]?B)(?:\s+\[(?<size>\d+(?:\.\d+)?)\s*(?<unit>[KMGTP]?B)\])?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xMetricsRegex = new(@"\[?\s*(?<progress>\d{1,3}(?:\.\d+)?)\s*%\]?\s+(?:(?<current>\d+)\s*\/\s*(?<total>\d+)\s+frames|(?<framesonly>\d+)\s+frames:)\s*,?\s*(?<fps>\d+(?:\.\d+)?)\s+fps,\s*(?<bitrate>\d+(?:\.\d+)?)\s+kb/s(?:,\s*eta\s+(?<eta>\d+:\d{2}:\d{2}))?(?:,\s*est\.\s*file\s*size\s+(?<size>\d+(?:\.\d+)?)\s*(?<unit>[KMGTP]?B))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xFrameMetricsRegex = new(@"(?<current>\d+)\s+frames:\s*(?<fps>\d+(?:\.\d+)?)\s+fps,\s*(?<bitrate>\d+(?:\.\d+)?)\s+kb/s", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xPipeFrameMetricsRegex = new(@"^(?:x26[45]\s+)?(?<current>\d+)\s+frames\s+@\s+(?<fps>\d+(?:\.\d+)?)\s+fps\s*\|\s*(?<bitrate>\d+(?:\.\d+)?)\s+kb/s\s*\|\s*(?<currentsize>\d+(?:\.\d+)?)\s*(?<currentunit>[KMGTP]?B)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xEncodedSummaryRegex = new(@"^encoded\s+(?<current>\d+)\s+frames(?:,\s+(?<fps>\d+(?:\.\d+)?)\s+fps|\s+in\s+\d+(?:\.\d+)?s\s+\((?<fpsparenthesized>\d+(?:\.\d+)?)\s+fps\)),\s+(?<bitrate>\d+(?:\.\d+)?)\s+kb/s(?:,\s*Avg\s+QP:\s*\d+(?:\.\d+)?)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xFrameRatioRegex = new(@"(?<current>\d+)\s*\/\s*(?<total>\d+)\s+frames?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xFrameEqualsRegex = new(@"\bframe=\s*(?<current>\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xLooseFpsRegex = new(@"(?<fps>\d+(?:\.\d+)?)\s*fps\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xLooseBitrateRegex = new(@"(?<bitrate>\d+(?:\.\d+)?)\s*kb(?:\/s|ps)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xLooseEtaRegex = new(@"(?:eta|time)\s*:?\s*(?<eta>-?\d+:\d{2}:\d{2})(?:\s*\[(?<remainingeta>-?\d+:\d{2}:\d{2})\])?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xLooseSizeRegex = new(@"(?:est\.\s*file\s*size|size)\s*:?\s*(?<size>\d+(?:\.\d+)?)\s*(?<unit>[KMGTP]?B)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xBracketedSizeRegex = new(@"\[(?<size>\d+(?:\.\d+)?)\s*(?<unit>[KMGTP]?B)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LsmasLwiIndexProgressRegex = new(@"^Creating lwi index file\s+(?<progress>\d{1,3})%$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BestSourceIndexProgressRegex = new(@"^(?:Information:\s+)?VideoSource\s+track\s+#\d+\s+index\s+progress\s+(?<progress>\d{1,3})%$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtFrameRegex = new(@"Encoding\s+frame\s+(?<frame>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtOutputRegex = new(@"Output\s+(?<frame>\d+)\s+frames", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtStatusPrefixRegex = new(
        @"^Encoding:\s*(?<current>\d+)\s*\/\s*(?<total>\d+)\s+Frames?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtStatusFpsRegex = new(
        @"@\s*(?<fps>\d+(?:\.\d+)?)\s+fps\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtStatusBitrateRegex = new(
        @"\|\s*(?<bitrate>\d+(?:\.\d+)?)\s+kb(?:\/s|ps)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtStatusTimeRegex = new(
        @"Time:\s*(?<elapsed>-?\d+:\d{2}:\d{2})(?:\s*\[(?<eta>-?\d+:\d{2}:\d{2})\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtStatusSizeRegex = new(
        @"Size:\s*(?<currentsize>\d+(?:\.\d+)?)\s*(?<currentunit>[KMGTP]?B)(?:\s*\[(?<size>\d+(?:\.\d+)?)\s*(?<unit>[KMGTP]?B)\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtLooseMetricsRegex = new(
        @"^Encoding:\s*(?<current>\d+)\s*\/\s*(?<total>\d+)\s+Frames?\b.*?(?<fps>\d+(?:\.\d+)?)\s+fps\b.*?(?<bitrate>\d+(?:\.\d+)?)\s+kb(?:\/s|ps)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtMetricsRegex = new(@"Encoding\s+frame\s+(?<current>\d+)\s+(?<bitrate>\d+(?:\.\d+)?)\s+kbps\s+(?<fps>\d+(?:\.\d+)?)\s+fps", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly ExternalToolLocator _toolLocator;
    private readonly SourceVideoInfoProbe _sourceInfoProbe;
    private readonly IEncoderDiscoveryService _discoveryService;
    private readonly IAppSettingsService _settingsService;
    private readonly LocalAppPaths _appPaths;
    private readonly ConcurrentDictionary<Guid, ManagedProcessExecution> _activeExecutions = new();

    public LocalEncodingJobRunner(
        LocalAppPaths paths,
        IEncoderDiscoveryService discoveryService,
        IAppSettingsService settingsService)
    {
        _appPaths = paths;
        _toolLocator = new ExternalToolLocator(paths, settingsService);
        _sourceInfoProbe = new SourceVideoInfoProbe(_toolLocator);
        _discoveryService = discoveryService;
        _settingsService = settingsService;
    }

    public string BuildDisplayCommand(EncodingJobRequest request)
    {
        var encoderPath = ResolveEncoderPath(request);
        return BuildPlan(
            request,
            encoderPath,
            includeSourceMetadata: request.Profile.Kind == EncoderKind.SvtAv1,
            allowCachedSourceInfo: true).DisplayCommand;
    }

    public void AbortJob(Guid jobId)
    {
        if (_activeExecutions.TryRemove(jobId, out var execution))
        {
            execution.Terminate();
        }
    }

    public async Task<EncodingJobResult> RunAsync(
        EncodingJobRequest request,
        IProgress<EncodingJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var language = GetLanguage();
        var encoderPath = ResolveEncoderPath(request);
        var visibleLogBuilder = new StringBuilder();
        var currentState = EncodingJobState.Running;
        var progressDispatchState = new ProgressDispatchState(DateTimeOffset.UtcNow, 0.0, 0, string.Empty);
        var pipelineKind = ResolvePipelineKind(request);
        var outputDirectory = Path.GetDirectoryName(request.OutputPath);
        var rawLogPath = CreateTemporaryRawLogPath(request);
        var lineGate = new object();
        var rawLogWriter = CreateRawLogWriter(rawLogPath);
        var rawLogWriterClosed = false;
        Task pumpOutput = Task.CompletedTask;
        Task pumpError = Task.CompletedTask;
        Task pumpSourceError = Task.CompletedTask;
        Task copySourceToEncoder = Task.CompletedTask;
        Process? activeProcess = null;
        Process? activeSourceProcess = null;
        ManagedProcessExecution? activeExecution = null;
        EncodingExecutionPlan? plan = null;

        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        void ReportSourceProbeProgress(string line)
        {
            var normalizedLine = EncoderConsoleLineNormalizer.Normalize(line);
            if (string.IsNullOrWhiteSpace(normalizedLine))
            {
                return;
            }

            var sourceDisplayLine = $"[source] {normalizedLine}";
            var sourcePreparationProgressPercent = ParseSourcePreparationProgressPercent(normalizedLine);

            lock (lineGate)
            {
                rawLogWriter.WriteLine(sourceDisplayLine);
                if (ShouldAppendSourcePreparationVisibleLogLine(normalizedLine))
                {
                    visibleLogBuilder.AppendLine(sourceDisplayLine);
                    TrimVisibleLogIfNeeded(visibleLogBuilder);
                }
            }

            progress?.Report(new EncodingJobProgress(
                request.JobId,
                currentState,
                sourcePreparationProgressPercent.HasValue
                    ? Math.Clamp(sourcePreparationProgressPercent.Value / 100.0, 0.0, 1.0)
                    : null,
                BuildSourceProbeSummary(language, sourcePreparationProgressPercent),
                sourceDisplayLine,
                Snapshot: null,
                IsSourcePreparation: true));
        }

        async Task CloseRawLogWriterAsync()
        {
            if (rawLogWriterClosed)
            {
                return;
            }

            rawLogWriterClosed = true;

            try
            {
                await rawLogWriter.FlushAsync();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            await rawLogWriter.DisposeAsync();
        }

        try
        {
            if (pipelineKind == InputPipelineKind.VapourSynth)
            {
                progress?.Report(new EncodingJobProgress(
                    request.JobId,
                    EncodingJobState.Running,
                    null,
                    BuildSourceProbeSummary(language, null),
                    "[source] Probing source metadata...",
                    Snapshot: null,
                    IsSourcePreparation: true));
            }

            plan = BuildPlan(
                request,
                encoderPath,
                includeSourceMetadata: true,
                pipelineKind,
                pipelineKind == InputPipelineKind.VapourSynth ? ReportSourceProbeProgress : null,
                cancellationToken);

            progress?.Report(new EncodingJobProgress(
                request.JobId,
                EncodingJobState.Running,
                0.0,
                BuildStageStartingSummary(language, plan.Steps[0]),
                plan.DisplayCommand,
                BuildInitialSnapshot(plan)));

            foreach (var step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AppendStageHeader(step, rawLogWriter, visibleLogBuilder);
                progress?.Report(new EncodingJobProgress(
                    request.JobId,
                    EncodingJobState.Running,
                    BuildStageStartingProgress(step),
                    BuildStageStartingSummary(language, step),
                    BuildStageStartingDetail(language, step),
                    BuildStageStartingSnapshot(plan, step)));

                var process = CreateProcess(step.EncoderCommand, encoderPath, redirectStandardInput: step.SourceCommand is not null);
                activeProcess = process;

                Process? sourceProcess = null;

                process.Start();
                try
                {
                    if (step.SourceCommand is not null)
                    {
                        sourceProcess = CreateSourceProcess(step.SourceCommand, encoderPath);
                        activeSourceProcess = sourceProcess;
                        sourceProcess.Start();
                    }
                }
                catch
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(2000);
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteDiagnostic($"Encoding job {request.JobId}: failed to terminate encoder after source start failure. {ex.GetType().Name}: {ex.Message}");
                    }

                    sourceProcess?.Dispose();
                    activeSourceProcess = null;
                    process.Dispose();
                    activeProcess = null;
                    throw;
                }

                activeExecution = sourceProcess is null
                    ? new ManagedProcessExecution(
                        message => WriteDiagnostic($"Encoding job {request.JobId}: {message}"),
                        process)
                    : new ManagedProcessExecution(
                        message => WriteDiagnostic($"Encoding job {request.JobId}: {message}"),
                        sourceProcess,
                        process);
                _activeExecutions[request.JobId] = activeExecution;

                void HandleLine(string line)
                {
                    var normalizedLine = EncoderConsoleLineNormalizer.Normalize(line);
                    if (string.IsNullOrWhiteSpace(normalizedLine))
                    {
                        return;
                    }

                    EncodingJobProgress? pendingProgress = null;

                    lock (lineGate)
                    {
                        rawLogWriter.WriteLine(normalizedLine);

                        if (!EncodingLogLineClassifier.IsTransientProgressLine(plan.Kind, normalizedLine))
                        {
                            visibleLogBuilder.AppendLine(normalizedLine);
                            TrimVisibleLogIfNeeded(visibleLogBuilder);
                        }

                        var progressSnapshot = ParseProgressSnapshot(plan.Kind, plan.TotalFrames, plan.SourceFramesPerSecond, normalizedLine);
                        var stageAwareProgress = ApplyStageProgress(progressSnapshot, step);
                        if (sourceProcess is not null
                            && !sourceProcess.HasExited
                            && stageAwareProgress?.ProgressFraction is null
                            && !ShouldSurfaceLineDuringSourcePreparation(normalizedLine))
                        {
                            return;
                        }

                        if (!ShouldReportProgress(plan.Kind, normalizedLine, stageAwareProgress, ref progressDispatchState))
                        {
                            return;
                        }

                        pendingProgress = new EncodingJobProgress(
                            request.JobId,
                            currentState,
                            stageAwareProgress?.ProgressFraction,
                            BuildRunningSummary(language, step, stageAwareProgress?.ProgressFraction),
                            normalizedLine,
                            stageAwareProgress?.Snapshot);
                    }

                    if (pendingProgress is not null)
                    {
                        progress?.Report(pendingProgress);
                    }
                }

                void HandleSourceLine(string line)
                {
                    var normalizedLine = EncoderConsoleLineNormalizer.Normalize(line);
                    if (string.IsNullOrWhiteSpace(normalizedLine))
                    {
                        return;
                    }

                    var sourceDisplayLine = $"[source] {normalizedLine}";
                    var sourcePreparationProgressPercent = ParseSourcePreparationProgressPercent(normalizedLine);
                    EncodingJobProgress? pendingProgress = null;

                    lock (lineGate)
                    {
                        rawLogWriter.WriteLine(sourceDisplayLine);
                        if (ShouldAppendSourcePreparationVisibleLogLine(normalizedLine))
                        {
                            visibleLogBuilder.AppendLine(sourceDisplayLine);
                            TrimVisibleLogIfNeeded(visibleLogBuilder);
                        }

                        pendingProgress = new EncodingJobProgress(
                            request.JobId,
                            currentState,
                            sourcePreparationProgressPercent.HasValue
                                ? Math.Clamp(sourcePreparationProgressPercent.Value / 100.0, 0.0, 1.0)
                                : null,
                            BuildSourceRunningSummary(language, step, sourcePreparationProgressPercent),
                            sourceDisplayLine,
                            Snapshot: null,
                            IsSourcePreparation: true);
                    }

                    if (pendingProgress is not null)
                    {
                        progress?.Report(pendingProgress);
                    }
                }

                if (sourceProcess is not null)
                {
                    progress?.Report(new EncodingJobProgress(
                        request.JobId,
                        currentState,
                        null,
                        BuildSourceRunningSummary(language, step, null),
                        step.StageCount > 1
                            ? $"[source] Pass {step.StageIndex}/{step.StageCount}: preparing source..."
                            : "[source] Preparing source...",
                        Snapshot: null,
                        IsSourcePreparation: true));

                    copySourceToEncoder = CopyPipeAsync(
                        sourceProcess.StandardOutput.BaseStream,
                        process.StandardInput.BaseStream,
                        cancellationToken);
                    pumpSourceError = PumpAsync(sourceProcess.StandardError, HandleSourceLine, cancellationToken);
                }
                else
                {
                    copySourceToEncoder = Task.CompletedTask;
                    pumpSourceError = Task.CompletedTask;
                }

                pumpOutput = PumpAsync(process.StandardOutput, HandleLine, cancellationToken);
                pumpError = PumpAsync(process.StandardError, HandleLine, cancellationToken);

                var sourceExitCodeShouldBeIgnored = false;
                if (sourceProcess is null)
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                else
                {
                    var encoderExitTask = process.WaitForExitAsync(cancellationToken);
                    var sourceExitTask = sourceProcess.WaitForExitAsync(cancellationToken);
                    var pipeCopyTask = ProcessPipelineMonitor.ObservePipeCopyAsync(copySourceToEncoder);
                    var ignoreSourceExitCode = false;
                    var firstCompletion = await ProcessPipelineMonitor.WaitForFirstCompletionAsync(
                        sourceExitTask,
                        encoderExitTask,
                        pipeCopyTask);

                    if (firstCompletion == PipelineFirstCompletion.ProducerExited)
                    {
                        var firstSourceExitCode = await GetExitCodeAsync(sourceProcess, sourceExitTask);
                        if (firstSourceExitCode != 0)
                        {
                            activeExecution.Terminate();
                        }
                    }
                    else if (firstCompletion == PipelineFirstCompletion.ConsumerExited)
                    {
                        ignoreSourceExitCode = true;
                        TryTerminateProcess(sourceProcess);
                    }
                    else if (firstCompletion == PipelineFirstCompletion.PipeBroken)
                    {
                        ignoreSourceExitCode = true;
                        TryTerminateProcess(sourceProcess);
                    }

                    await Task.WhenAll(
                        encoderExitTask,
                        sourceExitTask,
                        pipeCopyTask);

                    sourceExitCodeShouldBeIgnored = ignoreSourceExitCode;
                }

                activeExecution.Terminate();
                await Task.WhenAll(pumpOutput, pumpError, pumpSourceError);
                var exitCode = process.ExitCode;
                var sourceExitCode = sourceProcess?.ExitCode;
                if (sourceExitCodeShouldBeIgnored)
                {
                    sourceExitCode = null;
                }
                _activeExecutions.TryRemove(request.JobId, out _);
                activeExecution.Dispose();
                activeExecution = null;
                activeProcess = null;
                activeSourceProcess = null;

                if (exitCode != 0 || (sourceExitCode.HasValue && sourceExitCode.Value != 0))
                {
                    currentState = EncodingJobState.Failed;
                    var effectiveExitCode = ResolveStageExitCode(exitCode, sourceExitCode);
                    var failedSummary = BuildStageFailureSummary(language, step, exitCode, sourceExitCode);
                    var failedVisibleLog = visibleLogBuilder.ToString();
                    await CloseRawLogWriterAsync();
                    var failedSidecarLogPath = await WriteSidecarLogAsync(request, plan.DisplayCommand, currentState, effectiveExitCode, rawLogPath);

                    progress?.Report(new EncodingJobProgress(
                        request.JobId,
                        currentState,
                        BuildStageFailureProgress(step),
                        failedSummary,
                        LastMeaningfulLine(failedVisibleLog)));

                    return new EncodingJobResult(
                        request.JobId,
                        currentState,
                        effectiveExitCode,
                        failedSummary,
                        failedVisibleLog,
                        failedSidecarLogPath);
                }
            }

            currentState = EncodingJobState.Completed;
            var summary = T(language, "Encoding completed", "编码完成");
            var visibleLog = visibleLogBuilder.ToString();
            await CloseRawLogWriterAsync();
            var sidecarLogPath = await WriteSidecarLogAsync(request, plan.DisplayCommand, currentState, 0, rawLogPath);

            progress?.Report(new EncodingJobProgress(
                request.JobId,
                currentState,
                1.0,
                summary,
                LastMeaningfulLine(visibleLog)));

            return new EncodingJobResult(
                request.JobId,
                currentState,
                0,
                summary,
                visibleLog,
                sidecarLogPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            currentState = EncodingJobState.Cancelled;

            try
            {
                activeExecution?.Terminate();
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"Encoding job {request.JobId}: failed to terminate active execution after cancellation. {ex.GetType().Name}: {ex.Message}");
            }

            progress?.Report(new EncodingJobProgress(
                request.JobId,
                currentState,
                null,
                T(language, "Encoding cancelled", "编码已取消"),
                T(language, "The job was cancelled by the user.", "作业已被用户取消。")));

            var cancelledLog = visibleLogBuilder.ToString();

            try
            {
                await Task.WhenAll(ProcessPipelineMonitor.ObservePipeCopyAsync(copySourceToEncoder), pumpOutput, pumpError, pumpSourceError);
            }
            catch (OperationCanceledException)
            {
                // Expected while draining cancelled process output.
            }

            await CloseRawLogWriterAsync();
            var cancelledLogPath = await WriteSidecarLogAsync(request, plan?.DisplayCommand ?? string.Empty, currentState, -1, rawLogPath);
            return new EncodingJobResult(
                request.JobId,
                currentState,
                -1,
                T(language, "Encoding cancelled", "编码已取消"),
                cancelledLog,
                cancelledLogPath);
        }
        finally
        {
            _activeExecutions.TryRemove(request.JobId, out _);
            activeExecution?.Dispose();
            activeSourceProcess?.Dispose();
            CleanupPlanArtifacts(plan);
            CleanupPartialOutputFile(request, currentState);

            if (!rawLogWriterClosed)
            {
                await rawLogWriter.DisposeAsync();
            }

            CleanupTemporaryRawLog(rawLogPath);
            CleanupJobTempDirectory(request);
        }
    }

    private string ResolveEncoderPath(EncodingJobRequest request)
    {
        var settings = _settingsService.Load();
        var resolved = _discoveryService.ResolveEncoder(
            request.Profile.Kind,
            request.PreferredArchitecture,
            settings.PreferSystemEncoders);

        if (!string.IsNullOrWhiteSpace(resolved?.ExecutablePath) && File.Exists(resolved.ExecutablePath))
        {
            return resolved.ExecutablePath;
        }

        throw new FileNotFoundException(T(
            GetLanguage(),
            $"No usable {request.Profile.Kind.ToDisplayName()} executable was found. Import it or update it from the toolchain page first.",
            $"未找到 {request.Profile.Kind.ToDisplayName()} 可执行文件。请先在工具链页面导入或自动更新编码器。"));
    }

    private EncodingExecutionPlan BuildPlan(
        EncodingJobRequest request,
        string encoderPath,
        bool includeSourceMetadata,
        InputPipelineKind? pipelineKindOverride = null,
        Action<string>? sourceProbeProgress = null,
        CancellationToken cancellationToken = default,
        bool allowCachedSourceInfo = false)
    {
        var profile = request.Profile;
        var pipelineKind = pipelineKindOverride ?? ResolvePipelineKind(request);
        var sourceInfo = includeSourceMetadata
            ? ResolveSourceInfo(
                request,
                pipelineKind,
                profile.Kind == EncoderKind.SvtAv1 && pipelineKind != InputPipelineKind.RawYuvFile,
                allowCachedSourceInfo,
                sourceProbeProgress,
                cancellationToken)
            : null;
        var preset = EncoderArgumentValueNormalizer.NormalizePresetForCli(profile.Kind, profile.Preset);
        var tune = EncoderArgumentValueNormalizer.NormalizeTuneForCli(profile.Kind, profile.Tune);
        var profileValue = EncoderArgumentValueNormalizer.NormalizeProfileForCli(profile.Kind, profile.Profile);
        var sourceCommand = BuildSourceCommand(request, pipelineKind);

        var statsPath = profile.RateControl == RateControlMode.TwoPass
            ? BuildMultipassStatsPath(request, profile.Kind)
            : null;
        var includeX265UhdParameters = profile.Kind == EncoderKind.X265
            && !string.IsNullOrWhiteSpace(profile.UhdParameters);

        var steps = BuildExecutionSteps(
            request,
            encoderPath,
            sourceCommand,
            pipelineKind,
            sourceInfo,
            includeX265UhdParameters,
            preset,
            tune,
            profileValue,
            statsPath);

        var displayCommand = JoinStepDisplayCommands(steps);

        return new EncodingExecutionPlan(
            steps,
            displayCommand,
            profile.Kind,
            sourceInfo?.TotalFrames,
            sourceInfo?.FramesPerSecond,
            statsPath is null ? [] : [statsPath]);
    }

    private static IReadOnlyList<EncodingExecutionStep> BuildExecutionSteps(
        EncodingJobRequest request,
        string encoderPath,
        ProcessCommand? sourceCommand,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        bool includeX265UhdParameters,
        string preset,
        string tune,
        string profileValue,
        string? statsPath)
    {
        return request.Profile.Kind switch
        {
            EncoderKind.X264 or EncoderKind.X265 when request.Profile.RateControl == RateControlMode.TwoPass
                => BuildX26xTwoPassSteps(request, encoderPath, sourceCommand, pipelineKind, sourceInfo, includeX265UhdParameters, preset, tune, profileValue, statsPath!),
            EncoderKind.SvtAv1 when request.Profile.RateControl == RateControlMode.TwoPass
                => BuildSvtTwoPassSteps(request, encoderPath, sourceCommand, pipelineKind, sourceInfo, preset, tune, profileValue, statsPath!),
            _ => BuildSinglePassSteps(request, encoderPath, sourceCommand, pipelineKind, sourceInfo, includeX265UhdParameters, preset, tune, profileValue, statsPath)
        };
    }

    private static IReadOnlyList<EncodingExecutionStep> BuildSinglePassSteps(
        EncodingJobRequest request,
        string encoderPath,
        ProcessCommand? sourceCommand,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        bool includeX265UhdParameters,
        string preset,
        string tune,
        string profileValue,
        string? statsPath)
    {
        var encoderCommand = BuildEncoderCommand(
            request,
            encoderPath,
            pipelineKind,
            sourceInfo,
            includeX265UhdParameters,
            preset,
            tune,
            profileValue,
            request.OutputPath,
            BuildRateControlArguments(request.Profile.Kind, request.Profile, statsPath));

        return [CreateExecutionStep(sourceCommand, pipelineKind, encoderCommand, 1, 1)];
    }

    private static IReadOnlyList<EncodingExecutionStep> BuildX26xTwoPassSteps(
        EncodingJobRequest request,
        string encoderPath,
        ProcessCommand? sourceCommand,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        bool includeX265UhdParameters,
        string preset,
        string tune,
        string profileValue,
        string statsPath)
    {
        var pass1Command = BuildEncoderCommand(
            request,
            encoderPath,
            pipelineKind,
            sourceInfo,
            includeX265UhdParameters,
            preset,
            tune,
            profileValue,
            "NUL",
            BuildRateControlArguments(request.Profile.Kind, request.Profile, statsPath, passIndex: 1, passCount: 2));

        var pass2Command = BuildEncoderCommand(
            request,
            encoderPath,
            pipelineKind,
            sourceInfo,
            includeX265UhdParameters,
            preset,
            tune,
            profileValue,
            request.OutputPath,
            BuildRateControlArguments(request.Profile.Kind, request.Profile, statsPath, passIndex: 2, passCount: 2));

        return
        [
            CreateExecutionStep(sourceCommand, pipelineKind, pass1Command, 1, 2),
            CreateExecutionStep(sourceCommand, pipelineKind, pass2Command, 2, 2)
        ];
    }

    private static IReadOnlyList<EncodingExecutionStep> BuildSvtTwoPassSteps(
        EncodingJobRequest request,
        string encoderPath,
        ProcessCommand? sourceCommand,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        string preset,
        string tune,
        string profileValue,
        string statsPath)
    {
        var resolvedSourceInfo = sourceInfo
            ?? throw new InvalidOperationException("SVT-AV1 two-pass encoding requires detectable source metadata.");

        var pass1Command = BuildEncoderCommand(
            request,
            encoderPath,
            pipelineKind,
            resolvedSourceInfo,
            includeX265UhdParameters: false,
            preset,
            tune,
            profileValue,
            "NUL",
            BuildRateControlArguments(request.Profile.Kind, request.Profile, statsPath, passIndex: 1, passCount: 2));

        var pass2Command = BuildEncoderCommand(
            request,
            encoderPath,
            pipelineKind,
            resolvedSourceInfo,
            includeX265UhdParameters: false,
            preset,
            tune,
            profileValue,
            request.OutputPath,
            BuildRateControlArguments(request.Profile.Kind, request.Profile, statsPath, passIndex: 2, passCount: 2));

        return
        [
            CreateExecutionStep(sourceCommand, pipelineKind, pass1Command, 1, 2),
            CreateExecutionStep(sourceCommand, pipelineKind, pass2Command, 2, 2)
        ];
    }

    private static EncodingExecutionStep CreateExecutionStep(
        ProcessCommand? sourceCommand,
        InputPipelineKind pipelineKind,
        ProcessCommand encoderCommand,
        int stageIndex,
        int stageCount)
    {
        var pipelineCommand = sourceCommand is null
            ? encoderCommand.DisplayCommand
            : $"{sourceCommand.DisplayCommand} | {encoderCommand.DisplayCommand}";
        return new EncodingExecutionStep(
            encoderCommand,
            pipelineKind is InputPipelineKind.Y4mFile or InputPipelineKind.RawYuvFile ? null : sourceCommand,
            pipelineCommand,
            stageIndex,
            stageCount);
    }

    private static ProcessCommand BuildEncoderCommand(
        EncodingJobRequest request,
        string encoderPath,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        bool includeX265UhdParameters,
        string preset,
        string tune,
        string profileValue,
        string outputPath,
        string rateControl)
    {
        var profile = request.Profile;
        var outputArg = profile.Kind switch
        {
            EncoderKind.X264 => $"-o {Quote(outputPath)}",
            EncoderKind.X265 => $"-o {Quote(outputPath)}",
            EncoderKind.SvtAv1 => $"-b {Quote(outputPath)}",
            _ => throw new ArgumentOutOfRangeException()
        };

        var directInputArgs = profile.Kind switch
        {
            EncoderKind.X264 => BuildX264DirectInputArguments(request.SourcePath, pipelineKind),
            EncoderKind.X265 => BuildX265DirectInputArguments(request.SourcePath, pipelineKind),
            EncoderKind.SvtAv1 => BuildSvtDirectInputArguments(request.SourcePath, pipelineKind),
            _ => throw new ArgumentOutOfRangeException()
        };
        var sourceMetadataArgs = BuildEncoderColorMetadataArguments(
            profile.Kind,
            sourceInfo,
            profile.AdditionalArguments,
            includeX265UhdParameters ? profile.UhdParameters : string.Empty);

        var arguments = profile.Kind switch
        {
            EncoderKind.X264 => BuildArgumentParts(
                $"--preset {preset}",
                rateControl,
                Optional("--tune", tune),
                Optional("--profile", profileValue),
                directInputArgs,
                TokenizeCommandLine(sourceMetadataArgs),
                TokenizeCommandLine(profile.AdditionalArguments),
                outputArg),
            EncoderKind.X265 => BuildArgumentParts(
                $"--preset {preset}",
                rateControl,
                Optional("--tune", tune),
                Optional("--profile", profileValue),
                directInputArgs,
                TokenizeCommandLine(sourceMetadataArgs),
                TokenizeCommandLine(profile.AdditionalArguments),
                TokenizeCommandLine(includeX265UhdParameters ? profile.UhdParameters : string.Empty),
                outputArg),
            EncoderKind.SvtAv1 => BuildArgumentParts(
                $"--preset {preset}",
                rateControl,
                Optional("--tune", tune),
                Optional("--profile", profileValue),
                "--progress 2",
                sourceInfo is null ? string.Empty : BuildSvtSourceArguments(sourceInfo),
                directInputArgs,
                TokenizeCommandLine(sourceMetadataArgs),
                TokenizeCommandLine(profile.AdditionalArguments),
                outputArg),
            _ => throw new ArgumentOutOfRangeException()
        };

        return new ProcessCommand(
            encoderPath,
            arguments,
            $"{Quote(encoderPath)} {string.Join(' ', arguments.Select(DisplayToken))}");
    }

    private static string JoinStepDisplayCommands(IReadOnlyList<EncodingExecutionStep> steps)
    {
        if (steps.Count == 1)
        {
            return steps[0].DisplayCommand;
        }

        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            steps.Select(step => $"[Pass {step.StageIndex}/{step.StageCount}]{Environment.NewLine}{step.DisplayCommand}"));
    }

    private static EncodingProgressSnapshot? BuildInitialSnapshot(EncodingExecutionPlan plan)
    {
        if (plan.TotalFrames is null)
        {
            return null;
        }

        return new EncodingProgressSnapshot(0, plan.TotalFrames, null, null, null, null);
    }

    private async Task<string> WriteSidecarLogAsync(
        EncodingJobRequest request,
        string displayCommand,
        EncodingJobState state,
        int exitCode,
        string rawLogPath)
    {
        var primaryLogPath = GetAvailableLogPath(request);
        var primaryError = await TryWriteSidecarLogAsync(primaryLogPath, request, displayCommand, state, exitCode, rawLogPath);
        if (primaryError is null)
        {
            return primaryLogPath;
        }

        var fallbackLogPath = GetFallbackLogPath(request);
        var fallbackError = await TryWriteSidecarLogAsync(fallbackLogPath, request, displayCommand, state, exitCode, rawLogPath);
        if (fallbackError is null)
        {
            WriteDiagnostic(
                $"Encoding job {request.JobId}: primary sidecar log write failed for '{primaryLogPath}', "
                + $"fallback saved to '{fallbackLogPath}'. {primaryError.GetType().Name}: {primaryError.Message}");
            return fallbackLogPath;
        }

        WriteDiagnostic(
            $"Encoding job {request.JobId}: failed to write sidecar log. "
            + $"Primary='{primaryLogPath}' ({primaryError.GetType().Name}: {primaryError.Message}); "
            + $"Fallback='{fallbackLogPath}' ({fallbackError.GetType().Name}: {fallbackError.Message}); "
            + $"RawLog='{rawLogPath}'.");
        return string.Empty;
    }

    private static async Task<Exception?> TryWriteSidecarLogAsync(
        string logPath,
        EncodingJobRequest request,
        string displayCommand,
        EncodingJobState state,
        int exitCode,
        string rawLogPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteLineAsync($"JobId: {request.JobId}");
            await writer.WriteLineAsync($"Encoder: {request.Profile.Kind.ToDisplayName()}");
            await writer.WriteLineAsync($"State: {state}");
            await writer.WriteLineAsync($"ExitCode: {exitCode}");
            await writer.WriteLineAsync($"Source: {request.SourcePath}");
            await writer.WriteLineAsync($"Output: {request.OutputPath}");
            await writer.WriteLineAsync($"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("--- COMMAND ---");
            await writer.WriteLineAsync(displayCommand);
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("--- LOG ---");

            if (File.Exists(rawLogPath))
            {
                await writer.FlushAsync();
                using var reader = File.OpenText(rawLogPath);
                await reader.BaseStream.CopyToAsync(stream);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        var buffer = new char[512];
        var current = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var ch = buffer[index];
                if (ch is '\r' or '\n')
                {
                    if (current.Length > 0)
                    {
                        onLine(current.ToString());
                        current.Clear();
                    }

                    continue;
                }

                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            onLine(current.ToString());
        }
    }

    private SourceVideoInfo? ResolveSourceInfo(
        EncodingJobRequest request,
        InputPipelineKind pipelineKind,
        bool required,
        bool allowCached,
        Action<string>? sourceProbeProgress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceInfo = _sourceInfoProbe.Probe(
                request.SourcePath,
                pipelineKind,
                sourceProbeProgress,
                cancellationToken,
                allowCached);
            if (sourceInfo is not null)
            {
                return sourceInfo;
            }
        }
        catch when (!required)
        {
        }

        if (required)
        {
            throw new InvalidOperationException(T(
                GetLanguage(),
                "SVT-AV1 requires detectable source metadata. Make sure the current input can be recognized by ffprobe or vspipe.",
                "SVT-AV1 需要可探测的源信息。请确保当前输入可被 ffprobe / vspipe 正常识别。"));
        }

        return null;
    }

    private static string BuildRateControlArguments(
        EncoderKind kind,
        EncodingProfile profile,
        string? statsPath = null,
        int? passIndex = null,
        int? passCount = null)
    {
        return profile.RateControl switch
        {
            RateControlMode.Crf => kind == EncoderKind.SvtAv1
                ? $"--rc 0 --crf {FormatNumber(profile.Quality)}"
                : $"--crf {FormatNumber(profile.Quality)}",
            RateControlMode.Cq or RateControlMode.Qp => kind == EncoderKind.SvtAv1
                ? $"--rc 0 --qp {FormatNumber(profile.Quality)}"
                : $"--qp {FormatNumber(profile.Quality)}",
            RateControlMode.Abr or RateControlMode.Vbr => kind == EncoderKind.SvtAv1
                ? $"--rc 1 --tbr {profile.Bitrate ?? 3500}"
                : $"--bitrate {profile.Bitrate ?? 3500}",
            RateControlMode.TwoPass => BuildTwoPassRateControlArguments(kind, profile, statsPath, passIndex, passCount),
            _ => string.Empty
        };
    }

    private static string BuildTwoPassRateControlArguments(
        EncoderKind kind,
        EncodingProfile profile,
        string? statsPath,
        int? passIndex,
        int? passCount)
    {
        return kind switch
        {
            EncoderKind.X264 or EncoderKind.X265 => JoinArguments(
                $"--bitrate {profile.Bitrate ?? 3500}",
                passIndex.HasValue ? $"--pass {passIndex.Value}" : "--pass 1",
                Optional("--stats", QuoteIfPresent(statsPath))),
            EncoderKind.SvtAv1 => JoinArguments(
                $"--rc 1 --tbr {profile.Bitrate ?? 3500}",
                passIndex.HasValue ? $"--pass {passIndex.Value}" : "--pass 1",
                Optional("--stats", QuoteIfPresent(statsPath))),
            _ => string.Empty
        };
    }

    internal static string BuildEncoderColorMetadataArguments(
        EncoderKind kind,
        SourceVideoInfo? sourceInfo,
        string? additionalArguments,
        string? x265UhdParameters)
    {
        if (sourceInfo is null)
        {
            return string.Empty;
        }

        return kind switch
        {
            EncoderKind.X264 => BuildX264ColorMetadataArguments(sourceInfo, [additionalArguments ?? string.Empty]),
            EncoderKind.X265 => BuildX265ColorMetadataArguments(sourceInfo, [additionalArguments ?? string.Empty, x265UhdParameters ?? string.Empty]),
            EncoderKind.SvtAv1 => BuildSvtColorMetadataArguments(sourceInfo, additionalArguments),
            _ => string.Empty
        };
    }

    private static string BuildX264ColorMetadataArguments(SourceVideoInfo sourceInfo, IReadOnlyList<string> manualArguments)
    {
        var parts = new List<string>();
        AddStringMetadataOption(parts, "--range", MapX264Range(sourceInfo.ColorRange), manualArguments);
        AddStringMetadataOption(parts, "--colorprim", NormalizeX26xColorValue(sourceInfo.ColorPrimaries, X26xColorPrimaries), manualArguments);
        AddStringMetadataOption(parts, "--transfer", NormalizeX26xColorValue(sourceInfo.ColorTransfer, X26xColorTransfers), manualArguments);
        AddStringMetadataOption(parts, "--colormatrix", NormalizeX26xColorValue(sourceInfo.ColorMatrix, X26xColorMatrices), manualArguments);
        AddStringMetadataOption(parts, "--mastering-display", sourceInfo.MasteringDisplay, manualArguments);
        return JoinArguments([.. parts]);
    }

    private static string BuildX265ColorMetadataArguments(SourceVideoInfo sourceInfo, IReadOnlyList<string> manualArguments)
    {
        var parts = new List<string>();
        var hasVideoSignalPreset = ArgumentsContainAnyOption(manualArguments, "--video-signal-type-preset");
        if (hasVideoSignalPreset)
        {
            return string.Empty;
        }

        AddStringMetadataOption(parts, "--range", MapX265Range(sourceInfo.ColorRange), manualArguments);
        AddStringMetadataOption(parts, "--colorprim", NormalizeX26xColorValue(sourceInfo.ColorPrimaries, X26xColorPrimaries), manualArguments);
        AddStringMetadataOption(parts, "--transfer", NormalizeX26xColorValue(sourceInfo.ColorTransfer, X26xColorTransfers), manualArguments);
        AddStringMetadataOption(parts, "--colormatrix", NormalizeX26xColorValue(sourceInfo.ColorMatrix, X26xColorMatrices), manualArguments);
        AddStringMetadataOption(parts, "--master-display", sourceInfo.MasteringDisplay, manualArguments);
        AddStringMetadataOption(parts, "--max-cll", sourceInfo.ContentLightLevel, manualArguments);
        return JoinArguments([.. parts]);
    }

    private static string BuildSvtColorMetadataArguments(SourceVideoInfo sourceInfo, string? manualArguments)
    {
        var parts = new List<string>();
        var manualArgumentList = new[] { manualArguments ?? string.Empty };

        AddStringMetadataOption(parts, "--color-range", MapSvtRange(sourceInfo.ColorRange), manualArgumentList);
        AddStringMetadataOption(parts, "--color-primaries", MapSvtColorPrimaries(sourceInfo.ColorPrimaries), manualArgumentList);
        AddStringMetadataOption(parts, "--transfer-characteristics", MapSvtColorTransfer(sourceInfo.ColorTransfer), manualArgumentList);
        AddStringMetadataOption(parts, "--matrix-coefficients", MapSvtColorMatrix(sourceInfo.ColorMatrix), manualArgumentList);
        AddStringMetadataOption(parts, "--chroma-sample-position", MapSvtChromaLocation(sourceInfo.ChromaLocation), manualArgumentList);
        AddStringMetadataOption(parts, "--mastering-display", sourceInfo.MasteringDisplay, manualArgumentList);
        AddStringMetadataOption(parts, "--content-light", sourceInfo.ContentLightLevel, manualArgumentList);
        return JoinArguments([.. parts]);
    }

    private static void AddStringMetadataOption(ICollection<string> parts, string optionName, string? value, IReadOnlyList<string> manualArguments)
    {
        if (string.IsNullOrWhiteSpace(value) || ArgumentsContainAnyOption(manualArguments, optionName))
        {
            return;
        }

        parts.Add($"{optionName} {value}");
    }

    private static string? NormalizeX26xColorValue(string? value, IReadOnlySet<string> supportedValues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return supportedValues.Contains(normalized) ? normalized : null;
    }

    private static string? MapX264Range(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "tv" or "limited" => "tv",
            "pc" or "full" => "pc",
            _ => null
        };
    }

    private static string? MapX265Range(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "tv" or "limited" => "limited",
            "pc" or "full" => "full",
            _ => null
        };
    }

    private static string? MapSvtRange(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "tv" or "limited" => "0",
            "pc" or "full" => "1",
            _ => null
        };
    }

    private static string? MapSvtColorPrimaries(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "bt709" => "1",
            "bt470m" => "4",
            "bt470bg" => "5",
            "smpte170m" => "6",
            "smpte240m" => "7",
            "film" => "8",
            "bt2020" => "9",
            "smpte428" => "10",
            "smpte431" => "11",
            "smpte432" => "12",
            "ebu3213" or "jedec-p22" => "22",
            _ => null
        };
    }

    private static string? MapSvtColorTransfer(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "bt709" => "1",
            "bt470m" => "4",
            "bt470bg" => "5",
            "smpte170m" => "6",
            "smpte240m" => "7",
            "linear" => "8",
            "log100" => "9",
            "log316" => "10",
            "iec61966-2-4" => "11",
            "bt1361e" => "12",
            "iec61966-2-1" => "13",
            "bt2020-10" => "14",
            "bt2020-12" => "15",
            "smpte2084" => "16",
            "smpte428" => "17",
            "arib-std-b67" => "18",
            _ => null
        };
    }

    private static string? MapSvtColorMatrix(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "gbr" or "rgb" => "0",
            "bt709" => "1",
            "fcc" => "4",
            "bt470bg" => "5",
            "smpte170m" => "6",
            "smpte240m" => "7",
            "ycgco" => "8",
            "bt2020nc" => "9",
            "bt2020c" => "10",
            "smpte2085" => "11",
            "chroma-derived-nc" => "12",
            "chroma-derived-c" => "13",
            "ictcp" => "14",
            _ => null
        };
    }

    private static string? MapSvtChromaLocation(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "left" or "vertical" => "left",
            "topleft" or "colocated" or "top-left" => "topleft",
            _ => null
        };
    }

    private static bool ArgumentsContainAnyOption(IReadOnlyList<string> arguments, params string[] optionNames)
    {
        foreach (var argument in arguments)
        {
            if (ArgumentsContainAnyOption(argument, optionNames))
            {
                return true;
            }
        }

        return false;
    }

    private ProcessCommand? BuildSourceCommand(EncodingJobRequest request, InputPipelineKind pipelineKind)
    {
        return pipelineKind switch
        {
            InputPipelineKind.VapourSynth => CreateProcessCommand(
                _toolLocator.ResolveVspipe(),
                request.SourcePath,
                "-",
                "--container",
                "y4m"),
            InputPipelineKind.AviSynth => CreateProcessCommand(
                _toolLocator.ResolveAvs2PipeMod(),
                "-y4mp",
                request.SourcePath),
            InputPipelineKind.FfmpegPipe => CreateProcessCommand(
                _toolLocator.ResolveFfmpeg(),
                "-hide_banner",
                "-loglevel",
                "error",
                "-i",
                request.SourcePath,
                "-map",
                "0:v:0",
                "-an",
                "-sn",
                "-dn",
                "-strict",
                "-1",
                "-f",
                "yuv4mpegpipe",
                "-"),
            InputPipelineKind.RawYuvFile or InputPipelineKind.Y4mFile => null,
            _ => null
        };
    }

    private static ProcessCommand CreateProcessCommand(string executablePath, params string[] arguments)
    {
        return new ProcessCommand(
            executablePath,
            arguments,
            $"{Quote(executablePath)} {string.Join(' ', arguments.Select(DisplayToken))}");
    }

    private static IReadOnlyList<string> BuildArgumentParts(params object?[] parts)
    {
        var result = new List<string>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case null:
                    break;
                case string text when !string.IsNullOrWhiteSpace(text):
                    result.AddRange(TokenizeCommandLine(text));
                    break;
                case IEnumerable<string> values:
                    result.AddRange(values.Where(static value => !string.IsNullOrWhiteSpace(value)));
                    break;
            }
        }

        return result;
    }

    private static string DisplayToken(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.IndexOfAny([' ', '\t', '"']) >= 0
            ? Quote(value.Replace("\"", "\\\""))
            : value;
    }

    private static bool ArgumentsContainAnyOption(string? arguments, params string[] optionNames)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        var options = new HashSet<string>(optionNames, StringComparer.OrdinalIgnoreCase);
        foreach (var token in TokenizeCommandLine(arguments))
        {
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var optionName = token.Split('=', 2)[0];
            if (options.Contains(optionName))
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<string> TokenizeCommandLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var argv = CommandLineToArgvW(NormalizeLegacySingleQuotedArguments(value), out var argc);
        if (argv == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to tokenize command line arguments. Win32Error={Marshal.GetLastWin32Error()}");
        }

        try
        {
            var result = new List<string>(argc);
            for (var index = 0; index < argc; index++)
            {
                var item = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                var token = Marshal.PtrToStringUni(item);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    result.Add(token);
                }
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static string NormalizeLegacySingleQuotedArguments(string value)
    {
        if (value.IndexOf('\'') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var inDoubleQuotes = false;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"')
            {
                inDoubleQuotes = !inDoubleQuotes;
                builder.Append(character);
                continue;
            }

            if (character == '\''
                && !inDoubleQuotes
                && (index == 0 || char.IsWhiteSpace(value[index - 1])))
            {
                var closingIndex = value.IndexOf('\'', index + 1);
                if (closingIndex > index)
                {
                    AppendDoubleQuotedArgument(builder, value.AsSpan(index + 1, closingIndex - index - 1));
                    index = closingIndex;
                    continue;
                }
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static void AppendDoubleQuotedArgument(StringBuilder builder, ReadOnlySpan<char> value)
    {
        builder.Append('"');

        var backslashCount = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount);
                backslashCount = 0;
            }

            builder.Append(character);
        }

        if (backslashCount > 0)
        {
            builder.Append('\\', backslashCount * 2);
        }

        builder.Append('"');
    }

    private static readonly HashSet<string> X26xColorPrimaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "bt709",
        "bt470m",
        "bt470bg",
        "smpte170m",
        "smpte240m",
        "film",
        "bt2020",
        "smpte428",
        "smpte431",
        "smpte432"
    };

    private static readonly HashSet<string> X26xColorTransfers = new(StringComparer.OrdinalIgnoreCase)
    {
        "bt709",
        "bt470m",
        "bt470bg",
        "smpte170m",
        "smpte240m",
        "linear",
        "log100",
        "log316",
        "iec61966-2-4",
        "bt1361e",
        "iec61966-2-1",
        "bt2020-10",
        "bt2020-12",
        "smpte2084",
        "smpte428",
        "arib-std-b67"
    };

    private static readonly HashSet<string> X26xColorMatrices = new(StringComparer.OrdinalIgnoreCase)
    {
        "bt709",
        "fcc",
        "bt470bg",
        "smpte170m",
        "smpte240m",
        "gbr",
        "ycgco",
        "bt2020nc",
        "bt2020c",
        "smpte2085",
        "chroma-derived-nc",
        "chroma-derived-c",
        "ictcp"
    };

    private static string BuildSvtSourceArguments(SourceVideoInfo sourceInfo)
    {
        return JoinArguments(
            $"--width {sourceInfo.Width}",
            $"--height {sourceInfo.Height}",
            sourceInfo.TotalFrames is > 0 ? $"--frames {sourceInfo.TotalFrames.Value}" : string.Empty,
            sourceInfo.BitDepth > 0 ? $"--input-depth {sourceInfo.BitDepth}" : string.Empty);
    }

    private static bool ShouldReportProgress(
        EncoderKind kind,
        string line,
        ParsedProgressSnapshot? progressSnapshot,
        ref ProgressDispatchState state)
    {
        var now = DateTimeOffset.UtcNow;
        var currentProgressFraction = progressSnapshot?.ProgressFraction;
        var currentFrame = progressSnapshot?.Snapshot?.CurrentFrame;
        var isTransient = EncodingLogLineClassifier.IsTransientProgressLine(kind, line);

        if (!isTransient)
        {
            state = new ProgressDispatchState(now, currentProgressFraction, currentFrame, line);
            return true;
        }

        if (progressSnapshot is null)
        {
            var intervalElapsedWithoutSnapshot = now - state.LastReportedAt >= TransientProgressReportInterval;
            var lineChanged = !string.Equals(line, state.LastReportedLine, StringComparison.Ordinal);
            if (!intervalElapsedWithoutSnapshot || !lineChanged)
            {
                return false;
            }

            state = new ProgressDispatchState(now, state.LastProgressFraction, state.LastCurrentFrame, line);
            return true;
        }

        var intervalElapsed = now - state.LastReportedAt >= TransientProgressReportInterval;
        var hasMeaningfulProgressDelta = currentProgressFraction.HasValue
            && (!state.LastProgressFraction.HasValue
                || Math.Abs(currentProgressFraction.Value - state.LastProgressFraction.Value) >= 0.0025);
        var frameAdvanced = currentFrame != state.LastCurrentFrame;

        if (!frameAdvanced && !hasMeaningfulProgressDelta)
        {
            return false;
        }

        if (!intervalElapsed && !hasMeaningfulProgressDelta)
        {
            return false;
        }

        state = new ProgressDispatchState(now, currentProgressFraction, currentFrame, line);
        return true;
    }

    private static bool ShouldSurfaceLineDuringSourcePreparation(string line)
    {
        return line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || line.Contains("traceback", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldAppendSourcePreparationVisibleLogLine(string line)
    {
        return ShouldSurfaceLineDuringSourcePreparation(line);
    }

    private static ParsedProgressSnapshot? ParseProgressSnapshot(
        EncoderKind kind,
        long? totalFrames,
        double? sourceFramesPerSecond,
        string line)
    {
        var normalizedLine = EncoderConsoleLineNormalizer.Normalize(line);
        if (string.IsNullOrWhiteSpace(normalizedLine))
        {
            return null;
        }

        if (kind is EncoderKind.X264 or EncoderKind.X265)
        {
            var normalizedX26xLine = NormalizeX26xProgressPrefix(normalizedLine);

            var x265PipeMetricsMatch = X265PipeMetricsRegex.Match(normalizedX26xLine);
            if (x265PipeMetricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(x265PipeMetricsMatch.Groups["current"].Value);
                var parsedTotalFrames = ParseInvariantLong(x265PipeMetricsMatch.Groups["total"].Value) ?? totalFrames;
                var progress = TryBuildProgressFraction(
                    ParseInvariantDoubleNullable(x265PipeMetricsMatch.Groups["progress"].Value),
                    currentFrame,
                    parsedTotalFrames);
                var fps = ParseInvariantDoubleNullable(x265PipeMetricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(x265PipeMetricsMatch.Groups["bitrate"].Value);
                var eta = ParseEta(x265PipeMetricsMatch.Groups["remainingeta"].Value)
                    ?? ParseEta(x265PipeMetricsMatch.Groups["eta"].Value);
                var estimatedFileSizeBytes =
                    ParseSizeToBytes(x265PipeMetricsMatch.Groups["size"].Value, x265PipeMetricsMatch.Groups["unit"].Value)
                    ?? ParseSizeToBytes(x265PipeMetricsMatch.Groups["currentsize"].Value, x265PipeMetricsMatch.Groups["currentunit"].Value);

                return new ParsedProgressSnapshot(
                    progress,
                    new EncodingProgressSnapshot(currentFrame, parsedTotalFrames, fps, bitrate, eta, estimatedFileSizeBytes));
            }

            var metricsMatch = X26xMetricsRegex.Match(normalizedX26xLine);
            if (metricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(metricsMatch.Groups["current"].Value)
                    ?? ParseInvariantLong(metricsMatch.Groups["framesonly"].Value);
                var parsedTotalFrames = ParseInvariantLong(metricsMatch.Groups["total"].Value) ?? totalFrames;
                var progress = TryBuildProgressFraction(
                    ParseInvariantDoubleNullable(metricsMatch.Groups["progress"].Value),
                    currentFrame,
                    parsedTotalFrames);
                var fps = ParseInvariantDoubleNullable(metricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(metricsMatch.Groups["bitrate"].Value);
                var eta = ParseEta(metricsMatch.Groups["eta"].Value);
                var estimatedFileSizeBytes = ParseSizeToBytes(metricsMatch.Groups["size"].Value, metricsMatch.Groups["unit"].Value);

                return new ParsedProgressSnapshot(
                    progress,
                    new EncodingProgressSnapshot(currentFrame, parsedTotalFrames, fps, bitrate, eta, estimatedFileSizeBytes));
            }

            var frameMetricsMatch = X26xFrameMetricsRegex.Match(normalizedX26xLine);
            if (frameMetricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(frameMetricsMatch.Groups["current"].Value);
                var fps = ParseInvariantDoubleNullable(frameMetricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(frameMetricsMatch.Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, totalFrames);
                var eta = currentFrame.HasValue && totalFrames is > 0 && fps is > 0
                    ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(0, (totalFrames.Value - currentFrame.Value) / fps.Value))
                    : null;
                var estimatedSizeBytes = totalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                    ? (long?)EstimateFileSizeBytes(totalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                    : null;

                return new ParsedProgressSnapshot(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, totalFrames, fps, bitrate, eta, estimatedSizeBytes));
            }

            var pipeFrameMetricsMatch = X26xPipeFrameMetricsRegex.Match(normalizedX26xLine);
            if (pipeFrameMetricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(pipeFrameMetricsMatch.Groups["current"].Value);
                var fps = ParseInvariantDoubleNullable(pipeFrameMetricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(pipeFrameMetricsMatch.Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, totalFrames);
                var eta = currentFrame.HasValue && totalFrames is > 0 && fps is > 0
                    ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(0, (totalFrames.Value - currentFrame.Value) / fps.Value))
                    : null;
                var estimatedSizeBytes = totalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                    ? (long?)EstimateFileSizeBytes(totalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                    : ParseSizeToBytes(pipeFrameMetricsMatch.Groups["currentsize"].Value, pipeFrameMetricsMatch.Groups["currentunit"].Value);

                return new ParsedProgressSnapshot(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, totalFrames, fps, bitrate, eta, estimatedSizeBytes));
            }

            var encodedSummaryMatch = X26xEncodedSummaryRegex.Match(normalizedX26xLine);
            if (encodedSummaryMatch.Success)
            {
                var currentFrame = ParseInvariantLong(encodedSummaryMatch.Groups["current"].Value);
                var fps = ParseInvariantDoubleNullable(encodedSummaryMatch.Groups["fps"].Value)
                    ?? ParseInvariantDoubleNullable(encodedSummaryMatch.Groups["fpsparenthesized"].Value);
                var bitrate = ParseInvariantDoubleNullable(encodedSummaryMatch.Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, totalFrames);
                var estimatedSizeBytes = totalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                    ? (long?)EstimateFileSizeBytes(totalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                    : null;

                return new ParsedProgressSnapshot(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, totalFrames, fps, bitrate, null, estimatedSizeBytes));
            }

            var looseSnapshot = TryParseLooseX26xMetrics(normalizedX26xLine, totalFrames, sourceFramesPerSecond);
            if (looseSnapshot is not null)
            {
                return looseSnapshot;
            }

            var match = X26xProgressRegex.Match(normalizedX26xLine);
            if (match.Success)
            {
                return new ParsedProgressSnapshot(
                    Math.Clamp(ParseInvariantDouble(match.Groups["progress"].Value) / 100.0, 0.0, 1.0),
                    null);
            }
        }

        if (kind == EncoderKind.SvtAv1)
        {
            var statusPrefixMatch = SvtStatusPrefixRegex.Match(normalizedLine);
            if (statusPrefixMatch.Success)
            {
                var currentFrame = ParseInvariantLong(statusPrefixMatch.Groups["current"].Value);
                var parsedTotalFrames = ParseInvariantLong(statusPrefixMatch.Groups["total"].Value) ?? totalFrames;
                var fps = ParseInvariantDoubleNullable(SvtStatusFpsRegex.Match(normalizedLine).Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(SvtStatusBitrateRegex.Match(normalizedLine).Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, parsedTotalFrames);
                var statusTimeMatch = SvtStatusTimeRegex.Match(normalizedLine);
                var eta = ParseEta(statusTimeMatch.Groups["eta"].Value)
                    ?? (currentFrame.HasValue && parsedTotalFrames is > 0 && fps is > 0
                        ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(0, (parsedTotalFrames.Value - currentFrame.Value) / fps.Value))
                        : null);
                var statusSizeMatch = SvtStatusSizeRegex.Match(normalizedLine);
                var estimatedSizeBytes =
                    ParseSizeToBytes(statusSizeMatch.Groups["size"].Value, statusSizeMatch.Groups["unit"].Value)
                    ?? ParseSizeToBytes(statusSizeMatch.Groups["currentsize"].Value, statusSizeMatch.Groups["currentunit"].Value)
                    ?? (parsedTotalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                        ? (long?)EstimateFileSizeBytes(parsedTotalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                        : null);

                return new ParsedProgressSnapshot(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, parsedTotalFrames, fps, bitrate, eta, estimatedSizeBytes));
            }

            var looseMetricsMatch = SvtLooseMetricsRegex.Match(normalizedLine);
            if (looseMetricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(looseMetricsMatch.Groups["current"].Value);
                var parsedTotalFrames = ParseInvariantLong(looseMetricsMatch.Groups["total"].Value) ?? totalFrames;
                var fps = ParseInvariantDoubleNullable(looseMetricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(looseMetricsMatch.Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, parsedTotalFrames);
                var eta = currentFrame.HasValue && parsedTotalFrames is > 0 && fps is > 0
                    ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(0, (parsedTotalFrames.Value - currentFrame.Value) / fps.Value))
                    : null;
                var estimatedSizeBytes = parsedTotalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                    ? (long?)EstimateFileSizeBytes(parsedTotalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                    : null;

                return new ParsedProgressSnapshot(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, parsedTotalFrames, fps, bitrate, eta, estimatedSizeBytes));
            }

            var metricsMatch = SvtMetricsRegex.Match(normalizedLine);
            if (metricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(metricsMatch.Groups["current"].Value);
                var fps = ParseInvariantDoubleNullable(metricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(metricsMatch.Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, totalFrames);
                var eta = currentFrame.HasValue && totalFrames is > 0 && fps is > 0
                    ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(0, (totalFrames.Value - currentFrame.Value) / fps.Value))
                    : null;
                var estimatedSizeBytes = currentFrame.HasValue && totalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                    ? (long?)EstimateFileSizeBytes(totalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                    : null;

                return new ParsedProgressSnapshot(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, totalFrames, fps, bitrate, eta, estimatedSizeBytes));
            }

            var match = SvtFrameRegex.Match(normalizedLine);
            if (!match.Success)
            {
                match = SvtOutputRegex.Match(normalizedLine);
            }

            if (match.Success
                && totalFrames is > 0
                && long.TryParse(match.Groups["frame"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
            {
                return new ParsedProgressSnapshot(
                    Math.Clamp(frame / (double)totalFrames.Value, 0.0, 1.0),
                    new EncodingProgressSnapshot(frame, totalFrames, null, null, null, null));
            }
        }

        return null;
    }

    internal static (double? ProgressFraction, EncodingProgressSnapshot? Snapshot) ParseProgressSnapshotForTesting(
        EncoderKind kind,
        long? totalFrames,
        double? sourceFramesPerSecond,
        string line)
    {
        var snapshot = ParseProgressSnapshot(kind, totalFrames, sourceFramesPerSecond, line);
        return snapshot is null
            ? (null, null)
            : (snapshot.ProgressFraction, snapshot.Snapshot);
    }

    internal static int? ParseSourcePreparationProgressPercentForTesting(string line)
        => ParseSourcePreparationProgressPercent(EncoderConsoleLineNormalizer.Normalize(line));

    internal static bool ShouldSurfaceLineDuringSourcePreparationForTesting(string line)
        => ShouldSurfaceLineDuringSourcePreparation(EncoderConsoleLineNormalizer.Normalize(line));

    internal static bool ShouldAppendSourcePreparationVisibleLogLineForTesting(string line)
        => ShouldAppendSourcePreparationVisibleLogLine(EncoderConsoleLineNormalizer.Normalize(line));

    internal static string TrimVisibleLogForTesting(string text)
    {
        var builder = new StringBuilder(text);
        TrimVisibleLogIfNeeded(builder);
        return builder.ToString();
    }

    private static ParsedProgressSnapshot? TryParseLooseX26xMetrics(
        string line,
        long? totalFrames,
        double? sourceFramesPerSecond)
    {
        if (!(line.Contains('%')
              || line.Contains("frame=", StringComparison.OrdinalIgnoreCase)
              || line.Contains("frames", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (!line.Contains("fps", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var explicitPercent = ParseInvariantDoubleNullable(X26xProgressRegex.Match(line).Groups["progress"].Value);

        var frameRatioMatch = X26xFrameRatioRegex.Match(line);
        long? currentFrame = null;
        long? parsedTotalFrames = totalFrames;
        if (frameRatioMatch.Success)
        {
            currentFrame = ParseInvariantLong(frameRatioMatch.Groups["current"].Value);
            parsedTotalFrames = ParseInvariantLong(frameRatioMatch.Groups["total"].Value) ?? totalFrames;
        }
        else
        {
            var frameEqualsMatch = X26xFrameEqualsRegex.Match(line);
            if (frameEqualsMatch.Success)
            {
                currentFrame = ParseInvariantLong(frameEqualsMatch.Groups["current"].Value);
            }
        }

        var fps = ParseInvariantDoubleNullable(X26xLooseFpsRegex.Match(line).Groups["fps"].Value);
        var bitrate = ParseInvariantDoubleNullable(X26xLooseBitrateRegex.Match(line).Groups["bitrate"].Value);

        var etaMatch = X26xLooseEtaRegex.Match(line);
        var eta = ParseEta(etaMatch.Groups["remainingeta"].Value)
            ?? ParseEta(etaMatch.Groups["eta"].Value);

        long? estimatedFileSizeBytes = null;
        var sizeMatch = X26xLooseSizeRegex.Match(line);
        if (sizeMatch.Success)
        {
            estimatedFileSizeBytes = ParseSizeToBytes(sizeMatch.Groups["size"].Value, sizeMatch.Groups["unit"].Value);
        }

        if (!estimatedFileSizeBytes.HasValue)
        {
            var bracketedSizeMatches = X26xBracketedSizeRegex.Matches(line);
            if (bracketedSizeMatches.Count > 0)
            {
                var lastSize = bracketedSizeMatches[bracketedSizeMatches.Count - 1];
                estimatedFileSizeBytes = ParseSizeToBytes(lastSize.Groups["size"].Value, lastSize.Groups["unit"].Value);
            }
        }

        if (!estimatedFileSizeBytes.HasValue && parsedTotalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0)
        {
            estimatedFileSizeBytes = EstimateFileSizeBytes(parsedTotalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value);
        }

        var progressFraction = TryBuildProgressFraction(explicitPercent, currentFrame, parsedTotalFrames);

        if (!progressFraction.HasValue
            && !currentFrame.HasValue
            && !fps.HasValue
            && !bitrate.HasValue
            && !eta.HasValue
            && !estimatedFileSizeBytes.HasValue)
        {
            return null;
        }

        return new ParsedProgressSnapshot(
            progressFraction,
            new EncodingProgressSnapshot(currentFrame, parsedTotalFrames, fps, bitrate, eta, estimatedFileSizeBytes));
    }

    private static string NormalizeX26xProgressPrefix(string line)
    {
        if (line.StartsWith("x264 ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("x265 ", StringComparison.OrdinalIgnoreCase))
        {
            var bracketIndex = line.IndexOf('[');
            if (bracketIndex > 0)
            {
                return line[bracketIndex..].TrimStart();
            }
        }

        return line;
    }

    private static double ParseInvariantDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0.0;
    }

    private static double? ParseInvariantDoubleNullable(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? ParseInvariantLong(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static TimeSpan? ParseEta(string value)
    {
        var normalized = value?.Trim().TrimStart('-');
        return TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var eta)
            ? eta
            : null;
    }

    private static long? ParseSizeToBytes(string sizeValue, string unit)
    {
        if (string.IsNullOrWhiteSpace(sizeValue))
        {
            return null;
        }

        var size = ParseInvariantDouble(sizeValue);
        var multiplier = unit.Trim().ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            "TB" => 1024d * 1024d * 1024d * 1024d,
            _ => 1d
        };

        return (long)Math.Round(size * multiplier, MidpointRounding.AwayFromZero);
    }

    private static long EstimateFileSizeBytes(long totalFrames, double sourceFramesPerSecond, double bitrateKbps)
    {
        var durationSeconds = totalFrames / sourceFramesPerSecond;
        var bytes = durationSeconds * (bitrateKbps * 1000d / 8d);
        return (long)Math.Round(bytes, MidpointRounding.AwayFromZero);
    }

    private static double? TryBuildProgressFraction(double? explicitPercent, long? currentFrame, long? totalFrames)
    {
        if (explicitPercent.HasValue)
        {
            return Math.Clamp(explicitPercent.Value / 100.0, 0.0, 1.0);
        }

        if (currentFrame.HasValue && totalFrames is > 0)
        {
            return Math.Clamp(currentFrame.Value / (double)totalFrames.Value, 0.0, 1.0);
        }

        return null;
    }

    private static string GetAvailableLogPath(EncodingJobRequest request)
    {
        var outputPath = request.OutputPath;
        var directory = Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory;
        var baseName = Path.GetFileNameWithoutExtension(outputPath);
        var suffix = BuildLogFileSuffix(request.Profile);
        var extension = ".log";
        var candidate = Path.Combine(directory, $"{baseName}{suffix}{extension}");

        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (var index = 0; index < 10000; index++)
        {
            var timestampSuffix = index == 0
                ? $"_{DateTime.Now:yyyyMMdd_HHmmss}"
                : $"_{DateTime.Now:yyyyMMdd_HHmmss}_{index + 1}";
            candidate = Path.Combine(directory, $"{baseName}{suffix}{timestampSuffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{baseName}{suffix}_{Guid.NewGuid():N}{extension}");
    }

    private string GetFallbackLogPath(EncodingJobRequest request)
    {
        var baseName = Path.GetFileNameWithoutExtension(request.OutputPath);
        var prefix = string.IsNullOrWhiteSpace(baseName)
            ? request.JobId.ToString("N")
            : SanitizeFileName(baseName);
        var suffix = BuildLogFileSuffix(request.Profile);
        var candidate = Path.Combine(_appPaths.LogsRootPath, $"{prefix}{suffix}.log");

        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (var index = 0; index < 10000; index++)
        {
            var timestampSuffix = index == 0
                ? $"_{DateTime.Now:yyyyMMdd_HHmmss}"
                : $"_{DateTime.Now:yyyyMMdd_HHmmss}_{index + 1}";
            candidate = Path.Combine(_appPaths.LogsRootPath, $"{prefix}{suffix}{timestampSuffix}.log");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(_appPaths.LogsRootPath, $"{prefix}{suffix}_{Guid.NewGuid():N}.log");
    }

    private static string BuildLogFileSuffix(EncodingProfile profile)
    {
        var encoderToken = profile.Kind.ToShortName();

        var rateToken = profile.RateControl switch
        {
            RateControlMode.Crf => $"_crf{FormatFileTokenNumber(profile.Quality)}",
            RateControlMode.Cq => $"_cq{FormatFileTokenNumber(profile.Quality)}",
            RateControlMode.Qp => $"_qp{FormatFileTokenNumber(profile.Quality)}",
            RateControlMode.Abr => $"_abr{profile.Bitrate ?? 3500}",
            RateControlMode.Vbr => $"_vbr{profile.Bitrate ?? 3500}",
            RateControlMode.TwoPass => $"_2pass{profile.Bitrate ?? 3500}",
            _ => string.Empty
        };

        return $"_{encoderToken}{rateToken}";
    }

    private static string FormatFileTokenNumber(double value)
    {
        return value
            .ToString("0.0##", CultureInfo.InvariantCulture)
            .TrimEnd('0')
            .TrimEnd('.')
            .Replace('.', '_');
    }

    private static string LastMeaningfulLine(string log)
    {
        return log
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? string.Empty;
    }

    internal static Process CreateProcess(ProcessCommand command, string encoderPath, bool redirectStandardInput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(encoderPath) ?? AppContext.BaseDirectory
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
    }

    private static Process CreateSourceProcess(ProcessCommand command, string encoderPath)
    {
        return CreateProcess(command, encoderPath);
    }

    private static async Task CopyPipeAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, cancellationToken);
        }
        finally
        {
            try
            {
                await destination.DisposeAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to dispose encoding pipe destination. {ex}");
            }
        }
    }

    private static async Task<int> GetExitCodeAsync(Process process, Task waitForExitTask)
    {
        await waitForExitTask;
        return process.ExitCode;
    }

    private static bool TryTerminateProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ParsedProgressSnapshot? ApplyStageProgress(ParsedProgressSnapshot? progressSnapshot, EncodingExecutionStep step)
    {
        if (progressSnapshot is null)
        {
            return null;
        }

        var overallProgress = progressSnapshot.ProgressFraction.HasValue
            ? Math.Clamp(((step.StageIndex - 1) + progressSnapshot.ProgressFraction.Value) / step.StageCount, 0.0, 1.0)
            : (double?)null;

        return progressSnapshot with { ProgressFraction = overallProgress };
    }

    private static int? ParseSourcePreparationProgressPercent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var match = LsmasLwiIndexProgressRegex.Match(line);
        if (!match.Success)
        {
            match = BestSourceIndexProgressRegex.Match(line);
            if (!match.Success)
            {
                return null;
            }
        }

        return int.TryParse(match.Groups["progress"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var progress)
            ? Math.Clamp(progress, 0, 100)
            : null;
    }

    private static double BuildStageStartingProgress(EncodingExecutionStep step)
    {
        return step.StageCount <= 1
            ? 0.0
            : Math.Clamp((step.StageIndex - 1) / (double)step.StageCount, 0.0, 1.0);
    }

    private static double BuildStageFailureProgress(EncodingExecutionStep step)
    {
        return step.StageCount <= 1
            ? 0.0
            : Math.Clamp((step.StageIndex - 1) / (double)step.StageCount, 0.0, 1.0);
    }

    private static int ResolveStageExitCode(int encoderExitCode, int? sourceExitCode)
    {
        return encoderExitCode != 0
            ? encoderExitCode
            : sourceExitCode ?? 0;
    }

    private static string BuildStageFailureSummary(AppLanguage language, EncodingExecutionStep step, int encoderExitCode, int? sourceExitCode)
    {
        if (sourceExitCode.HasValue && sourceExitCode.Value != 0)
        {
            return step.StageCount > 1
                ? T(language,
                    $"Pass {step.StageIndex}/{step.StageCount} failed (source exit code {sourceExitCode.Value}, encoder exit code {encoderExitCode})",
                    $"第 {step.StageIndex}/{step.StageCount} 遍失败，源进程退出代码 {sourceExitCode.Value}，编码器退出代码 {encoderExitCode}")
                : T(language,
                    $"Encoding failed (source exit code {sourceExitCode.Value}, encoder exit code {encoderExitCode})",
                    $"编码失败，源进程退出代码 {sourceExitCode.Value}，编码器退出代码 {encoderExitCode}");
        }

        return step.StageCount > 1
            ? T(language, $"Pass {step.StageIndex}/{step.StageCount} failed (exit code {encoderExitCode})", $"第 {step.StageIndex}/{step.StageCount} 遍失败，退出代码 {encoderExitCode}")
            : T(language, $"Encoding failed (exit code {encoderExitCode})", $"编码失败，退出代码 {encoderExitCode}");
    }

    private static string BuildStageStartingSummary(AppLanguage language, EncodingExecutionStep step)
    {
        return step.StageCount > 1
            ? T(language, $"Starting pass {step.StageIndex}/{step.StageCount}", $"开始第 {step.StageIndex}/{step.StageCount} 遍")
            : T(language, "Encoding started", "编码已启动");
    }

    private static string BuildRunningSummary(AppLanguage language, EncodingExecutionStep step, double? progressFraction)
    {
        if (step.StageCount > 1)
        {
            return T(language, $"Pass {step.StageIndex}/{step.StageCount} running", $"第 {step.StageIndex}/{step.StageCount} 遍编码中");
        }

        return progressFraction is { } progressValue
            ? T(language, $"Encoding {progressValue:P0}", $"编码中 {progressValue:P0}")
            : T(language, "Encoding", "编码中");
    }

    private static string BuildSourceRunningSummary(AppLanguage language, EncodingExecutionStep step, int? sourcePreparationProgressPercent)
    {
        if (sourcePreparationProgressPercent.HasValue)
        {
            return step.StageCount > 1
                ? T(
                    language,
                    $"Pass {step.StageIndex}/{step.StageCount}: preparing source {sourcePreparationProgressPercent.Value}%",
                    $"第 {step.StageIndex}/{step.StageCount} 遍：正在准备源 {sourcePreparationProgressPercent.Value}%")
                : T(
                    language,
                    $"Preparing source {sourcePreparationProgressPercent.Value}%",
                    $"正在准备源 {sourcePreparationProgressPercent.Value}%");
        }

        return step.StageCount > 1
            ? T(
                language,
                $"Pass {step.StageIndex}/{step.StageCount}: preparing source...",
                $"第 {step.StageIndex}/{step.StageCount} 遍：正在准备源...")
            : T(
                language,
                "Preparing source...",
                "正在准备源...");
    }

    private static string BuildSourceProbeSummary(AppLanguage language, int? sourcePreparationProgressPercent)
    {
        return sourcePreparationProgressPercent.HasValue
            ? T(
                language,
                $"Preparing source {sourcePreparationProgressPercent.Value}%",
                $"正在准备源 {sourcePreparationProgressPercent.Value}%")
            : T(
                language,
                "Preparing source...",
                "正在准备源...");
    }

    private static string BuildStageStartingDetail(AppLanguage language, EncodingExecutionStep step)
    {
        return step.StageCount > 1
            ? T(language, $"Starting pass {step.StageIndex}/{step.StageCount}.", $"开始执行第 {step.StageIndex}/{step.StageCount} 遍。")
            : T(language, "Starting the encoding job.", "开始执行编码任务。");
    }

    private static EncodingProgressSnapshot? BuildStageStartingSnapshot(EncodingExecutionPlan plan, EncodingExecutionStep step)
    {
        if (plan.TotalFrames is null)
        {
            return null;
        }

        return new EncodingProgressSnapshot(0, plan.TotalFrames, null, null, null, null);
    }

    private static void AppendStageHeader(EncodingExecutionStep step, StreamWriter rawLogWriter, StringBuilder visibleLogBuilder)
    {
        if (step.StageCount <= 1)
        {
            return;
        }

        AppendLogLine(rawLogWriter, $"--- PASS {step.StageIndex}/{step.StageCount} ---");
        AppendLogLine(rawLogWriter, step.DisplayCommand);
        AppendLogLine(visibleLogBuilder, $"--- PASS {step.StageIndex}/{step.StageCount} ---");
        AppendLogLine(visibleLogBuilder, step.DisplayCommand);
    }

    private static void AppendLogLine(StreamWriter writer, string line)
    {
        writer.WriteLine(line);
        writer.WriteLine();
    }

    private static void AppendLogLine(StringBuilder builder, string line)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.AppendLine(line);
        TrimVisibleLogIfNeeded(builder);
    }

    private static void TrimVisibleLogIfNeeded(StringBuilder builder)
    {
        if (builder.Length <= MaxVisibleLogLength)
        {
            return;
        }

        var removeCount = Math.Max(0, builder.Length - RetainedVisibleLogLength);
        if (removeCount > 0)
        {
            builder.Remove(0, removeCount);
        }

        var firstLineBreak = IndexOfLineBreak(builder);
        if (firstLineBreak >= 0 && firstLineBreak + 1 < builder.Length)
        {
            builder.Remove(0, firstLineBreak + 1);
        }

        if (!StartsWith(builder, VisibleLogTruncationMarker))
        {
            builder.Insert(0, $"{VisibleLogTruncationMarker}{Environment.NewLine}");
        }
    }

    private static int IndexOfLineBreak(StringBuilder builder)
    {
        for (var index = 0; index < builder.Length; index++)
        {
            var character = builder[index];
            if (character is '\r' or '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static bool StartsWith(StringBuilder builder, string value)
    {
        if (builder.Length < value.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (builder[index] != value[index])
            {
                return false;
            }
        }

        return true;
    }

    private string CreateTemporaryRawLogPath(EncodingJobRequest request)
    {
        var directory = Path.Combine(GetJobTempDirectory(request), "logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{request.JobId:N}.raw.log");
    }

    private static StreamWriter CreateRawLogWriter(string path)
    {
        var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream, Encoding.UTF8);
    }

    private void CleanupTemporaryRawLog(string path)
    {
        BestEffortCleanup.DeleteFile(
            path,
            $"temporary raw log '{path}'",
            WriteDiagnostic);
    }

    private void CleanupPlanArtifacts(EncodingExecutionPlan? plan)
    {
        if (plan?.CleanupPaths is null)
        {
            return;
        }

        foreach (var path in plan.CleanupPaths.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"Failed to delete cleanup path '{path}'. {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void CleanupPartialOutputFile(EncodingJobRequest request, EncodingJobState state)
    {
        if (state == EncodingJobState.Completed)
        {
            return;
        }

        BestEffortCleanup.DeleteFileIfZeroLength(
            request.OutputPath,
            $"partial output '{request.OutputPath}'",
            WriteDiagnostic);
    }

    private string BuildMultipassStatsPath(EncodingJobRequest request, EncoderKind kind)
    {
        var directory = Path.Combine(GetJobTempDirectory(request), "multipass");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{request.JobId:N}_{kind.ToShortName()}_stats.log");
    }

    private static string GetJobTempDirectory(EncodingJobRequest request)
    {
        var outputDirectory = Path.GetDirectoryName(request.OutputPath);
        var baseDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Environment.CurrentDirectory
            : outputDirectory;
        return Path.Combine(baseDirectory, TempWorkspaceFolderName, request.JobId.ToString("N"));
    }

    private void CleanupJobTempDirectory(EncodingJobRequest request)
    {
        var jobTempDirectory = GetJobTempDirectory(request);
        BestEffortCleanup.DeleteDirectoryRecursively(
            jobTempDirectory,
            $"job temp directory '{jobTempDirectory}'",
            WriteDiagnostic);

        var rootDirectory = Path.GetDirectoryName(jobTempDirectory);
        BestEffortCleanup.DeleteDirectoryIfEmpty(rootDirectory, WriteDiagnostic);
    }

    private void WriteDiagnostic(string message)
    {
        AppDiagnosticsLog.Write(_appPaths, nameof(LocalEncodingJobRunner), message);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? "encoding-job" : sanitized;
    }

    private static string Optional(string name, string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : $"{name} {value}";
    }

    private static string QuoteIfPresent(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : Quote(value);
    }

    private static InputPipelineKind ResolvePipelineKind(EncodingJobRequest request)
    {
        if (request.PipelineKind != InputPipelineKind.Auto)
        {
            return request.PipelineKind;
        }

        return InputSourceSupport.ResolvePipelineKind(request.SourcePath);
    }

    private static string X264InputSwitch(InputPipelineKind pipelineKind)
    {
        return pipelineKind switch
        {
            InputPipelineKind.Y4mFile => "--demuxer y4m",
            InputPipelineKind.RawYuvFile => "--demuxer raw",
            _ => "--demuxer y4m --stdin y4m"
        };
    }

    private static string BuildX264DirectInputArguments(string sourcePath, InputPipelineKind pipelineKind)
    {
        return pipelineKind switch
        {
            InputPipelineKind.Y4mFile => $"{X264InputSwitch(pipelineKind)} {Quote(sourcePath)}",
            InputPipelineKind.RawYuvFile => $"{X264InputSwitch(pipelineKind)} {Quote(sourcePath)}",
            _ => $"{X264InputSwitch(pipelineKind)} -"
        };
    }

    private static string BuildX265DirectInputArguments(string sourcePath, InputPipelineKind pipelineKind)
    {
        return pipelineKind switch
        {
            InputPipelineKind.Y4mFile => $"--y4m --input {Quote(sourcePath)}",
            InputPipelineKind.RawYuvFile => $"--input {Quote(sourcePath)}",
            _ => "--y4m --input -"
        };
    }

    private static string BuildSvtDirectInputArguments(string sourcePath, InputPipelineKind pipelineKind)
    {
        return pipelineKind switch
        {
            InputPipelineKind.Y4mFile => $"--input {Quote(sourcePath)}",
            InputPipelineKind.RawYuvFile => $"--input {Quote(sourcePath)}",
            _ => "--input -"
        };
    }

    private static string OptionalSegment(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string Quote(string value)
    {
        return $"\"{value}\"";
    }

    private static string JoinArguments(params string[] parts)
    {
        return string.Join(" ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.0##", CultureInfo.InvariantCulture);
    }

    private AppLanguage GetLanguage() => _settingsService.Load().Language;

    private static string T(AppLanguage language, string en, string zh) =>
        language == AppLanguage.English ? en : zh;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private sealed record ParsedProgressSnapshot(
        double? ProgressFraction,
        EncodingProgressSnapshot? Snapshot);

    private sealed record ProgressDispatchState(
        DateTimeOffset LastReportedAt,
        double? LastProgressFraction,
        long? LastCurrentFrame,
        string LastReportedLine);
}
