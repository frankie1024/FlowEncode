using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class LocalEncodingJobRunner : IEncodingJobRunner
{
    private const string TempWorkspaceFolderName = ".flowencode-temp";
    private static readonly TimeSpan TransientProgressReportInterval = TimeSpan.FromMilliseconds(125);
    private readonly ExternalToolLocator _toolLocator;
    private readonly EncodingCommandBuilder _commandBuilder;
    private readonly EncodingJobLogWriter _logWriter;
    private readonly SourceVideoInfoProbe _sourceInfoProbe;
    private readonly ParallelVideoEncodingAv1anRunner _parallelVideoAv1anRunner;
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
        _commandBuilder = new EncodingCommandBuilder(_toolLocator);
        _logWriter = new EncodingJobLogWriter(paths, WriteDiagnostic);
        _sourceInfoProbe = new SourceVideoInfoProbe(_toolLocator);
        _parallelVideoAv1anRunner = new ParallelVideoEncodingAv1anRunner(paths, settingsService);
        _discoveryService = discoveryService;
        _settingsService = settingsService;
    }

    public string BuildDisplayCommand(EncodingJobRequest request)
    {
        if (request.UseAv1anParallelVideoEncoding)
        {
            return _parallelVideoAv1anRunner.BuildDisplayCommand(request);
        }

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

        _parallelVideoAv1anRunner.Abort(jobId);
    }

    public async Task<EncodingJobResult> RunAsync(
        EncodingJobRequest request,
        IProgress<EncodingJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.UseAv1anParallelVideoEncoding)
        {
            return await _parallelVideoAv1anRunner.RunAsync(request, progress, cancellationToken);
        }

        var language = GetLanguage();
        var encoderPath = ResolveEncoderPath(request);
        var stagedOutputPath = CreateStagedOutputPath(request);
        var visibleLogBuilder = new StringBuilder();
        var currentState = EncodingJobState.Running;
        var progressDispatchState = new ProgressDispatchState(DateTimeOffset.UtcNow, 0.0, 0, string.Empty);
        var pipelineKind = ResolvePipelineKind(request);
        var outputDirectory = Path.GetDirectoryName(stagedOutputPath);
        var rawLogPath = CreateTemporaryRawLogPath(request);
        var lineGate = new object();
        var rawLogWriter = EncodingJobLogWriter.CreateRawLogWriter(rawLogPath);
        var rawLogWriterClosed = false;
        Task pumpOutput = Task.CompletedTask;
        Task pumpError = Task.CompletedTask;
        Task pumpSourceError = Task.CompletedTask;
        Task copySourceToEncoder = Task.CompletedTask;
        Process? activeProcess = null;
        Process? activeSourceProcess = null;
        ManagedProcessExecution? activeExecution = null;
        EncodingExecutionPlan? plan = null;
        var displayCommand = string.Empty;

        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        void ReportSourceProbeProgress(string line)
        {
            var parsedLine = VideoEncodingOutputBridge.ParseSourcePreparationLine(line);
            if (parsedLine is null)
            {
                return;
            }

            lock (lineGate)
            {
                rawLogWriter.WriteLine(parsedLine.DisplayLine);
                if (parsedLine.ShouldShowInLog)
                {
                    visibleLogBuilder.AppendLine(parsedLine.DisplayLine);
                    EncodingJobLogWriter.TrimVisibleLogIfNeeded(visibleLogBuilder);
                }
            }

            progress?.Report(new EncodingJobProgress(
                request.JobId,
                currentState,
                parsedLine.ProgressPercent.HasValue
                    ? Math.Clamp(parsedLine.ProgressPercent.Value / 100.0, 0.0, 1.0)
                    : null,
                BuildSourceProbeSummary(language, parsedLine.ProgressPercent),
                parsedLine.DisplayLine,
                Snapshot: null,
                IsSourcePreparation: true,
                ShouldShowInLog: parsedLine.ShouldShowInLog));
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
                    IsSourcePreparation: true,
                    ShouldShowInLog: false));
            }

            plan = BuildPlan(
                request,
                encoderPath,
                includeSourceMetadata: true,
                pipelineKind,
                pipelineKind == InputPipelineKind.VapourSynth ? ReportSourceProbeProgress : null,
                cancellationToken,
                outputPathOverride: stagedOutputPath);
            displayCommand = ResolveDisplayCommand(plan.DisplayCommand, stagedOutputPath, request.OutputPath);

            progress?.Report(new EncodingJobProgress(
                request.JobId,
                EncodingJobState.Running,
                0.0,
                BuildStageStartingSummary(language, plan.Steps[0]),
                displayCommand,
                BuildInitialSnapshot(plan)));

            foreach (var step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EncodingJobLogWriter.AppendStageHeader(
                    step with { DisplayCommand = ResolveDisplayCommand(step.DisplayCommand, stagedOutputPath, request.OutputPath) },
                    rawLogWriter,
                    visibleLogBuilder);
                progress?.Report(new EncodingJobProgress(
                    request.JobId,
                    EncodingJobState.Running,
                    BuildStageStartingProgress(step),
                    BuildStageStartingSummary(language, step),
                    BuildStageStartingDetail(language, step),
                    BuildStageStartingSnapshot(plan, step)));
                progressDispatchState = new ProgressDispatchState(
                    DateTimeOffset.UtcNow,
                    BuildStageStartingProgress(step),
                    0,
                    string.Empty);

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
                    var parsedLine = VideoEncodingOutputBridge.ParseEncodingLine(
                        plan.Kind,
                        plan.TotalFrames,
                        plan.SourceFramesPerSecond,
                        line);
                    if (parsedLine is null)
                    {
                        return;
                    }

                    EncodingJobProgress? pendingProgress = null;

                    lock (lineGate)
                    {
                        rawLogWriter.WriteLine(parsedLine.NormalizedLine);

                        if (parsedLine.ShouldShowInLog)
                        {
                            visibleLogBuilder.AppendLine(parsedLine.NormalizedLine);
                            EncodingJobLogWriter.TrimVisibleLogIfNeeded(visibleLogBuilder);
                        }

                        var progressSnapshot = parsedLine.ParseResult;
                        var stageAwareProgress = ApplyStageProgress(progressSnapshot, step);
                        var reportedProgressFraction = ResolveReportedProgressFraction(stageAwareProgress, progressDispatchState);
                        if (sourceProcess is not null
                            && !sourceProcess.HasExited
                            && stageAwareProgress?.ProgressFraction is null
                            && !parsedLine.ShouldSurfaceDuringSourcePreparation)
                        {
                            return;
                        }

                        if (!ShouldReportProgress(parsedLine.IsTransient, parsedLine.NormalizedLine, stageAwareProgress, ref progressDispatchState))
                        {
                            return;
                        }

                        pendingProgress = new EncodingJobProgress(
                            request.JobId,
                            currentState,
                            reportedProgressFraction,
                            BuildRunningSummary(language, step, reportedProgressFraction),
                            parsedLine.NormalizedLine,
                            stageAwareProgress?.Snapshot,
                            ShouldShowInLog: parsedLine.ShouldShowInLog);
                    }

                    if (pendingProgress is not null)
                    {
                        progress?.Report(pendingProgress);
                    }
                }

                void HandleSourceLine(string line)
                {
                    var parsedLine = VideoEncodingOutputBridge.ParseSourcePreparationLine(line);
                    if (parsedLine is null)
                    {
                        return;
                    }

                    EncodingJobProgress? pendingProgress = null;

                    lock (lineGate)
                    {
                        rawLogWriter.WriteLine(parsedLine.DisplayLine);
                        if (parsedLine.ShouldShowInLog)
                        {
                            visibleLogBuilder.AppendLine(parsedLine.DisplayLine);
                            EncodingJobLogWriter.TrimVisibleLogIfNeeded(visibleLogBuilder);
                        }

                        pendingProgress = new EncodingJobProgress(
                            request.JobId,
                            currentState,
                            parsedLine.ProgressPercent.HasValue
                                ? Math.Clamp(parsedLine.ProgressPercent.Value / 100.0, 0.0, 1.0)
                                : null,
                            BuildSourceRunningSummary(language, step, parsedLine.ProgressPercent),
                            parsedLine.DisplayLine,
                            Snapshot: null,
                            IsSourcePreparation: true,
                            ShouldShowInLog: parsedLine.ShouldShowInLog);
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
                        IsSourcePreparation: true,
                        ShouldShowInLog: false));

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
                    var failedSidecarLogPath = await _logWriter.WriteSidecarLogAsync(request, displayCommand, currentState, effectiveExitCode, rawLogPath);

                    progress?.Report(new EncodingJobProgress(
                        request.JobId,
                        currentState,
                        BuildStageFailureProgress(step),
                        failedSummary,
                        EncodingJobLogWriter.LastMeaningfulLine(failedVisibleLog)));

                    return new EncodingJobResult(
                        request.JobId,
                        currentState,
                        effectiveExitCode,
                        failedSummary,
                        failedVisibleLog,
                        failedSidecarLogPath);
                }
            }

            FinalizeOutputFile(stagedOutputPath, request.OutputPath, request.JobId);
            currentState = EncodingJobState.Completed;
            var summary = T(language, "Encoding completed", "编码完成");
            var visibleLog = visibleLogBuilder.ToString();
            await CloseRawLogWriterAsync();
            var sidecarLogPath = await _logWriter.WriteSidecarLogAsync(request, displayCommand, currentState, 0, rawLogPath);

            progress?.Report(new EncodingJobProgress(
                request.JobId,
                currentState,
                1.0,
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
            var cancelledLogPath = await _logWriter.WriteSidecarLogAsync(request, displayCommand, currentState, -1, rawLogPath);
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
            CleanupStagedOutput(stagedOutputPath, request.OutputPath, request.JobId);

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
        bool allowCachedSourceInfo = false,
        string? outputPathOverride = null)
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
        var statsPath = profile.RateControl == RateControlMode.TwoPass
            ? BuildMultipassStatsPath(request, profile.Kind)
            : null;
        return _commandBuilder.BuildPlan(request, encoderPath, pipelineKind, sourceInfo, statsPath, outputPathOverride);
    }

    private static EncodingProgressSnapshot? BuildInitialSnapshot(EncodingExecutionPlan plan)
    {
        if (plan.TotalFrames is null)
        {
            return null;
        }

        return new EncodingProgressSnapshot(0, plan.TotalFrames, null, null, null, null);
    }

    private static Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        return ProcessOutputPump.PumpLinesAsync(reader, onLine, cancellationToken);
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

    private static bool ShouldReportProgress(
        bool isTransient,
        string line,
        EncodingProgressParseResult? progressSnapshot,
        ref ProgressDispatchState state)
    {
        var now = DateTimeOffset.UtcNow;
        var currentProgressFraction = progressSnapshot?.ProgressFraction;
        var currentFrame = progressSnapshot?.Snapshot?.CurrentFrame;

        if (!isTransient)
        {
            state = CreateUpdatedDispatchState(now, currentProgressFraction, currentFrame, line, state);
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

            state = CreateUpdatedDispatchState(now, state.LastProgressFraction, state.LastCurrentFrame, line, state);
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

        state = CreateUpdatedDispatchState(now, currentProgressFraction, currentFrame, line, state);
        return true;
    }

    private static ProgressDispatchState CreateUpdatedDispatchState(
        DateTimeOffset at,
        double? currentProgressFraction,
        long? currentFrame,
        string line,
        ProgressDispatchState previous)
    {
        return new ProgressDispatchState(
            at,
            ResolveReportedProgressFraction(currentProgressFraction, previous.LastProgressFraction),
            currentFrame ?? previous.LastCurrentFrame,
            line);
    }

    private static double? ResolveReportedProgressFraction(
        EncodingProgressParseResult? progressSnapshot,
        ProgressDispatchState state)
    {
        return ResolveReportedProgressFraction(progressSnapshot?.ProgressFraction, state.LastProgressFraction);
    }

    private static double? ResolveReportedProgressFraction(
        double? currentProgressFraction,
        double? lastProgressFraction)
    {
        return currentProgressFraction ?? lastProgressFraction;
    }

    private static bool ShouldSurfaceLineDuringSourcePreparation(string line)
    {
        return VideoEncodingOutputBridge.ShouldSurfaceLineDuringSourcePreparation(line);
    }

    private static bool ShouldAppendSourcePreparationVisibleLogLine(string line)
    {
        return ShouldSurfaceLineDuringSourcePreparation(line);
    }

    internal static bool ShouldSurfaceLineDuringSourcePreparationForTesting(string line)
        => ShouldSurfaceLineDuringSourcePreparation(EncoderConsoleLineNormalizer.Normalize(line));

    internal static bool ShouldAppendSourcePreparationVisibleLogLineForTesting(string line)
        => ShouldAppendSourcePreparationVisibleLogLine(EncoderConsoleLineNormalizer.Normalize(line));

    internal static double? ResolveReportedProgressFractionForTesting(
        double? currentProgressFraction,
        double? lastProgressFraction)
        => ResolveReportedProgressFraction(currentProgressFraction, lastProgressFraction);

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

    private static EncodingProgressParseResult? ApplyStageProgress(EncodingProgressParseResult? progressSnapshot, EncodingExecutionStep step)
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

    private string CreateTemporaryRawLogPath(EncodingJobRequest request)
    {
        var directory = Path.Combine(GetJobTempDirectory(request), "logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{request.JobId:N}.raw.log");
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

    private static string CreateStagedOutputPath(EncodingJobRequest request)
    {
        return ExecutionOutputStaging.CreateStagedFilePath(GetJobTempDirectory(request), request.OutputPath, request.JobId);
    }

    private void FinalizeOutputFile(string stagedOutputPath, string finalOutputPath, Guid jobId)
    {
        try
        {
            ExecutionOutputStaging.FinalizeFile(stagedOutputPath, finalOutputPath, jobId);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(T(
                GetLanguage(),
                $"Encoding completed but the output file could not be finalized: {finalOutputPath}",
                $"编码已完成，但无法完成输出文件落盘：{finalOutputPath}"), ex);
        }
    }

    private void CleanupStagedOutput(string stagedOutputPath, string finalOutputPath, Guid jobId)
    {
        ExecutionOutputStaging.CleanupStagedFile(
            stagedOutputPath,
            finalOutputPath,
            jobId,
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

    private static string ResolveDisplayCommand(string command, string stagedOutputPath, string finalOutputPath)
    {
        return string.IsNullOrWhiteSpace(command)
            ? string.Empty
            : command.Replace(stagedOutputPath, finalOutputPath, StringComparison.Ordinal);
    }

    private void WriteDiagnostic(string message)
    {
        AppDiagnosticsLog.Write(_appPaths, nameof(LocalEncodingJobRunner), message);
    }

    private static InputPipelineKind ResolvePipelineKind(EncodingJobRequest request)
    {
        if (request.PipelineKind != InputPipelineKind.Auto)
        {
            return request.PipelineKind;
        }

        return InputSourceSupport.ResolvePipelineKind(request.SourcePath);
    }

    private AppLanguage GetLanguage() => _settingsService.Load().Language;

    private static string T(AppLanguage language, string en, string zh) =>
        language == AppLanguage.English ? en : zh;

    private sealed record ProgressDispatchState(
        DateTimeOffset LastReportedAt,
        double? LastProgressFraction,
        long? LastCurrentFrame,
        string LastReportedLine);
}
