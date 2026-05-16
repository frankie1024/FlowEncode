using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class CliAudioProcessingRunner : IAudioProcessingRunner
{
    private static readonly Regex DeewStageProgressRegex = new(@"Stage progress:\s*(?<value>\d{1,3}(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeewDisplayProgressRegex = new(@"\[\s*(?<stage>DEE\s*:[^\]]+)\]\s*.*?(?<value>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Eac3ToProcessProgressRegex = new(@"^\s*process:\s*(?<value>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Eac3ToAdditionalPassNeededRegex = new(@"(?<pass>\d+)(?:st|nd|rd|th)\s+pass\s+will\s+be\s+necessary", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Eac3ToStartingPassRegex = new(@"Starting\s+(?<pass>\d+)(?:st|nd|rd|th)\s+pass", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FfmpegOutTimeRegex = new(@"^\s*out_time=(?<value>\d{1,}:\d{2}:\d{2}\.\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FfmpegOutTimeMsRegex = new(@"^\s*out_time_ms=(?<value>\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FfmpegOutTimeUsRegex = new(@"^\s*out_time_us=(?<value>\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FfmpegSpeedRegex = new(@"^\s*speed=(?<value>\d{1,3}(?:\.\d+)?)x\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ProgressFilePollInterval = TimeSpan.FromMilliseconds(200);
    private const double SignificantProgressDelta = 0.01;
    private readonly IToolProbeService _toolProbeService;
    private readonly IAppSettingsService _settingsService;
    private readonly ConcurrentDictionary<Guid, ManagedProcessExecution> _activeExecutions = new();

    public CliAudioProcessingRunner(IToolProbeService toolProbeService, IAppSettingsService settingsService)
    {
        _toolProbeService = toolProbeService;
        _settingsService = settingsService;
    }

    public string BuildDisplayCommand(AudioProcessingRequest request)
    {
        return request.Mode switch
        {
            AudioProcessingMode.Eac3To => $"eac3to.exe {BuildEac3ToDisplayArguments(request)}",
            AudioProcessingMode.Ddp => $"deew.exe {BuildDdpDisplayArguments(request.SourcePath, request.OutputPath)}",
            AudioProcessingMode.Opus => BuildOpusCommand(request, "ffmpeg.exe", "opusenc.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Mode, null)
        };
    }

    public void Abort(Guid jobId)
    {
        if (_activeExecutions.TryRemove(jobId, out var execution))
        {
            execution.Terminate();
        }
    }

    public async Task<AudioProcessingResult> RunAsync(
        AudioProcessingRequest request,
        IProgress<AudioProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var language = GetLanguage();
        if (!File.Exists(request.SourcePath))
        {
            throw new FileNotFoundException(T(language, "Audio source file was not found.", "未找到音频输入文件。"), request.SourcePath);
        }

        return request.Mode switch
        {
            AudioProcessingMode.Eac3To => await RunEac3ToAsync(request, progress, cancellationToken),
            AudioProcessingMode.Ddp => await RunDdpAsync(request, progress, cancellationToken),
            AudioProcessingMode.Opus => await RunOpusAsync(request, progress, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Mode, null)
        };
    }

    private async Task<AudioProcessingResult> RunEac3ToAsync(
        AudioProcessingRequest request,
        IProgress<AudioProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var eac3toPath = await ResolveToolPathAsync(RegisteredToolKind.Eac3To, cancellationToken);
        var arguments = BuildEac3ToArgumentParts(request);
        var command = $"{Quote(eac3toPath)} {CommandLineDisplay.JoinArguments(arguments)}";
        var startInfo = new ProcessStartInfo
        {
            FileName = eac3toPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunProcessAsync(
            request,
            progress,
            command,
            startInfo,
            cancellationToken);
    }

    private async Task<AudioProcessingResult> RunDdpAsync(
        AudioProcessingRequest request,
        IProgress<AudioProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var deewPath = await ResolveToolPathAsync(RegisteredToolKind.Deew, cancellationToken);
        var deePath = await ResolveToolPathAsync(RegisteredToolKind.Dee, cancellationToken);
        var ffmpegPath = await ResolveToolPathAsync(RegisteredToolKind.Ffmpeg, cancellationToken);
        var ffprobePath = await ResolveToolPathAsync(RegisteredToolKind.Ffprobe, cancellationToken);

        var outputDirectory = string.IsNullOrWhiteSpace(request.OutputPath)
            ? Environment.CurrentDirectory
            : request.OutputPath;
        Directory.CreateDirectory(outputDirectory);

        var command = $"{Quote(deewPath)} {BuildDdpDisplayArguments(request.SourcePath, outputDirectory)}";
        var startInfo = new ProcessStartInfo
        {
            FileName = deewPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(request.SourcePath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("-np");

        PrepareDeewEnvironment(startInfo, deewPath, deePath, ffmpegPath, ffprobePath);
        return await RunProcessAsync(request, progress, command, startInfo, cancellationToken);
    }

    private async Task<AudioProcessingResult> RunOpusAsync(
        AudioProcessingRequest request,
        IProgress<AudioProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = await ResolveToolPathAsync(RegisteredToolKind.Ffmpeg, cancellationToken);
        request = await ResolveOpusDurationFallbackAsync(request, cancellationToken);
        var runPlan = CreateRunPlan(request);
        var progressFilePath = Path.Combine(
            Path.GetTempPath(),
            $"flowencode_opus_progress_{request.JobId:N}.log");

        try
        {
            if (ShouldUseFfmpegLibOpusMappingFamily1(request))
            {
                var displayArguments = BuildFfmpegLibOpusDisplayArguments(request);
                var runArguments = BuildFfmpegLibOpusArgumentParts(runPlan.ExecutionRequest, progressFilePath);
                var command = $"{Quote(ffmpegPath)} {displayArguments}";
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true
                };
                foreach (var argument in runArguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                return await RunProcessAsync(
                    runPlan,
                    progress,
                    command,
                    startInfo,
                    cancellationToken,
                    progressFilePath);
            }

            var opusEncoderPath = await ResolveToolPathAsync(RegisteredToolKind.OpusExt, cancellationToken);

            var displayCommand = BuildOpusPipelineCommand(request, ffmpegPath, opusEncoderPath);
            var startInfos = CreateOpusPipelineStartInfos(runPlan.ExecutionRequest, ffmpegPath, opusEncoderPath, progressFilePath);

            return await RunOpusPipelineAsync(
                runPlan,
                progress,
                displayCommand,
                startInfos,
                cancellationToken,
                progressFilePath);
        }
        finally
        {
            BestEffortCleanup.DeleteFile(progressFilePath, $"Opus progress file '{progressFilePath}'");
        }
    }

    private async Task<AudioProcessingRequest> ResolveOpusDurationFallbackAsync(
        AudioProcessingRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SourceDurationSeconds is > 0)
        {
            return request;
        }

        try
        {
            var sourceInfoService = new FfprobeAudioSourceInfoService(_toolProbeService);
            var sourceInfo = await sourceInfoService.ProbeAsync(request.SourcePath, cancellationToken);
            return sourceInfo?.DurationSeconds is > 0
                ? request with { SourceDurationSeconds = sourceInfo.DurationSeconds }
                : request;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return request;
        }
    }

    private async Task<AudioProcessingResult> RunProcessAsync(
        AudioProcessingRequest request,
        IProgress<AudioProcessingProgress>? progress,
        string displayCommand,
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        string? supplementalProgressFilePath = null)
    {
        return await RunProcessAsync(
            new AudioProcessingRunPlan(request, request, null),
            progress,
            displayCommand,
            startInfo,
            cancellationToken,
            supplementalProgressFilePath);
    }

    private async Task<AudioProcessingResult> RunProcessAsync(
        AudioProcessingRunPlan runPlan,
        IProgress<AudioProcessingProgress>? progress,
        string displayCommand,
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        string? supplementalProgressFilePath = null)
    {
        return await RunProcessWithProgressAsync(
            runPlan,
            progress,
            displayCommand,
            (handleLine, token) => ExecuteSingleProcessAsync(
                runPlan.ExecutionRequest,
                startInfo,
                handleLine,
                token,
                supplementalProgressFilePath),
            cancellationToken);
    }

    private async Task<AudioProcessingResult> RunProcessWithProgressAsync(
        AudioProcessingRunPlan runPlan,
        IProgress<AudioProcessingProgress>? progress,
        string displayCommand,
        Func<Action<string>, CancellationToken, Task<ProcessExecutionResult>> executeAsync,
        CancellationToken cancellationToken)
    {
        var language = GetLanguage();
        var request = runPlan.DisplayRequest;
        var outputDirectory = Path.GetDirectoryName(runPlan.ExecutionRequest.OutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var logBuilder = new StringBuilder();
        var gate = new object();
        var lastProgress = 0.0;
        var hasKnownProgress = false;
        var lastReportAt = DateTimeOffset.MinValue;
        var lastReportedProgress = 0.0;
        var hasReportedProgress = false;
        var lastReportedLine = string.Empty;
        var lastReportedDetailLine = string.Empty;
        var deewPhase = request.Mode == AudioProcessingMode.Ddp
            ? DeewProgressPhase.FfmpegPreparation
            : DeewProgressPhase.None;
        AudioProcessingTelemetry? lastReportedTelemetry = null;
        var lastReportedPhaseLabel = string.Empty;
        var lastLoggedLine = string.Empty;
        var eac3ToProgressState = request.Mode == AudioProcessingMode.Eac3To
            ? new Eac3ToProgressState()
            : null;
        var opusTelemetryState = request.Mode == AudioProcessingMode.Opus
            ? new OpusTelemetryState(request.SourceDurationSeconds, request.OpusBitrateKbps, request.OutputPath)
            : null;

        void HandleLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            AudioProcessingProgress? update = null;

            lock (gate)
            {
                var now = DateTimeOffset.UtcNow;
                var rawLine = ConsoleOutputLineNormalizer.Normalize(line);
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    return;
                }

                deewPhase = UpdateDeewPhase(request.Mode, deewPhase, rawLine);
                eac3ToProgressState?.Update(rawLine);
                opusTelemetryState?.Update(rawLine);
                var parsedProgress = ParseProgress(request, rawLine);
                if (parsedProgress.HasValue)
                {
                    if (eac3ToProgressState is not null)
                    {
                        parsedProgress = eac3ToProgressState.Normalize(parsedProgress.Value);
                    }

                    hasKnownProgress = true;
                    var normalizedRunningProgress = NormalizeRunningProgress(parsedProgress.Value);
                    lastProgress = request.Mode == AudioProcessingMode.Eac3To
                        && eac3ToProgressState is not null
                        && eac3ToProgressState.TotalPasses > 1
                        ? Math.Clamp(normalizedRunningProgress, 0.0, 1.0)
                        : Math.Clamp(Math.Max(lastProgress, normalizedRunningProgress), 0.0, 1.0);
                }

                var phaseLabel = eac3ToProgressState?.PhaseLabel ?? string.Empty;
                var telemetry = opusTelemetryState?.Build(hasKnownProgress ? lastProgress : null);
                var detailLine = NormalizeDetailLine(request, rawLine, parsedProgress, deewPhase);
                line = NormalizeDisplayLine(request, rawLine, parsedProgress, deewPhase);
                var telemetryChanged = !EqualityComparer<AudioProcessingTelemetry?>.Default.Equals(telemetry, lastReportedTelemetry);
                var phaseChanged = !string.Equals(phaseLabel, lastReportedPhaseLabel, StringComparison.Ordinal);
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (request.Mode == AudioProcessingMode.Opus
                        && telemetryChanged
                        && now - lastReportAt >= ProgressReportInterval)
                    {
                        lastReportAt = now;
                        if (hasKnownProgress)
                        {
                            hasReportedProgress = true;
                            lastReportedProgress = lastProgress;
                        }

                        lastReportedTelemetry = telemetry;
                        update = new AudioProcessingProgress(
                            request.JobId,
                            EncodingJobState.Running,
                            hasKnownProgress ? lastProgress : null,
                            BuildRunningSummary(language, request.Mode, lastProgress, hasKnownProgress, lastReportedLine, phaseLabel, deewPhase),
                            lastReportedDetailLine,
                            telemetry,
                            phaseLabel);
                    }
                    else if (request.Mode == AudioProcessingMode.Eac3To
                        && phaseChanged
                        && now - lastReportAt >= ProgressReportInterval)
                    {
                        lastReportAt = now;
                        if (hasKnownProgress)
                        {
                            hasReportedProgress = true;
                            lastReportedProgress = lastProgress;
                        }

                        lastReportedPhaseLabel = phaseLabel;
                        update = new AudioProcessingProgress(
                            request.JobId,
                            EncodingJobState.Running,
                            hasKnownProgress ? lastProgress : null,
                            BuildRunningSummary(language, request.Mode, lastProgress, hasKnownProgress, lastReportedLine, phaseLabel, deewPhase),
                            lastReportedDetailLine,
                            null,
                            phaseLabel);
                    }

                    return;
                }

                var reachedReportWindow = now - lastReportAt >= ProgressReportInterval;
                var progressAdvancedEnough = hasKnownProgress
                    && (!hasReportedProgress || lastProgress - lastReportedProgress >= SignificantProgressDelta);
                var lineChanged = !string.Equals(line, lastReportedLine, StringComparison.Ordinal);
                var detailLineChanged = !string.Equals(detailLine, lastReportedDetailLine, StringComparison.Ordinal);
                var immediateCliLine = ShouldReportImmediateCliLine(request.Mode, detailLine, detailLineChanged);
                var shouldReport = request.Mode == AudioProcessingMode.Ddp
                    ? ShouldReportDdpLine(detailLine, parsedProgress, reachedReportWindow, progressAdvancedEnough, detailLineChanged)
                    : immediateCliLine
                        || ShouldReportGenericLine(reachedReportWindow, progressAdvancedEnough, lineChanged)
                        || (request.Mode == AudioProcessingMode.Opus && reachedReportWindow && (telemetryChanged || parsedProgress.HasValue))
                        || (request.Mode == AudioProcessingMode.Eac3To && phaseChanged && reachedReportWindow);

                if (ShouldAppendLogLine(request.Mode, detailLine, parsedProgress, ref lastLoggedLine))
                {
                    logBuilder.AppendLine(detailLine);
                }

                if (!shouldReport)
                {
                    return;
                }

                lastReportAt = now;
                if (hasKnownProgress)
                {
                    hasReportedProgress = true;
                    lastReportedProgress = lastProgress;
                }

                lastReportedLine = line;
                lastReportedDetailLine = detailLine;
                lastReportedTelemetry = telemetry;
                lastReportedPhaseLabel = phaseLabel;
                update = new AudioProcessingProgress(
                    request.JobId,
                    EncodingJobState.Running,
                    hasKnownProgress ? lastProgress : null,
                    BuildRunningSummary(language, request.Mode, lastProgress, hasKnownProgress, line, phaseLabel, deewPhase),
                    detailLine,
                    telemetry,
                    phaseLabel);
            }

            if (update is not null)
            {
                progress?.Report(update);
            }
        }

        progress?.Report(new AudioProcessingProgress(
            request.JobId,
            EncodingJobState.Running,
            null,
            BuildStartingSummary(language, request.Mode),
            BuildStartingDetail(language, request.Mode),
            null,
            eac3ToProgressState?.PhaseLabel));

        try
        {
            var executionResult = await executeAsync(HandleLine, cancellationToken);
            var log = logBuilder.ToString();
            var semanticFailureDetail = TryBuildSemanticFailureDetail(request.Mode, log);
            if (executionResult.ExitCode == 0 && string.IsNullOrWhiteSpace(semanticFailureDetail))
            {
                FinalizeOutputFileForSuccess(runPlan);
                DeletePartialOutputFile(runPlan);

                progress?.Report(new AudioProcessingProgress(
                    request.JobId,
                    EncodingJobState.Completed,
                    1.0,
                    BuildCompletedSummary(language, request.Mode),
                    LastMeaningfulLine(log),
                    null,
                    eac3ToProgressState?.PhaseLabel));

                return new AudioProcessingResult(
                    request.JobId,
                    EncodingJobState.Completed,
                    0,
                    BuildCompletedSummary(language, request.Mode),
                    log,
                    displayCommand);
            }

            DeletePartialOutputFile(runPlan);

            var effectiveExitCode = executionResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(semanticFailureDetail)
                ? -2
                : executionResult.ExitCode;
            var failureDetail = string.IsNullOrWhiteSpace(semanticFailureDetail)
                ? executionResult.ExitCodeDetail
                : semanticFailureDetail;
            var failureSummary = string.IsNullOrWhiteSpace(failureDetail)
                ? T(language, $"{BuildFailureSummary(language, request.Mode)} (exit code {executionResult.ExitCode})", $"{BuildFailureSummary(language, request.Mode)}，退出代码 {executionResult.ExitCode}")
                : T(language, $"{BuildFailureSummary(language, request.Mode)}: {failureDetail}", $"{BuildFailureSummary(language, request.Mode)}，{failureDetail}");
            progress?.Report(new AudioProcessingProgress(
                request.JobId,
                EncodingJobState.Failed,
                null,
                failureSummary,
                LastMeaningfulLine(log),
                null,
                eac3ToProgressState?.PhaseLabel));

            return new AudioProcessingResult(
                request.JobId,
                EncodingJobState.Failed,
                effectiveExitCode,
                failureSummary,
                log,
                displayCommand);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeletePartialOutputFile(runPlan);

            return new AudioProcessingResult(
                request.JobId,
                EncodingJobState.Cancelled,
                -1,
                BuildCancelledSummary(language, request.Mode),
                logBuilder.ToString(),
                displayCommand);
        }
        catch
        {
            DeletePartialOutputFile(runPlan);
            throw;
        }
    }

    private async Task<ProcessExecutionResult> ExecuteSingleProcessAsync(
        AudioProcessingRequest request,
        ProcessStartInfo startInfo,
        Action<string> handleLine,
        CancellationToken cancellationToken,
        string? supplementalProgressFilePath)
    {
        Process? process = null;
        ManagedProcessExecution? activeExecution = null;
        Task pumpOutput = Task.CompletedTask;
        Task pumpError = Task.CompletedTask;
        Task pumpSupplementalProgress = Task.CompletedTask;

        try
        {
            process = new Process { StartInfo = startInfo };
            process.Start();
            activeExecution = new ManagedProcessExecution(process);
            _activeExecutions[request.JobId] = activeExecution;

            using var cancellationRegistration = cancellationToken.Register(static state =>
            {
                if (state is ManagedProcessExecution execution)
                {
                    execution.Terminate();
                }
            }, activeExecution);

            pumpOutput = PumpAsync(process.StandardOutput, handleLine, cancellationToken);
            pumpError = PumpAsync(process.StandardError, handleLine, cancellationToken);
            if (request.Mode == AudioProcessingMode.Opus
                && !string.IsNullOrWhiteSpace(supplementalProgressFilePath))
            {
                pumpSupplementalProgress = PumpProgressFileAsync(process, supplementalProgressFilePath, handleLine);
            }

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(pumpOutput, pumpError, pumpSupplementalProgress);

            return new ProcessExecutionResult(process.ExitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activeExecution?.Terminate();

            try
            {
                await Task.WhenAll(pumpOutput, pumpError, pumpSupplementalProgress);
            }
            catch (Exception ex)
            {
                WriteDebugDiagnostic(
                    $"Failed to drain audio process output after cancellation for job {request.JobId}",
                    ex);
            }

            throw;
        }
        catch
        {
            activeExecution?.Terminate();
            throw;
        }
        finally
        {
            _activeExecutions.TryRemove(request.JobId, out _);
            activeExecution?.Dispose();
        }
    }

    private async Task<AudioProcessingResult> RunOpusPipelineAsync(
        AudioProcessingRunPlan runPlan,
        IProgress<AudioProcessingProgress>? progress,
        string displayCommand,
        OpusPipelineStartInfos startInfos,
        CancellationToken cancellationToken,
        string? supplementalProgressFilePath)
    {
        return await RunProcessWithProgressAsync(
            runPlan,
            progress,
            displayCommand,
            (handleLine, token) => ExecuteOpusPipelineAsync(
                runPlan.ExecutionRequest,
                startInfos,
                handleLine,
                token,
                supplementalProgressFilePath),
            cancellationToken);
    }

    private async Task<ProcessExecutionResult> ExecuteOpusPipelineAsync(
        AudioProcessingRequest request,
        OpusPipelineStartInfos startInfos,
        Action<string> handleLine,
        CancellationToken cancellationToken,
        string? supplementalProgressFilePath)
    {
        Process? ffmpegProcess = null;
        Process? opusProcess = null;
        ManagedProcessExecution? activeExecution = null;
        Task copyPcmToOpus = Task.CompletedTask;
        Task pumpFfmpegError = Task.CompletedTask;
        Task pumpOpusOutput = Task.CompletedTask;
        Task pumpOpusError = Task.CompletedTask;
        Task pumpSupplementalProgress = Task.CompletedTask;

        try
        {
            ffmpegProcess = new Process { StartInfo = startInfos.FfmpegStartInfo };
            opusProcess = new Process { StartInfo = startInfos.OpusEncoderStartInfo };

            ffmpegProcess.Start();
            try
            {
                opusProcess.Start();
            }
            catch
            {
                TryTerminate(ffmpegProcess);
                throw;
            }

            activeExecution = new ManagedProcessExecution(ffmpegProcess, opusProcess);
            _activeExecutions[request.JobId] = activeExecution;

            using var cancellationRegistration = cancellationToken.Register(static state =>
            {
                if (state is ManagedProcessExecution execution)
                {
                    execution.Terminate();
                }
            }, activeExecution);

            copyPcmToOpus = CopyOpusPcmPipeAsync(
                ffmpegProcess.StandardOutput.BaseStream,
                opusProcess.StandardInput.BaseStream,
                cancellationToken);
            pumpFfmpegError = PumpAsync(ffmpegProcess.StandardError, handleLine, cancellationToken);
            pumpOpusOutput = PumpAsync(opusProcess.StandardOutput, handleLine, cancellationToken);
            pumpOpusError = PumpAsync(opusProcess.StandardError, handleLine, cancellationToken);
            if (!string.IsNullOrWhiteSpace(supplementalProgressFilePath))
            {
                pumpSupplementalProgress = PumpProgressFileAsync(ffmpegProcess, supplementalProgressFilePath, handleLine);
            }

            var ffmpegExitTask = ffmpegProcess.WaitForExitAsync(cancellationToken);
            var opusExitTask = opusProcess.WaitForExitAsync(cancellationToken);
            var pipeCopyTask = ProcessPipelineMonitor.ObservePipeCopyAsync(copyPcmToOpus);
            var ffmpegExitCodeShouldBeIgnored = false;
            var firstCompletion = await ProcessPipelineMonitor.WaitForFirstCompletionAsync(
                ffmpegExitTask,
                opusExitTask,
                pipeCopyTask);

            if (firstCompletion == PipelineFirstCompletion.ProducerExited)
            {
                var firstFfmpegExitCode = await GetExitCodeAsync(ffmpegProcess, ffmpegExitTask);
                if (firstFfmpegExitCode != 0)
                {
                    activeExecution.Terminate();
                }
            }
            else if (firstCompletion == PipelineFirstCompletion.ConsumerExited)
            {
                var opusExitCode = await GetExitCodeAsync(opusProcess, opusExitTask);
                if (opusExitCode != 0)
                {
                    activeExecution.Terminate();
                }
                else
                {
                    ffmpegExitCodeShouldBeIgnored = true;
                    TryTerminate(ffmpegProcess);
                }
            }
            else if (firstCompletion == PipelineFirstCompletion.PipeBroken)
            {
                ffmpegExitCodeShouldBeIgnored = true;
                TryTerminate(ffmpegProcess);
            }

            await Task.WhenAll(
                ffmpegExitTask,
                opusExitTask,
                pipeCopyTask);
            await Task.WhenAll(pumpFfmpegError, pumpOpusOutput, pumpOpusError, pumpSupplementalProgress);

            var ffmpegExitCode = ffmpegExitCodeShouldBeIgnored ? 0 : ffmpegProcess.ExitCode;
            return BuildOpusPipelineExecutionResult(ffmpegExitCode, opusProcess.ExitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activeExecution?.Terminate();

            try
            {
                await Task.WhenAll(
                    ProcessPipelineMonitor.ObservePipeCopyAsync(copyPcmToOpus),
                    pumpFfmpegError,
                    pumpOpusOutput,
                    pumpOpusError,
                    pumpSupplementalProgress);
            }
            catch (Exception ex)
            {
                WriteDebugDiagnostic(
                    $"Failed to drain Opus pipeline output after cancellation for job {request.JobId}",
                    ex);
            }

            throw;
        }
        catch
        {
            activeExecution?.Terminate();

            try
            {
                await Task.WhenAll(
                    ProcessPipelineMonitor.ObservePipeCopyAsync(copyPcmToOpus),
                    pumpFfmpegError,
                    pumpOpusOutput,
                    pumpOpusError,
                    pumpSupplementalProgress);
            }
            catch (Exception ex)
            {
                WriteDebugDiagnostic(
                    $"Failed to drain Opus pipeline output after failure for job {request.JobId}",
                    ex);
            }

            throw;
        }
        finally
        {
            _activeExecutions.TryRemove(request.JobId, out _);
            activeExecution?.Dispose();
        }
    }

    private static string BuildOpusCommand(AudioProcessingRequest request, string ffmpegExecutable, string opusEncoderExecutable)
    {
        return ShouldUseFfmpegLibOpusMappingFamily1(request)
            ? $"{Quote(ffmpegExecutable)} {BuildFfmpegLibOpusDisplayArguments(request)}"
            : BuildOpusPipelineCommand(request, ffmpegExecutable, opusEncoderExecutable);
    }

    private static string BuildOpusPipelineCommand(
        AudioProcessingRequest request,
        string ffmpegExecutable,
        string opusEncoderExecutable,
        string? progressTarget = null)
    {
        var bitrateKbps = GetRequiredOpusBitrateKbps(request);
        var ffmpegArguments = CommandLineDisplay.JoinArguments(BuildOpusPipelineFfmpegArgumentParts(request, progressTarget));
        return $"{Quote(ffmpegExecutable)} {ffmpegArguments} | {Quote(opusEncoderExecutable)} --bitrate {bitrateKbps} --ignorelength - {Quote(request.OutputPath)}";
    }

    internal static OpusPipelineStartInfos CreateOpusPipelineStartInfos(
        AudioProcessingRequest request,
        string ffmpegExecutable,
        string opusEncoderExecutable,
        string? progressTarget = null)
    {
        var ffmpegStartInfo = new ProcessStartInfo
        {
            FileName = ffmpegExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        foreach (var argument in BuildOpusPipelineFfmpegArgumentParts(request, progressTarget))
        {
            ffmpegStartInfo.ArgumentList.Add(argument);
        }

        var opusEncoderStartInfo = new ProcessStartInfo
        {
            FileName = opusEncoderExecutable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        opusEncoderStartInfo.ArgumentList.Add("--bitrate");
        opusEncoderStartInfo.ArgumentList.Add(GetRequiredOpusBitrateKbps(request).ToString(CultureInfo.InvariantCulture));
        opusEncoderStartInfo.ArgumentList.Add("--ignorelength");
        opusEncoderStartInfo.ArgumentList.Add("-");
        opusEncoderStartInfo.ArgumentList.Add(request.OutputPath);

        return new OpusPipelineStartInfos(ffmpegStartInfo, opusEncoderStartInfo);
    }

    private static string BuildFfmpegLibOpusDisplayArguments(AudioProcessingRequest request, string? progressTarget = null)
    {
        return CommandLineDisplay.JoinArguments(BuildFfmpegLibOpusArgumentParts(request, progressTarget));
    }

    private static IReadOnlyList<string> BuildFfmpegLibOpusArgumentParts(AudioProcessingRequest request, string? progressTarget = null)
    {
        var bitrateKbps = GetRequiredOpusBitrateKbps(request);
        var parts = new List<string>
        {
            "-hide_banner",
            "-y",
            "-nostats",
        };

        parts.AddRange(BuildFfmpegProgressArgumentParts(progressTarget));
        parts.AddRange(
        [
            "-i",
            request.SourcePath,
            "-map",
            "0:a:0",
            "-vn",
            "-sn",
            "-dn",
        ]);

        var mappingPlan = ResolveOpusMappingFamily1Plan(request);
        if (!string.IsNullOrWhiteSpace(mappingPlan.FilterGraph))
        {
            parts.Add("-filter:a");
            parts.Add(mappingPlan.FilterGraph);
        }

        parts.AddRange(
        [
            "-c:a",
            "libopus",
            "-b:a",
            $"{bitrateKbps}k",
            "-vbr",
            "on",
            "-application",
            "audio",
            "-ar",
            "48000",
            "-mapping_family",
            "1",
            request.OutputPath
        ]);

        return parts;
    }

    private static IReadOnlyList<string> BuildOpusPipelineFfmpegArgumentParts(
        AudioProcessingRequest request,
        string? progressTarget)
    {
        var parts = new List<string>
        {
            "-hide_banner",
            "-nostats"
        };

        parts.AddRange(BuildFfmpegProgressArgumentParts(progressTarget));
        parts.AddRange(
        [
            "-i",
            request.SourcePath,
            "-map",
            "0:a:0",
            "-vn",
            "-sn",
            "-dn",
            "-c:a",
            "pcm_s16le",
            "-ar",
            "48000",
            "-f",
            "wav",
            "pipe:1"
        ]);

        return parts;
    }

    private static IReadOnlyList<string> BuildFfmpegProgressArgumentParts(string? progressTarget)
    {
        var target = string.IsNullOrWhiteSpace(progressTarget)
            ? "pipe:2"
            : progressTarget;

        return
        [
            "-progress",
            target,
            "-stats_period",
            "0.5"
        ];
    }

    private static string BuildDdpDisplayArguments(string sourcePath, string outputDirectory)
    {
        return CommandLineDisplay.JoinArguments(new[] { "-i", sourcePath, "-o", outputDirectory, "-np" });
    }

    private static string BuildEac3ToDisplayArguments(AudioProcessingRequest request)
    {
        return CommandLineDisplay.JoinArguments(BuildEac3ToArgumentParts(request));
    }

    private static IReadOnlyList<string> BuildEac3ToArgumentParts(AudioProcessingRequest request)
    {
        var parts = new List<string>
        {
            request.SourcePath,
            request.OutputPath
        };

        if (request.Eac3ToAdditionalArguments.Count > 0)
        {
            parts.AddRange(request.Eac3ToAdditionalArguments);
        }

        parts.Add("-progressnumbers");
        return parts;
    }

    private static int GetRequiredOpusBitrateKbps(AudioProcessingRequest request)
    {
        return request.OpusBitrateKbps
            ?? throw new InvalidOperationException("Opus bitrate is not specified.");
    }

    private static bool ShouldUseFfmpegLibOpusMappingFamily1(AudioProcessingRequest request)
    {
        return request.UseOpusMappingFamily1
            && ResolveOpusMappingFamily1Plan(request).CanUseMappingFamily1;
    }

    private static OpusMappingFamily1Plan ResolveOpusMappingFamily1Plan(AudioProcessingRequest request)
    {
        if (request.SourceChannelCount is > 8)
        {
            return new OpusMappingFamily1Plan(false, null);
        }

        var layout = NormalizeChannelLayoutName(request.SourceChannelLayout);
        if (string.IsNullOrWhiteSpace(layout))
        {
            return new OpusMappingFamily1Plan(false, null);
        }

        var normalizedFilter = layout switch
        {
            "quad(side)" => "channelmap=map=FL-FL|FR-FR|SL-BL|SR-BR:channel_layout=quad",
            "5.0(side)" => "channelmap=map=FL-FL|FR-FR|FC-FC|SL-BL|SR-BR:channel_layout=5.0",
            "5.1(side)" => "channelmap=map=FL-FL|FR-FR|FC-FC|LFE-LFE|SL-BL|SR-BR:channel_layout=5.1",
            "6.1(back)" => "channelmap=map=FL-FL|FR-FR|FC-FC|LFE-LFE|BL-SL|BR-SR|BC-BC:channel_layout=6.1",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(normalizedFilter))
        {
            return new OpusMappingFamily1Plan(true, normalizedFilter);
        }

        return IsDirectOpusMappingFamily1Layout(layout)
            ? new OpusMappingFamily1Plan(true, null)
            : new OpusMappingFamily1Plan(false, null);
    }

    private static bool IsDirectOpusMappingFamily1Layout(string layout)
    {
        return layout is "mono"
            or "stereo"
            or "3.0"
            or "quad"
            or "5.0"
            or "5.1"
            or "6.1"
            or "7.1";
    }

    private static string NormalizeChannelLayoutName(string? layout)
    {
        return string.IsNullOrWhiteSpace(layout)
            ? string.Empty
            : layout.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    internal static AudioProcessingRunPlan CreateRunPlan(AudioProcessingRequest request)
    {
        if (request.Mode != AudioProcessingMode.Opus
            || string.IsNullOrWhiteSpace(request.OutputPath))
        {
            return new AudioProcessingRunPlan(request, request, null);
        }

        var stagedOutputPath = CreateStagedOutputPath(request.OutputPath, request.JobId);
        return new AudioProcessingRunPlan(
            request,
            request with { OutputPath = stagedOutputPath },
            stagedOutputPath);
    }

    private static void FinalizeOutputFileForSuccess(AudioProcessingRunPlan runPlan)
    {
        if (string.IsNullOrWhiteSpace(runPlan.StagedOutputPath))
        {
            return;
        }

        var stagedOutputPath = runPlan.StagedOutputPath;
        var finalOutputPath = runPlan.DisplayRequest.OutputPath;
        if (!File.Exists(stagedOutputPath))
        {
            throw new FileNotFoundException("Opus temporary output was not produced.", stagedOutputPath);
        }

        try
        {
            var outputDirectory = Path.GetDirectoryName(finalOutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            if (File.Exists(finalOutputPath))
            {
                var backupPath = CreateStagedOutputPath(finalOutputPath, runPlan.DisplayRequest.JobId, "backup");
                TryDeleteFile(backupPath);
                File.Replace(stagedOutputPath, finalOutputPath, backupPath, ignoreMetadataErrors: true);
                TryDeleteFile(backupPath);
                return;
            }

            File.Move(stagedOutputPath, finalOutputPath);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to finalize Opus output file: {finalOutputPath}", ex);
        }
    }

    private static void DeletePartialOutputFile(AudioProcessingRunPlan runPlan)
    {
        if (!string.IsNullOrWhiteSpace(runPlan.StagedOutputPath))
        {
            TryDeleteFile(runPlan.StagedOutputPath);
            TryDeleteOrphanedBackupFile(runPlan);
        }

        if (runPlan.DisplayRequest.Mode == AudioProcessingMode.Ddp)
        {
            TryDeleteZeroLengthDdpOutputFiles(runPlan.DisplayRequest);
            return;
        }

        TryDeleteZeroLengthFile(runPlan.DisplayRequest.OutputPath);
    }

    private static string CreateStagedOutputPath(string outputPath, Guid jobId, string suffix = "staging")
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        var outputFileName = Path.GetFileNameWithoutExtension(outputPath);
        var outputExtension = Path.GetExtension(outputPath);
        var stagedFileName = $"{outputFileName}.{jobId:N}.{suffix}.tmp{outputExtension}";
        return string.IsNullOrWhiteSpace(outputDirectory)
            ? stagedFileName
            : Path.Combine(outputDirectory, stagedFileName);
    }

    private static void TryDeleteFile(string path)
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
            WriteDebugDiagnostic($"Failed to delete temporary audio file '{path}'", ex);
        }
    }

    private static void TryDeleteZeroLengthFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.Length == 0)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            WriteDebugDiagnostic($"Failed to delete zero-length audio file '{path}'", ex);
        }
    }

    private static void TryDeleteZeroLengthDdpOutputFiles(AudioProcessingRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.OutputPath) || !Directory.Exists(request.OutputPath))
            {
                return;
            }

            var sourceStem = Path.GetFileNameWithoutExtension(request.SourcePath);
            foreach (var file in Directory.EnumerateFiles(request.OutputPath))
            {
                var extension = Path.GetExtension(file);
                if (!string.Equals(extension, ".ec3", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(extension, ".ac3", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(extension, ".ddp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(sourceStem)
                    && !fileName.Contains(sourceStem, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileInfo = new FileInfo(file);
                if (fileInfo.Length == 0)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            WriteDebugDiagnostic($"Failed to delete zero-length DDP output files for '{request.OutputPath}'", ex);
        }
    }

    private static void TryDeleteOrphanedBackupFile(AudioProcessingRunPlan runPlan)
    {
        if (string.IsNullOrWhiteSpace(runPlan.DisplayRequest.OutputPath))
        {
            return;
        }

        var backupPath = CreateStagedOutputPath(runPlan.DisplayRequest.OutputPath, runPlan.DisplayRequest.JobId, "backup");
        if (!File.Exists(runPlan.DisplayRequest.OutputPath))
        {
            return;
        }

        TryDeleteFile(backupPath);
    }

    private async Task<string> ResolveToolPathAsync(RegisteredToolKind kind, CancellationToken cancellationToken)
    {
        var result = await _toolProbeService.ProbeAsync(kind, cancellationToken);
        if (result.IsReady && !string.IsNullOrWhiteSpace(result.ExecutablePath))
        {
            return result.ExecutablePath;
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.FailureReason)
            ? T(GetLanguage(), $"No usable {kind.ToDisplayName()} was found.", $"未找到可用的 {kind.ToDisplayName()}。")
            : result.FailureReason);
    }

    private static void PrepareDeewEnvironment(
        ProcessStartInfo startInfo,
        string deewPath,
        string deePath,
        string ffmpegPath,
        string ffprobePath)
    {
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["FORCE_COLOR"] = "1";
        startInfo.Environment["TERM"] = "xterm-256color";
        startInfo.Environment.Remove("NO_COLOR");

        var pathEntries = new[]
        {
            Path.GetDirectoryName(deewPath),
            Path.GetDirectoryName(deePath),
            Path.GetDirectoryName(ffmpegPath),
            Path.GetDirectoryName(ffprobePath),
            Environment.GetEnvironmentVariable("PATH")
        }
        .Where(static entry => !string.IsNullOrWhiteSpace(entry))
        .Select(static entry => entry!)
        .Distinct(StringComparer.OrdinalIgnoreCase);

        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, pathEntries);
    }

    private static double? ParseProgress(AudioProcessingRequest request, string line)
    {
        return request.Mode switch
        {
            AudioProcessingMode.Ddp => ParseDeewProgress(line),
            AudioProcessingMode.Eac3To => ParseEac3ToProgress(line),
            AudioProcessingMode.Opus => ParseOpusProgress(line, request.SourceDurationSeconds),
            _ => null
        };
    }

    private static double? ParseDeewProgress(string line)
    {
        var stageMatch = DeewStageProgressRegex.Match(line);
        if (stageMatch.Success
            && double.TryParse(stageMatch.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var stageValue))
        {
            return Math.Clamp(stageValue / 100.0, 0.0, 1.0);
        }

        var displayMatch = DeewDisplayProgressRegex.Match(line);
        if (!displayMatch.Success)
        {
            return null;
        }

        return double.TryParse(displayMatch.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var displayValue)
            ? Math.Clamp(displayValue / 100.0, 0.0, 1.0)
            : null;
    }

    internal static double? ParseDeewProgressForTesting(string line)
        => ParseDeewProgress(ConsoleOutputLineNormalizer.Normalize(line));

    internal static string? TryBuildDdpSemanticFailureDetailForTesting(string log)
        => TryBuildDdpSemanticFailureDetail(log);

    private static double? ParseEac3ToProgress(string line)
    {
        var match = Eac3ToProcessProgressRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value / 100.0, 0.0, 1.0)
            : null;
    }

    private static double? ParseOpusProgress(string line, double? durationSeconds)
    {
        if (!durationSeconds.HasValue || durationSeconds.Value <= 0)
        {
            return null;
        }

        var processedSeconds = ParseFfmpegProcessedSeconds(line);
        if (!processedSeconds.HasValue)
        {
            return null;
        }

        return Math.Clamp(processedSeconds.Value / durationSeconds.Value, 0.0, 1.0);
    }

    internal static double? ParseOpusProgressForTesting(string line, double? durationSeconds)
        => ParseOpusProgress(ConsoleOutputLineNormalizer.Normalize(line), durationSeconds);

    private static bool ShouldReportDdpLine(
        string line,
        double? parsedProgress,
        bool reachedReportWindow,
        bool progressAdvancedEnough,
        bool lineChanged)
    {
        if (parsedProgress.HasValue)
        {
            return progressAdvancedEnough || (reachedReportWindow && lineChanged);
        }

        if (ToolLogLineClassifier.LooksLikeDeewConsoleProgressLine(line))
        {
            return false;
        }

        return lineChanged;
    }

    private static bool ShouldReportGenericLine(
        bool reachedReportWindow,
        bool progressAdvancedEnough,
        bool lineChanged)
    {
        if (!reachedReportWindow && !progressAdvancedEnough)
        {
            return false;
        }

        return lineChanged || progressAdvancedEnough;
    }

    private static bool ShouldReportImmediateCliLine(
        AudioProcessingMode mode,
        string line,
        bool lineChanged)
    {
        if (!lineChanged || string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return mode switch
        {
            AudioProcessingMode.Eac3To or AudioProcessingMode.Opus => !ToolLogLineClassifier.IsAudioTransientLine(mode, line),
            _ => false
        };
    }

    private static bool ShouldAppendLogLine(
        AudioProcessingMode mode,
        string line,
        double? parsedProgress,
        ref string lastLoggedLine)
    {
        var logLineChanged = !string.Equals(line, lastLoggedLine, StringComparison.Ordinal);
        if (!logLineChanged)
        {
            return false;
        }

        if (ToolLogLineClassifier.IsAudioTransientLine(mode, line))
        {
            return false;
        }

        if (mode == AudioProcessingMode.Ddp && parsedProgress.HasValue)
        {
            return false;
        }

        if (mode == AudioProcessingMode.Ddp && ToolLogLineClassifier.LooksLikeDeewConsoleProgressLine(line))
        {
            return false;
        }

        lastLoggedLine = line;
        return true;
    }

    private string NormalizeDetailLine(AudioProcessingRequest request, string line, double? parsedProgress, DeewProgressPhase deewPhase)
    {
        return request.Mode == AudioProcessingMode.Ddp
            ? line
            : NormalizeDisplayLine(request, line, parsedProgress, deewPhase);
    }

    private string NormalizeDisplayLine(AudioProcessingRequest request, string line, double? parsedProgress, DeewProgressPhase deewPhase)
    {
        if (request.Mode == AudioProcessingMode.Ddp)
        {
            if (parsedProgress.HasValue)
            {
                return $"process: {NormalizeRunningProgress(parsedProgress.Value) * 100:0.#}%";
            }

            return deewPhase == DeewProgressPhase.FfmpegPreparation && LooksLikeDeewWarmupLine(line)
                ? GetDeewWarmupDisplayLine()
                : deewPhase == DeewProgressPhase.DeeEncoding && LooksLikeDeewPhaseTransitionLine(line)
                    ? GetDeewEncodeDisplayLine(GetLanguage())
                    : line;
        }

        if (request.Mode != AudioProcessingMode.Opus)
        {
            return line;
        }

        if (parsedProgress.HasValue)
        {
            return $"process: {NormalizeRunningProgress(parsedProgress.Value) * 100:0.##}%";
        }

        return LooksLikeFfmpegProgressMetadataLine(line)
            ? string.Empty
            : line;
    }

    private static bool LooksLikeFfmpegProgressMetadataLine(string line)
    {
        return line.StartsWith("bitrate=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("total_size=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("out_time_ms=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("dup_frames=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("drop_frames=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("speed=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("progress=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("maxrss=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("frame=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("fps=", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("stream_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDeewWarmupLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || LooksLikeFailureLine(line))
        {
            return false;
        }

        return line.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
            || line.Contains("starting", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[output]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDeewPhaseTransitionLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || LooksLikeFailureLine(line))
        {
            return false;
        }

        return line.Contains("running the following commands", StringComparison.OrdinalIgnoreCase)
            || line.Contains("dee -x", StringComparison.OrdinalIgnoreCase)
            || line.Contains("encoding summary", StringComparison.OrdinalIgnoreCase)
            || line.Contains("dolby encoding engine wrapper", StringComparison.OrdinalIgnoreCase)
            || line.Contains("dee version", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[ dee:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[dee:", StringComparison.OrdinalIgnoreCase);
    }

    private static DeewProgressPhase UpdateDeewPhase(AudioProcessingMode mode, DeewProgressPhase currentPhase, string line)
    {
        if (mode != AudioProcessingMode.Ddp)
        {
            return DeewProgressPhase.None;
        }

        if (ParseDeewProgress(line).HasValue || LooksLikeDeewPhaseTransitionLine(line))
        {
            return DeewProgressPhase.DeeEncoding;
        }

        return currentPhase;
    }

    private static bool LooksLikeFailureLine(string line)
    {
        return line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("traceback", StringComparison.OrdinalIgnoreCase)
            || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || LooksLikeDdpPcmConfigurationFailure(line);
    }

    private static string? TryBuildSemanticFailureDetail(AudioProcessingMode mode, string log)
    {
        if (mode != AudioProcessingMode.Ddp)
        {
            return null;
        }

        return TryBuildDdpSemanticFailureDetail(log);
    }

    private static string? TryBuildDdpSemanticFailureDetail(string log)
    {
        var normalized = CollapseWhitespace(log);
        if (!LooksLikeDdpPcmConfigurationFailure(normalized))
        {
            return null;
        }

        var startIndex = normalized.IndexOf("pcm_to_ddp:", StringComparison.OrdinalIgnoreCase);
        var detail = startIndex >= 0 ? normalized[startIndex..] : normalized;
        var duplicateIndex = detail.IndexOf(" pcm_to_ddp:", "pcm_to_ddp:".Length, StringComparison.OrdinalIgnoreCase);
        if (duplicateIndex > 0)
        {
            detail = detail[..duplicateIndex];
        }

        const int maxDetailLength = 260;
        return detail.Length > maxDetailLength
            ? string.Concat(detail.AsSpan(0, maxDetailLength).TrimEnd(), "...")
            : detail;
    }

    private static bool LooksLikeDdpPcmConfigurationFailure(string line)
    {
        if (string.IsNullOrWhiteSpace(line)
            || !line.Contains("pcm_to_ddp:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return line.Contains("check input and downmix config", StringComparison.OrdinalIgnoreCase)
            || line.Contains("resulting output channels", StringComparison.OrdinalIgnoreCase)
            || line.Contains("valid value(s)", StringComparison.OrdinalIgnoreCase);
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(character);
            pendingSpace = false;
        }

        return builder.ToString().Trim();
    }

    private static double? ParseFfmpegProcessedSeconds(string line)
    {
        var outTimeMatch = FfmpegOutTimeRegex.Match(line);
        if (outTimeMatch.Success
            && TimeSpan.TryParse(outTimeMatch.Groups["value"].Value, CultureInfo.InvariantCulture, out var processed))
        {
            return processed.TotalSeconds;
        }

        var outTimeMsMatch = FfmpegOutTimeMsRegex.Match(line);
        if (outTimeMsMatch.Success
            && long.TryParse(outTimeMsMatch.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return milliseconds / 1_000_000d;
        }

        var outTimeUsMatch = FfmpegOutTimeUsRegex.Match(line);
        if (outTimeUsMatch.Success
            && long.TryParse(outTimeUsMatch.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
        {
            return microseconds / 1_000_000d;
        }

        return null;
    }

    private static double NormalizeRunningProgress(double value)
    {
        var clamped = Math.Clamp(value, 0.0, 1.0);
        return clamped >= 1.0 ? 0.999 : clamped;
    }

    private sealed class Eac3ToProgressState
    {
        public int CurrentPass { get; private set; } = 1;

        public int TotalPasses { get; private set; } = 1;

        public string? PhaseLabel =>
            TotalPasses > 1
                ? $"Pass {CurrentPass}/{TotalPasses}"
                : null;

        public void Update(string line)
        {
            var additionalPassMatch = Eac3ToAdditionalPassNeededRegex.Match(line);
            if (additionalPassMatch.Success
                && int.TryParse(additionalPassMatch.Groups["pass"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requiredPass)
                && requiredPass > 1)
            {
                TotalPasses = Math.Max(TotalPasses, requiredPass);
            }

            var startingPassMatch = Eac3ToStartingPassRegex.Match(line);
            if (startingPassMatch.Success
                && int.TryParse(startingPassMatch.Groups["pass"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentPass)
                && currentPass > 1)
            {
                CurrentPass = Math.Max(CurrentPass, currentPass);
                TotalPasses = Math.Max(TotalPasses, currentPass);
            }
        }

        public double Normalize(double rawProgress)
        {
            var normalizedRaw = Math.Clamp(rawProgress, 0.0, 1.0);
            if (TotalPasses <= 1)
            {
                return normalizedRaw;
            }

            var currentPassIndex = Math.Clamp(CurrentPass - 1, 0, TotalPasses - 1);
            return ((double)currentPassIndex + normalizedRaw) / TotalPasses;
        }
    }

    private sealed class OpusTelemetryState
    {
        private readonly double? _sourceDurationSeconds;
        private readonly int? _targetBitrateKbps;
        private readonly string _outputPath;

        private double? _processedSeconds;
        private double? _speedMultiplier;
        private double? _lastObservedProcessedSeconds;
        private DateTimeOffset? _lastObservedProcessedAt;

        public OpusTelemetryState(double? sourceDurationSeconds, int? targetBitrateKbps, string outputPath)
        {
            _sourceDurationSeconds = sourceDurationSeconds;
            _targetBitrateKbps = targetBitrateKbps;
            _outputPath = outputPath;
        }

        public void Update(string line)
        {
            var processedSeconds = ParseFfmpegProcessedSeconds(line);
            if (processedSeconds.HasValue)
            {
                UpdateDerivedSpeed(processedSeconds.Value);
                _processedSeconds = processedSeconds.Value;
                return;
            }

            var speedMatch = FfmpegSpeedRegex.Match(line);
            if (speedMatch.Success
                && double.TryParse(speedMatch.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            {
                _speedMultiplier = speed > 0 ? speed : null;
            }
        }

        private void UpdateDerivedSpeed(double processedSeconds)
        {
            var observedAt = DateTimeOffset.UtcNow;
            if (_lastObservedProcessedSeconds.HasValue
                && _lastObservedProcessedAt.HasValue)
            {
                var processedDelta = processedSeconds - _lastObservedProcessedSeconds.Value;
                var wallClockDelta = (observedAt - _lastObservedProcessedAt.Value).TotalSeconds;
                if (processedDelta > 0 && wallClockDelta > 0.05)
                {
                    var derivedSpeed = processedDelta / wallClockDelta;
                    if (derivedSpeed > 0)
                    {
                        _speedMultiplier = derivedSpeed;
                    }
                }
            }

            _lastObservedProcessedSeconds = processedSeconds;
            _lastObservedProcessedAt = observedAt;
        }

        public AudioProcessingTelemetry? Build(double? progressFraction)
        {
            var estimatedOutputBytes = EstimateOutputBytes(progressFraction);
            var bitrateKbps = ResolveBitrateKbps();
            var remaining = EstimateRemaining();

            if (_speedMultiplier is null
                && bitrateKbps is null
                && remaining is null
                && estimatedOutputBytes is null)
            {
                return null;
            }

            return new AudioProcessingTelemetry(
                _speedMultiplier,
                bitrateKbps,
                remaining,
                estimatedOutputBytes);
        }

        private double? ResolveBitrateKbps()
        {
            var currentOutputBytes = TryGetOutputFileSize();
            if (currentOutputBytes.HasValue
                && _processedSeconds.HasValue
                && _processedSeconds.Value > 0)
            {
                return currentOutputBytes.Value * 8d / _processedSeconds.Value / 1000d;
            }

            return _targetBitrateKbps;
        }

        private TimeSpan? EstimateRemaining()
        {
            if (!_sourceDurationSeconds.HasValue
                || !_processedSeconds.HasValue)
            {
                return null;
            }

            var sourceRemainingSeconds = Math.Max(_sourceDurationSeconds.Value - _processedSeconds.Value, 0d);
            if (sourceRemainingSeconds <= 0)
            {
                return TimeSpan.Zero;
            }

            if (_speedMultiplier.HasValue && _speedMultiplier.Value > 0)
            {
                return TimeSpan.FromSeconds(sourceRemainingSeconds / _speedMultiplier.Value);
            }

            return null;
        }

        private long? EstimateOutputBytes(double? progressFraction)
        {
            if (_sourceDurationSeconds.HasValue
                && _sourceDurationSeconds.Value > 0
                && _targetBitrateKbps.HasValue
                && _targetBitrateKbps.Value > 0)
            {
                return (long)Math.Round(_targetBitrateKbps.Value * 1000d / 8d * _sourceDurationSeconds.Value);
            }

            var currentOutputBytes = TryGetOutputFileSize();
            if (currentOutputBytes.HasValue
                && progressFraction.HasValue
                && progressFraction.Value > 0)
            {
                return (long)Math.Round(currentOutputBytes.Value / progressFraction.Value);
            }

            return null;
        }

        private long? TryGetOutputFileSize()
        {
            try
            {
                if (!File.Exists(_outputPath))
                {
                    return null;
                }

                return new FileInfo(_outputPath).Length;
            }
            catch
            {
                return null;
            }
        }
    }

    private sealed record OpusProgressFileSnapshot(
        string ProgressTimeLine,
        string? SpeedLine);

    internal sealed record OpusPipelineStartInfos(
        ProcessStartInfo FfmpegStartInfo,
        ProcessStartInfo OpusEncoderStartInfo);

    internal sealed record AudioProcessingRunPlan(
        AudioProcessingRequest DisplayRequest,
        AudioProcessingRequest ExecutionRequest,
        string? StagedOutputPath);

    private sealed record OpusMappingFamily1Plan(
        bool CanUseMappingFamily1,
        string? FilterGraph);

    private sealed record ProcessExecutionResult(
        int ExitCode,
        string? ExitCodeDetail = null);

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
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

            for (var i = 0; i < read; i++)
            {
                var character = buffer[i];
                if (character is '\r' or '\n')
                {
                    FlushConsoleSegment(segmentBuilder, onLine);
                    continue;
                }

                if (!char.IsControl(character) || character is '\t' or '\u001B')
                {
                    segmentBuilder.Append(character);
                }
            }
        }

        FlushConsoleSegment(segmentBuilder, onLine);
    }

    private static async Task CopyOpusPcmPipeAsync(
        Stream ffmpegOutput,
        Stream opusInput,
        CancellationToken cancellationToken)
    {
        try
        {
            await ffmpegOutput.CopyToAsync(opusInput, cancellationToken);
        }
        finally
        {
            try
            {
                await opusInput.DisposeAsync();
            }
            catch (Exception ex)
            {
                WriteDebugDiagnostic("Failed to dispose Opus stdin pipe", ex);
            }
        }
    }

    private static ProcessExecutionResult BuildOpusPipelineExecutionResult(int ffmpegExitCode, int opusExitCode)
    {
        if (ffmpegExitCode == 0 && opusExitCode == 0)
        {
            return new ProcessExecutionResult(0);
        }

        var exitCode = ffmpegExitCode != 0 ? ffmpegExitCode : opusExitCode;
        return new ProcessExecutionResult(
            exitCode,
            $"FFmpeg exit code {ffmpegExitCode}, opusenc exit code {opusExitCode}");
    }

    private static async Task PumpProgressFileAsync(Process process, string path, Action<string> onLine)
    {
        OpusProgressFileSnapshot? lastSnapshot = null;

        while (true)
        {
            var snapshot = await ReadOpusProgressFileSnapshotAsync(path);
            if (snapshot is not null)
            {
                if (!string.Equals(snapshot.ProgressTimeLine, lastSnapshot?.ProgressTimeLine, StringComparison.Ordinal))
                {
                    onLine(snapshot.ProgressTimeLine);
                }

                if (!string.IsNullOrWhiteSpace(snapshot.SpeedLine)
                    && !string.Equals(snapshot.SpeedLine, lastSnapshot?.SpeedLine, StringComparison.Ordinal))
                {
                    onLine(snapshot.SpeedLine);
                }

                lastSnapshot = snapshot;
            }

            if (process.HasExited)
            {
                break;
            }

            await Task.Delay(ProgressFilePollInterval);
        }
    }

    private static async Task<OpusProgressFileSnapshot?> ReadOpusProgressFileSnapshotAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            var content = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            string? outTimeLine = null;
            string? outTimeMsLine = null;
            string? outTimeUsLine = null;
            string? speedLine = null;

            var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var rawLine in lines)
            {
                if (rawLine.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase))
                {
                    outTimeLine = rawLine;
                    continue;
                }

                if (rawLine.StartsWith("out_time_ms=", StringComparison.OrdinalIgnoreCase))
                {
                    outTimeMsLine = rawLine;
                    continue;
                }

                if (rawLine.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase))
                {
                    outTimeUsLine = rawLine;
                    continue;
                }

                if (rawLine.StartsWith("speed=", StringComparison.OrdinalIgnoreCase))
                {
                    speedLine = rawLine;
                }
            }

            var progressTimeLine = outTimeLine ?? outTimeMsLine ?? outTimeUsLine;
            if (string.IsNullOrWhiteSpace(progressTimeLine))
            {
                return null;
            }

            return new OpusProgressFileSnapshot(progressTimeLine, speedLine);
        }
        catch (IOException ex)
        {
            WriteDebugDiagnostic($"Failed to read Opus progress file '{path}'", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteDebugDiagnostic($"Access denied while reading Opus progress file '{path}'", ex);
        }

        return null;
    }

    private static async Task<int> GetExitCodeAsync(Process process, Task waitForExitTask)
    {
        await waitForExitTask;
        return process.ExitCode;
    }

    private static bool TryTerminate(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(true);
            return true;
        }
        catch (Exception ex)
        {
            WriteDebugDiagnostic("Failed to terminate audio process", ex);
            return false;
        }
    }

    private static void WriteDebugDiagnostic(string message, Exception exception)
    {
        Debug.WriteLine($"{nameof(CliAudioProcessingRunner)}: {message}. {exception}");
    }

    private static string BuildStartingSummary(AppLanguage language, AudioProcessingMode mode)
    {
        return mode switch
        {
            AudioProcessingMode.Eac3To => T(language, "eac3to started", "eac3to 转换已启动"),
            AudioProcessingMode.Ddp => GetDeewWarmupDisplayLine(language),
            AudioProcessingMode.Opus => T(language, "Opus started", "Opus 转换已启动"),
            _ => T(language, "Audio processing started", "音频处理已启动")
        };
    }

    private static string BuildStartingDetail(AppLanguage language, AudioProcessingMode mode)
    {
        return mode == AudioProcessingMode.Ddp
            ? GetDeewWarmupDisplayLine(language)
            : string.Empty;
    }

    private static string BuildRunningSummary(
        AppLanguage language,
        AudioProcessingMode mode,
        double progress,
        bool hasKnownProgress,
        string line,
        string? phaseLabel = null,
        DeewProgressPhase deewPhase = DeewProgressPhase.None)
    {
        if (hasKnownProgress)
        {
            return mode switch
            {
                AudioProcessingMode.Eac3To => string.IsNullOrWhiteSpace(phaseLabel)
                    ? T(language, $"eac3to {progress * 100:0.#}%", $"eac3to 转换中 {progress * 100:0.#}%")
                    : T(language, $"eac3to {phaseLabel} · {progress * 100:0.#}%", $"eac3to 转换中 {phaseLabel} · {progress * 100:0.#}%"),
                AudioProcessingMode.Ddp => T(language, $"DEE encoding {progress * 100:0.#}%", $"DEE 编码中 {progress * 100:0.#}%"),
                AudioProcessingMode.Opus => T(language, $"Opus {progress * 100:0.##}%", $"Opus 转换中 {progress * 100:0.##}%"),
                _ => T(language, $"Audio processing {progress * 100:0.#}%", $"音频处理中 {progress * 100:0.#}%")
            };
        }

        if (mode == AudioProcessingMode.Eac3To && !string.IsNullOrWhiteSpace(phaseLabel))
        {
            return T(language, $"eac3to {phaseLabel}", $"eac3to 转换中 {phaseLabel}");
        }

        if (mode == AudioProcessingMode.Ddp && deewPhase == DeewProgressPhase.DeeEncoding)
        {
            return GetDeewEncodeDisplayLine(language);
        }

        return string.IsNullOrWhiteSpace(line)
            ? T(language, "Audio processing", "音频处理中")
            : line.Trim();
    }

    private static string BuildCompletedSummary(AppLanguage language, AudioProcessingMode mode)
    {
        return mode switch
        {
            AudioProcessingMode.Eac3To => T(language, "eac3to completed", "eac3to 转换完成"),
            AudioProcessingMode.Ddp => T(language, "DDP completed", "DDP 转换完成"),
            AudioProcessingMode.Opus => T(language, "Opus completed", "Opus 转换完成"),
            _ => T(language, "Audio processing completed", "音频处理完成")
        };
    }

    private static string BuildCancelledSummary(AppLanguage language, AudioProcessingMode mode)
    {
        return mode switch
        {
            AudioProcessingMode.Eac3To => T(language, "eac3to cancelled", "eac3to 转换已取消"),
            AudioProcessingMode.Ddp => T(language, "DDP cancelled", "DDP 转换已取消"),
            AudioProcessingMode.Opus => T(language, "Opus cancelled", "Opus 转换已取消"),
            _ => T(language, "Audio processing cancelled", "音频处理已取消")
        };
    }

    private static string BuildFailureSummary(AppLanguage language, AudioProcessingMode mode)
    {
        return mode switch
        {
            AudioProcessingMode.Eac3To => T(language, "eac3to failed", "eac3to 转换失败"),
            AudioProcessingMode.Ddp => T(language, "DDP failed", "DDP 转换失败"),
            AudioProcessingMode.Opus => T(language, "Opus failed", "Opus 转换失败"),
            _ => T(language, "Audio processing failed", "音频处理失败")
        };
    }

    private static string LastMeaningfulLine(string log)
    {
        return log
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? string.Empty;
    }

    private AppLanguage GetLanguage() => _settingsService.Load().Language;

    private string GetDeewWarmupDisplayLine() => GetDeewWarmupDisplayLine(GetLanguage());

    private static string GetDeewWarmupDisplayLine(AppLanguage language) =>
        T(language, "FFmpeg is preparing the source. Please wait...", "ffmpeg 处理中，请稍后...");

    private static string GetDeewEncodeDisplayLine(AppLanguage language) =>
        T(language, "DEE encoding in progress...", "DEE 编码中，请稍后...");

    private static string T(AppLanguage language, string en, string zh) =>
        language == AppLanguage.English ? en : zh;

    private static string Quote(string value) => CommandLineDisplay.Quote(value);

    private static void FlushConsoleSegment(StringBuilder segmentBuilder, Action<string> onLine)
    {
        if (segmentBuilder.Length == 0)
        {
            return;
        }

        var normalized = NormalizeConsoleSegment(segmentBuilder.ToString());
        segmentBuilder.Clear();

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            onLine(normalized);
        }
    }

    private static string NormalizeConsoleSegment(string text)
    {
        return ConsoleOutputLineNormalizer.Normalize(text);
    }

    internal string NormalizeDisplayLineForTesting(
        AudioProcessingRequest request,
        string line,
        double? parsedProgress,
        string deewPhase)
    {
        var phase = Enum.TryParse<DeewProgressPhase>(deewPhase, ignoreCase: true, out var parsedPhase)
            ? parsedPhase
            : DeewProgressPhase.None;
        return NormalizeDisplayLine(request, line, parsedProgress, phase);
    }

    private enum DeewProgressPhase
    {
        None,
        FfmpegPreparation,
        DeeEncoding
    }

}
