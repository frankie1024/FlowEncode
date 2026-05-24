using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class LegacyAv1anCliFallbackRunner : IAutoCompressionRunner
{
    private const string TempWorkspaceFolderName = ".flowencode-temp";
    private const int MaxVisibleLogLength = 200_000;
    private const int RetainedVisibleLogLength = 120_000;
    private const string VisibleLogTruncationMarker = "[Log truncated; only latest output is kept]";
    private static readonly Regex ProgressRegex = new(@"(?<!\d)(?<value>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex FractionProgressRegex = new(@"(?<!\d)(?<done>\d{1,7})\s*/\s*(?<total>\d{1,7})(?!\d)", RegexOptions.Compiled);
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(250);
    private const double SignificantProgressDelta = 0.0025;

    private readonly ExternalToolLocator _toolLocator;
    private readonly IAppSettingsService _settingsService;
    private readonly LocalAppPaths _appPaths;
    private readonly ConcurrentDictionary<Guid, ManagedProcessExecution> _activeExecutions = new();

    public LegacyAv1anCliFallbackRunner(LocalAppPaths paths, IAppSettingsService settingsService)
    {
        _appPaths = paths;
        _settingsService = settingsService;
        _toolLocator = new ExternalToolLocator(paths, settingsService);
    }

    public string BuildDisplayCommand(AutoCompressionRequest request)
    {
        var av1anPath = _toolLocator.ResolveAv1an();
        var arguments = BuildArgumentParts(request, GetTempDirectory(request), GetLanguage());
        return $"{Quote(av1anPath)} {CommandLineDisplay.JoinArguments(arguments)}";
    }

    public void Abort(Guid jobId)
    {
        if (_activeExecutions.TryRemove(jobId, out var execution))
        {
            execution.Terminate();
        }
    }

    public async Task<AutoCompressionResult> RunAsync(
        AutoCompressionRequest request,
        IProgress<AutoCompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var language = GetLanguage();
        if (!File.Exists(request.SourcePath))
        {
            throw new FileNotFoundException(T(language, "Auto encode source file was not found.", "未找到自动压制输入源文件。"), request.SourcePath);
        }

        var av1anPath = _toolLocator.ResolveAv1an();
        await EnsureAv1anRuntimeReadyAsync(av1anPath, language, cancellationToken);
        var tempDirectory = GetTempDirectory(request);
        Directory.CreateDirectory(tempDirectory);
        var stagedOutputPath = CreateStagedOutputPath(request, tempDirectory);
        var executionRequest = request with { OutputPath = stagedOutputPath };
        var arguments = BuildArgumentParts(executionRequest, tempDirectory, language);
        var displayCommand = $"{Quote(av1anPath)} {CommandLineDisplay.JoinArguments(BuildArgumentParts(request, tempDirectory, language))}";
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
        var lastProgress = 0.0;
        var hasKnownProgress = false;
        var lastReportAt = DateTimeOffset.MinValue;
        var lastReportedProgress = 0.0;
        var hasReportedProgress = false;
        var lastReportedLine = string.Empty;

        void HandleLine(string line)
        {
            var normalizedLine = ConsoleOutputLineNormalizer.Normalize(line);
            if (string.IsNullOrWhiteSpace(normalizedLine))
            {
                return;
            }

            AutoCompressionProgress? update = null;

            lock (gate)
            {
                var now = DateTimeOffset.UtcNow;
                var parsedProgress = ParseProgressFraction(normalizedLine) ?? ParseFractionProgress(normalizedLine);
                var detailLine = NormalizeDisplayLine(normalizedLine, parsedProgress);
                var isTransient = ToolLogLineClassifier.IsAutoCompressionTransientLine(detailLine)
                    || ToolLogLineClassifier.IsAutoCompressionTransientLine(normalizedLine);
                if (parsedProgress.HasValue)
                {
                    hasKnownProgress = true;
                    lastProgress = Math.Clamp(Math.Max(lastProgress, parsedProgress.Value), 0.0, 1.0);
                }

                if (!isTransient)
                {
                    AppendVisibleLogLine(logBuilder, normalizedLine);
                }

                var reachedReportWindow = now - lastReportAt >= ProgressReportInterval;
                var progressAdvancedEnough = hasKnownProgress
                    && (!hasReportedProgress || lastProgress - lastReportedProgress >= SignificantProgressDelta);
                var lineChanged = !string.Equals(detailLine, lastReportedLine, StringComparison.Ordinal);
                var shouldReport = !isTransient
                    ? lineChanged
                    : parsedProgress.HasValue
                        ? progressAdvancedEnough || (reachedReportWindow && lineChanged)
                        : reachedReportWindow && lineChanged;

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

                lastReportedLine = detailLine;
                var stage = ResolveStage(normalizedLine);
                var summary = BuildRunningSummary(language, stage, lastProgress, normalizedLine);
                update = new AutoCompressionProgress(
                    request.JobId,
                    EncodingJobState.Running,
                    stage,
                    hasKnownProgress ? lastProgress : null,
                    summary,
                    detailLine);
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
            process = CreateProcess(av1anPath, arguments, GetWorkingDirectory(request, av1anPath));
            process.Start();
            activeExecution = new ManagedProcessExecution(
                message => WriteDiagnostic($"Auto compression job {request.JobId}: {message}"),
                process);
            _activeExecutions[request.JobId] = activeExecution;

            pumpOutput = PumpAsync(process.StandardOutput, HandleLine, cancellationToken);
            pumpError = PumpAsync(process.StandardError, HandleLine, cancellationToken);

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
                        out var failureSummary))
                {
                    progress?.Report(new AutoCompressionProgress(
                        request.JobId,
                        EncodingJobState.Failed,
                        AutoCompressionExecutionStage.Failed,
                        null,
                        failureSummary,
                        failureSummary));

                    return new AutoCompressionResult(
                        request.JobId,
                        EncodingJobState.Failed,
                        finalExitCode,
                        failureSummary,
                        log,
                        displayCommand);
                }

                outputFinalized = true;
                progress?.Report(new AutoCompressionProgress(
                    request.JobId,
                    EncodingJobState.Completed,
                    AutoCompressionExecutionStage.Completed,
                    1.0,
                    T(language, "Auto encode completed", "自动压制完成"),
                    LastMeaningfulLine(log)));

                return new AutoCompressionResult(
                    request.JobId,
                    EncodingJobState.Completed,
                    0,
                    T(language, "Auto encode completed", "自动压制完成"),
                    log,
                    displayCommand);
            }

            progress?.Report(new AutoCompressionProgress(
                request.JobId,
                EncodingJobState.Failed,
                AutoCompressionExecutionStage.Failed,
                null,
                T(language, $"Auto encode failed (exit code {finalExitCode})", $"自动压制失败，退出代码 {finalExitCode}"),
                LastMeaningfulLine(log)));

            return new AutoCompressionResult(
                request.JobId,
                EncodingJobState.Failed,
                finalExitCode,
                T(language, $"Auto encode failed (exit code {finalExitCode})", $"自动压制失败，退出代码 {finalExitCode}"),
                log,
                displayCommand);
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
                WriteDiagnostic($"Auto compression job {request.JobId}: failed to drain process output after cancellation. {ex.GetType().Name}: {ex.Message}");
            }

            var log = logBuilder.ToString();
            progress?.Report(new AutoCompressionProgress(
                request.JobId,
                EncodingJobState.Cancelled,
                AutoCompressionExecutionStage.Cancelled,
                null,
                T(language, "Auto encode cancelled", "自动压制已取消"),
                T(language, "The task was cancelled.", "任务已取消。")));

            return new AutoCompressionResult(
                request.JobId,
                EncodingJobState.Cancelled,
                -1,
                T(language, "Auto encode cancelled", "自动压制已取消"),
                log,
                displayCommand);
        }
        finally
        {
            _activeExecutions.TryRemove(request.JobId, out _);
            activeExecution?.Dispose();
            ExecutionOutputStaging.CleanupStagedFile(stagedOutputPath, request.OutputPath, request.JobId, WriteDiagnostic);
            CleanupPartialOutputFile(request, outputFinalized, WriteDiagnostic);
            CleanupJobTempDirectory(request, WriteDiagnostic);
        }
    }

    internal static IReadOnlyList<string> BuildArgumentParts(
        AutoCompressionRequest request,
        string tempDirectory,
        AppLanguage language,
        bool useStructuredProgress = false)
    {
        if (request.Probes <= 0)
        {
            throw new InvalidOperationException(T(language, "The probe count must be greater than 0.", "探测次数必须大于 0。"));
        }

        if (!string.IsNullOrWhiteSpace(request.VideoParameters)
            && (request.VideoParameters.Contains('\r') || request.VideoParameters.Contains('\n')))
        {
            throw new InvalidOperationException(T(language, "Fine parameters must be a single line.", "小参不支持换行，请使用单行参数。"));
        }

        var args = new List<string>
        {
            "-i",
            request.SourcePath,
            "-o",
            request.OutputPath,
            "-y",
            "--keep",
            "--temp",
            tempDirectory,
            "--encoder",
            MapEncoder(request.EncoderKind, language),
            "--target-metric",
            MapMetric(request.Metric),
            "--target-quality",
            request.TargetQuality.ToString("0.###", CultureInfo.InvariantCulture),
            "--probes",
            request.Probes.ToString(CultureInfo.InvariantCulture)
        };

        if (useStructuredProgress)
        {
            args.Add("--progress-format");
            args.Add("jsonl");
        }

        if (request.Workers is > 0)
        {
            args.Add("--workers");
            args.Add(request.Workers.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (request.SearchProfile is { InterpolationMethod: { Length: > 0 } interpolationMethod })
        {
            args.Add("--interp-method");
            args.Add(interpolationMethod);
        }

        if (request.SearchProfile is { ProbingStatistic: { Length: > 0 } probingStatistic })
        {
            args.Add("--probing-stat");
            args.Add(probingStatistic);
        }

        if (request.SearchProfile is { ProbeResolution: { Length: > 0 } probeResolution })
        {
            args.Add("--probe-res");
            args.Add(probeResolution);
        }

        if (request.SearchProfile is { ProbingRate: > 0 and not 1 } searchProfile)
        {
            args.Add("--probing-rate");
            args.Add(searchProfile.ProbingRate.ToString(CultureInfo.InvariantCulture));
        }

        if (!useStructuredProgress && request.SearchProfile?.ScoutEnabled == true)
        {
            // Placeholder until the fork implements scout explicitly.
            args.Add("--probe-slow");
        }

        if (!string.IsNullOrWhiteSpace(request.VideoParameters))
        {
            args.Add("--video-params");
            args.Add(request.VideoParameters.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.BackendArguments))
        {
            args.AddRange(CommandArgumentTokenizer.Tokenize(request.BackendArguments, throwOnUnclosedQuote: true));
        }

        return args;
    }

    internal static string GetTempDirectory(AutoCompressionRequest request)
    {
        var outputDirectory = Path.GetDirectoryName(request.OutputPath);
        var baseDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Environment.CurrentDirectory
            : outputDirectory;
        return Path.Combine(baseDirectory, TempWorkspaceFolderName, "av1an", request.JobId.ToString("N"));
    }

    internal static string CreateStagedOutputPath(AutoCompressionRequest request, string tempDirectory)
    {
        var fileName = Path.GetFileName(request.OutputPath);
        return Path.Combine(tempDirectory, fileName);
    }

    internal static void CleanupJobTempDirectory(AutoCompressionRequest request, Action<string> writeDiagnostic)
    {
        var jobTempDirectory = GetTempDirectory(request);
        BestEffortCleanup.DeleteDirectoryRecursively(
            jobTempDirectory,
            $"auto compression temp directory '{jobTempDirectory}'",
            writeDiagnostic);
        BestEffortCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(jobTempDirectory), writeDiagnostic);
        BestEffortCleanup.DeleteDirectoryIfEmpty(Path.GetDirectoryName(Path.GetDirectoryName(jobTempDirectory)), writeDiagnostic);
    }

    internal static void CleanupPartialOutputFile(AutoCompressionRequest request, bool preserveOutput, Action<string> writeDiagnostic)
    {
        if (preserveOutput)
        {
            return;
        }

        BestEffortCleanup.DeleteFileIfZeroLength(
            request.OutputPath,
            $"auto compression partial output '{request.OutputPath}'",
            writeDiagnostic);
    }

    private void WriteDiagnostic(string message)
    {
        AppDiagnosticsLog.Write(_appPaths, nameof(LegacyAv1anCliFallbackRunner), message);
    }

    internal static string MapEncoder(EncoderKind kind, AppLanguage language)
    {
        return kind switch
        {
            EncoderKind.X264 => "x264",
            EncoderKind.X265 => "x265",
            EncoderKind.SvtAv1 => "svt-av1",
            _ => throw new InvalidOperationException(T(language, $"Unsupported encoder: {kind}", $"不支持的编码器: {kind}"))
        };
    }

    internal static string MapMetric(AutoCompressionMetric metric)
    {
        return metric switch
        {
            AutoCompressionMetric.Vmaf => "vmaf",
            AutoCompressionMetric.Ssimulacra2 => "ssimulacra2",
            AutoCompressionMetric.ButteraugliInf => "butteraugli-inf",
            AutoCompressionMetric.Butteraugli3 => "butteraugli-3",
            AutoCompressionMetric.Xpsnr => "xpsnr",
            AutoCompressionMetric.XpsnrWeighted => "xpsnr-weighted",
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null)
        };
    }

    internal static Process CreateProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory
                : workingDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        VapourSynthRuntimePathResolver.EnrichProcessPath(startInfo);

        return new Process
        {
            StartInfo = startInfo
        };
    }

    internal static string GetWorkingDirectory(AutoCompressionRequest request, string fileName)
    {
        var sourceDirectory = Path.GetDirectoryName(request.SourcePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return sourceDirectory;
        }

        return Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory;
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

    private static double? ParseProgressFraction(string line)
    {
        var match = ProgressRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return null;
        }

        return Math.Clamp(percent / 100.0, 0.0, 1.0);
    }

    private static double? ParseFractionProgress(string line)
    {
        if (!LooksLikeProgressLine(line))
        {
            return null;
        }

        var match = FractionProgressRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups["done"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var done)
            || !int.TryParse(match.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            || total <= 0
            || done < 0)
        {
            return null;
        }

        if (done > total)
        {
            return null;
        }

        return Math.Clamp((double)done / total, 0.0, 1.0);
    }

    private static bool LooksLikeProgressLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return line.Contains("chunk", StringComparison.OrdinalIgnoreCase)
            || line.Contains("scene", StringComparison.OrdinalIgnoreCase)
            || line.Contains("probe", StringComparison.OrdinalIgnoreCase)
            || line.Contains("target quality", StringComparison.OrdinalIgnoreCase)
            || line.Contains("split", StringComparison.OrdinalIgnoreCase)
            || line.Contains("encode", StringComparison.OrdinalIgnoreCase)
            || line.Contains("progress", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRunningSummary(AppLanguage language, AutoCompressionExecutionStage stage, double progressFraction, string line)
    {
        var stageText = ResolveStageText(language, stage, line);
        if (progressFraction > 0)
        {
            return string.IsNullOrWhiteSpace(stageText)
                ? T(language, $"Auto encode {progressFraction:P0}", $"自动压制中 {progressFraction:P0}")
                : $"{stageText} {progressFraction:P0}";
        }

        return stageText ?? T(language, "Auto encode running", "自动压制中");
    }

    private static AutoCompressionExecutionStage ResolveStage(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return AutoCompressionExecutionStage.Preparing;
        }

        if (line.Contains("scenecut", StringComparison.OrdinalIgnoreCase)
            || line.Contains("scene(s)", StringComparison.OrdinalIgnoreCase))
        {
            return AutoCompressionExecutionStage.SceneDetection;
        }

        if (line.Contains("target quality", StringComparison.OrdinalIgnoreCase)
            || line.Contains("probe", StringComparison.OrdinalIgnoreCase)
            || line.Contains("vmaf", StringComparison.OrdinalIgnoreCase)
            || line.Contains("ssimulacra", StringComparison.OrdinalIgnoreCase)
            || line.Contains("xpsnr", StringComparison.OrdinalIgnoreCase)
            || line.Contains("butteraugli", StringComparison.OrdinalIgnoreCase))
        {
            return AutoCompressionExecutionStage.Probing;
        }

        if (line.Contains("chunk", StringComparison.OrdinalIgnoreCase)
            || line.Contains("encoding", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fps", StringComparison.OrdinalIgnoreCase))
        {
            return AutoCompressionExecutionStage.Encoding;
        }

        if (line.Contains("concat", StringComparison.OrdinalIgnoreCase)
            || line.Contains("merge", StringComparison.OrdinalIgnoreCase)
            || line.Contains("mux", StringComparison.OrdinalIgnoreCase))
        {
            return AutoCompressionExecutionStage.Concatenating;
        }

        if (line.Contains("input:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("split", StringComparison.OrdinalIgnoreCase))
        {
            return AutoCompressionExecutionStage.ChunkPlanning;
        }

        return AutoCompressionExecutionStage.Preparing;
    }

    private static string? ResolveStageText(AppLanguage language, AutoCompressionExecutionStage stage, string line)
    {
        return stage switch
        {
            AutoCompressionExecutionStage.SceneDetection => T(language, "Scene detection", "场景检测中"),
            AutoCompressionExecutionStage.Probing => T(language, "Target probing", "目标探测中"),
            AutoCompressionExecutionStage.Encoding => T(language, "Encoding", "编码中"),
            AutoCompressionExecutionStage.Concatenating => T(language, "Finalizing", "收尾处理中"),
            AutoCompressionExecutionStage.ChunkPlanning => T(language, "Preparing", "预处理中"),
            _ => string.IsNullOrWhiteSpace(line) ? null : T(language, "Preparing", "预处理中")
        };
    }

    private static string NormalizeDisplayLine(string line, double? parsedProgress)
    {
        return parsedProgress.HasValue
            ? $"process: {Math.Clamp(parsedProgress.Value, 0.0, 1.0) * 100:0.##}%"
            : line;
    }

    internal static void AppendVisibleLogLine(StringBuilder builder, string line)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(line);
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

        var retainedText = builder.ToString();
        var firstLineBreak = retainedText.IndexOfAny(['\r', '\n']);
        if (firstLineBreak >= 0 && firstLineBreak + 1 < builder.Length)
        {
            builder.Remove(0, firstLineBreak + 1);
        }

        if (!builder.ToString().StartsWith(VisibleLogTruncationMarker, StringComparison.Ordinal))
        {
            builder.Insert(0, $"{VisibleLogTruncationMarker}{Environment.NewLine}");
        }
    }

    internal static string LastMeaningfulLine(string log)
    {
        return log
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? string.Empty;
    }

    internal static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
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

    internal static async Task EnsureAv1anRuntimeReadyAsync(string av1anPath, AppLanguage language, CancellationToken cancellationToken)
    {
        using var process = CreateProcess(av1anPath, ["--version"]);
        using (ErrorDialogSuppression.Enter())
        {
            process.Start();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode == 0)
        {
            return;
        }

        var message = BuildRuntimeFailureMessage(language, process.ExitCode, output, error);
        throw new InvalidOperationException(message);
    }

    internal static string BuildRuntimeFailureMessage(AppLanguage language, int exitCode, string output, string error)
    {
        var stderr = (error ?? string.Empty).Trim();
        var stdout = (output ?? string.Empty).Trim();
        var merged = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : string.IsNullOrWhiteSpace(stdout)
                ? stderr
                : $"{stderr}{Environment.NewLine}{stdout}";

        if (exitCode == unchecked((int)0xC0000135) || exitCode == -1073741515)
        {
            return T(language, "Av1an failed to start because runtime dependencies are missing, commonly VSScript.dll or the VapourSynth runtime. Install or repair VapourSynth and try again.", "Av1an 启动失败：缺少运行时依赖（常见为 VSScript.dll / VapourSynth 运行时）。请安装或修复 VapourSynth 后重试。");
        }

        if (merged.Contains("Failed to get VSScript API", StringComparison.OrdinalIgnoreCase))
        {
            return T(language, "Av1an failed to start because the VSScript API is unavailable. The current VapourSynth runtime is incompatible with Av1an or damaged. Reinstall VapourSynth and confirm vspipe works before trying again.", "Av1an 启动失败：检测到 VSScript API 不可用。当前 VapourSynth 运行时与 Av1an 不兼容或安装损坏，请重装 VapourSynth（并确认 vspipe 可正常运行）后重试。");
        }

        if (!string.IsNullOrWhiteSpace(merged))
        {
            return T(language, $"Av1an preflight failed (exit code {exitCode}): {merged}", $"Av1an 预检失败（退出码 {exitCode}）：{merged}");
        }

        return T(language, $"Av1an preflight failed (exit code {exitCode}).", $"Av1an 预检失败，退出码 {exitCode}。");
    }

    private AppLanguage GetLanguage() => _settingsService.Load().Language;

    private static string T(AppLanguage language, string en, string zh) =>
        language == AppLanguage.English ? en : zh;
}
