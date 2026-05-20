using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class CliAudioProcessingRunnerTests
{
    [TestMethod]
    public void BuildDisplayCommand_WhenOpusUsesDefaultPipeline_UsesOpusencPipe()
    {
        var runner = CreateRunner();
        var request = CreateRequest(useOpusMappingFamily1: false);

        var command = runner.BuildDisplayCommand(request);

        StringAssert.Contains(command, "ffmpeg.exe");
        StringAssert.Contains(command, "opusenc.exe");
        StringAssert.Contains(command, "-f wav pipe:1 |");
        Assert.IsFalse(command.Contains("--quiet", StringComparison.Ordinal));
        Assert.IsFalse(command.Contains("-loglevel error", StringComparison.Ordinal));
        Assert.IsFalse(command.Contains("-mapping_family 1", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildDisplayCommand_WhenOpusMappingFamilyIsEnabled_UsesFfmpegLibopus()
    {
        var runner = CreateRunner();
        var request = CreateRequest(useOpusMappingFamily1: true, channelLayout: "5.1", channelCount: 6);

        var command = runner.BuildDisplayCommand(request);

        StringAssert.Contains(command, "ffmpeg.exe");
        StringAssert.Contains(command, "-c:a libopus");
        StringAssert.Contains(command, "-mapping_family 1");
        Assert.IsFalse(command.Contains("-loglevel error", StringComparison.Ordinal));
        Assert.IsFalse(command.Contains("opusenc.exe", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildDisplayCommand_WhenOpusMappingFamilyIsEnabledForSideSurround_NormalizesLayout()
    {
        var runner = CreateRunner();
        var request = CreateRequest(useOpusMappingFamily1: true, channelLayout: "5.1(side)", channelCount: 6);

        var command = runner.BuildDisplayCommand(request);

        StringAssert.Contains(command, "-filter:a");
        StringAssert.Contains(command, "channelmap=map=FL-FL|FR-FR|FC-FC|LFE-LFE|SL-BL|SR-BR:channel_layout=5.1");
        StringAssert.Contains(command, "-mapping_family 1");
        Assert.IsFalse(command.Contains("opusenc.exe", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildDisplayCommand_WhenOpusMappingFamilyIsEnabledForUnsupportedLayout_FallsBackToOpusencPipe()
    {
        var runner = CreateRunner();
        var request = CreateRequest(useOpusMappingFamily1: true, channelLayout: "4.0", channelCount: 4);

        var command = runner.BuildDisplayCommand(request);

        StringAssert.Contains(command, "opusenc.exe");
        StringAssert.Contains(command, "-f wav pipe:1 |");
        Assert.IsFalse(command.Contains("-mapping_family 1", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildDisplayCommand_WhenDdpRequested_IncludesNoPromptFlag()
    {
        var runner = CreateRunner();
        var request = CreateDdpRequest(
            sourcePath: @"D:\audio\input.wav",
            outputDirectory: @"D:\audio\ddp-out");

        var command = runner.BuildDisplayCommand(request);

        StringAssert.Contains(command, "deew.exe");
        StringAssert.Contains(command, "-np");
        StringAssert.Contains(command, "-o");
    }

    [TestMethod]
    public void CreateOpusPipelineStartInfos_WhenPathsContainChinese_UsesDirectProcessArguments()
    {
        var request = CreateRequest(
            useOpusMappingFamily1: false,
            sourcePath: @"Z:\cmct发布\audio\2-中影公映国配-zh.dts",
            outputPath: @"Z:\cmct发布\audio\2-中影公映国配-zh.opus");

        var startInfos = CliAudioProcessingRunner.CreateOpusPipelineStartInfos(
            request,
            @"C:\工具\ffmpeg.exe",
            @"C:\工具\opusenc.exe",
            @"C:\临时目录\progress.log");

        Assert.AreEqual(@"C:\工具\ffmpeg.exe", startInfos.FfmpegStartInfo.FileName);
        Assert.AreEqual(@"C:\工具\opusenc.exe", startInfos.OpusEncoderStartInfo.FileName);
        CollectionAssert.Contains(startInfos.FfmpegStartInfo.ArgumentList, request.SourcePath);
        CollectionAssert.Contains(startInfos.FfmpegStartInfo.ArgumentList, @"C:\临时目录\progress.log");
        CollectionAssert.Contains(startInfos.OpusEncoderStartInfo.ArgumentList, "--ignorelength");
        CollectionAssert.Contains(startInfos.OpusEncoderStartInfo.ArgumentList, request.OutputPath);
        Assert.AreEqual(string.Empty, startInfos.FfmpegStartInfo.Arguments);
        Assert.AreEqual(string.Empty, startInfos.OpusEncoderStartInfo.Arguments);
    }

    [TestMethod]
    public void CreateRunPlan_WhenOpusRequested_StagesOutputUntilSuccess()
    {
        var request = CreateRequest(
            useOpusMappingFamily1: false,
            outputPath: @"D:\audio\输出 文件.opus");

        var runPlan = CliAudioProcessingRunner.CreateRunPlan(request);

        Assert.AreEqual(request.OutputPath, runPlan.DisplayRequest.OutputPath);
        Assert.AreNotEqual(request.OutputPath, runPlan.ExecutionRequest.OutputPath);
        Assert.AreEqual(runPlan.ExecutionRequest.OutputPath, runPlan.StagedOutputPath);
        Assert.IsFalse(string.IsNullOrWhiteSpace(runPlan.StagingDirectory));
        StringAssert.Contains(runPlan.ExecutionRequest.OutputPath, ".staging.tmp.opus");
        StringAssert.Contains(runPlan.ExecutionRequest.OutputPath, ".flowencode-temp");
    }

    [TestMethod]
    public void CreateRunPlan_WhenEac3ToRequested_StagesOutputFile()
    {
        var request = new AudioProcessingRequest(
            Guid.NewGuid(),
            @"D:\audio\input.thd",
            @"D:\audio\movie.flac",
            AudioProcessingMode.Eac3To,
            AudioEac3ToOutputFormat.Flac,
            [],
            null,
            null,
            null,
            null,
            false);

        var runPlan = CliAudioProcessingRunner.CreateRunPlan(request);

        Assert.AreEqual(request.OutputPath, runPlan.DisplayRequest.OutputPath);
        Assert.AreNotEqual(request.OutputPath, runPlan.ExecutionRequest.OutputPath);
        Assert.AreEqual(runPlan.ExecutionRequest.OutputPath, runPlan.StagedOutputPath);
        Assert.IsFalse(string.IsNullOrWhiteSpace(runPlan.StagingDirectory));
        StringAssert.Contains(runPlan.ExecutionRequest.OutputPath, ".flowencode-temp");
        StringAssert.Contains(runPlan.ExecutionRequest.OutputPath, ".staging.tmp.flac");
    }

    [TestMethod]
    public void CreateRunPlan_WhenDdpRequested_StagesOutputDirectory()
    {
        var request = CreateDdpRequest(
            sourcePath: @"D:\audio\input.wav",
            outputDirectory: @"D:\audio\ddp-out");

        var runPlan = CliAudioProcessingRunner.CreateRunPlan(request);

        Assert.AreEqual(request.OutputPath, runPlan.DisplayRequest.OutputPath);
        Assert.AreNotEqual(request.OutputPath, runPlan.ExecutionRequest.OutputPath);
        Assert.IsNull(runPlan.StagedOutputPath);
        Assert.AreEqual(runPlan.ExecutionRequest.OutputPath, runPlan.StagingDirectory);
        StringAssert.Contains(runPlan.ExecutionRequest.OutputPath, ".flowencode-temp");
        Assert.IsFalse(
            runPlan.ExecutionRequest.OutputPath.StartsWith(
                request.OutputPath + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            "DDP staging should not be created inside the final output directory.");
    }

    [TestMethod]
    public async Task RunAsync_Eac3To_WithInvalidAdditionalArguments_FailsInsteadOfCancelling()
    {
        var sourcePath = EnsureSmokeWav();
        var outputPath = Path.Combine(AudioSmokeRoot, "invalid-eac3to.flac");
        var runner = CreateRunner(new StubToolProbeService(RequireDetectedPath("eac3to.exe")));
        var request = new AudioProcessingRequest(
            Guid.NewGuid(),
            sourcePath,
            outputPath,
            AudioProcessingMode.Eac3To,
            AudioEac3ToOutputFormat.Flac,
            ["--definitely-invalid-option"],
            null,
            null,
            null,
            null,
            false);

        var result = await runner.RunAsync(request);

        Assert.AreEqual(EncodingJobState.Failed, result.State);
        Assert.AreNotEqual(-1, result.ExitCode);
        Assert.IsTrue(
            result.Log.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || result.Log.Contains("error", StringComparison.OrdinalIgnoreCase)
            || result.Log.Contains("unknown", StringComparison.OrdinalIgnoreCase),
            $"Expected error output in log, actual log:{Environment.NewLine}{result.Log}");
        Assert.IsFalse(File.Exists(outputPath) && new FileInfo(outputPath).Length == 0);
    }

    [TestMethod]
    public async Task RunAsync_Ddp_WithStereoSource_CompletesWithoutPromptOrEofError()
    {
        var sourcePath = EnsureStereoSmokeWav();
        var outputDirectory = Path.Combine(AudioSmokeRoot, "ddp-stereo-no-prompt");
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        Directory.CreateDirectory(outputDirectory);
        var runner = CreateRunner(new StubToolProbeService(new Dictionary<RegisteredToolKind, string>
        {
            [RegisteredToolKind.Deew] = RequireDetectedPath("deew.exe"),
            [RegisteredToolKind.Dee] = RequireDetectedPath("dee.exe"),
            [RegisteredToolKind.Ffmpeg] = RequireDetectedPath("ffmpeg.exe"),
            [RegisteredToolKind.Ffprobe] = RequireDetectedPath("ffprobe.exe")
        }));

        var request = CreateDdpRequest(sourcePath, outputDirectory);

        var result = await runner.RunAsync(request);

        Assert.AreEqual(EncodingJobState.Completed, result.State);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(
            result.Log.Contains("consider using", StringComparison.OrdinalIgnoreCase)
            || result.Log.Contains("stereo", StringComparison.OrdinalIgnoreCase)
            || result.Log.Contains("mono", StringComparison.OrdinalIgnoreCase),
            $"Expected DDP warning details in log, actual log:{Environment.NewLine}{result.Log}");
        Assert.IsFalse(result.Log.Contains("[y/n]", StringComparison.OrdinalIgnoreCase), "DDP log still contains an interactive prompt.");
        Assert.IsFalse(result.Log.Contains("are you sure", StringComparison.OrdinalIgnoreCase), "DDP log still contains an interactive confirmation question.");
        Assert.IsFalse(result.Log.Contains("EOFError", StringComparison.OrdinalIgnoreCase), "DDP log still contains the old non-interactive EOFError.");
        Assert.IsTrue(
            Directory.EnumerateFiles(outputDirectory, "*.ec3").Any(file => new FileInfo(file).Length > 0),
            "Expected DDP output file was not created.");
        Assert.IsFalse(Directory.EnumerateFiles(outputDirectory).Any(file => new FileInfo(file).Length == 0));
    }

    [TestMethod]
    public async Task RunAsync_Ddp_WithStereoSource_ReportsDeeProgressUpdates()
    {
        var sourcePath = EnsureStereoSmokeWav();
        var outputDirectory = Path.Combine(AudioSmokeRoot, "ddp-stereo-progress");
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        Directory.CreateDirectory(outputDirectory);
        var runner = CreateRunner(new StubToolProbeService(new Dictionary<RegisteredToolKind, string>
        {
            [RegisteredToolKind.Deew] = RequireDetectedPath("deew.exe"),
            [RegisteredToolKind.Dee] = RequireDetectedPath("dee.exe"),
            [RegisteredToolKind.Ffmpeg] = RequireDetectedPath("ffmpeg.exe"),
            [RegisteredToolKind.Ffprobe] = RequireDetectedPath("ffprobe.exe")
        }));

        var request = CreateDdpRequest(sourcePath, outputDirectory);
        var progress = new RecordingProgress<AudioProcessingProgress>();

        var result = await runner.RunAsync(request, progress);

        var progressUpdates = progress.Snapshot();
        Assert.AreEqual(EncodingJobState.Completed, result.State);
        Assert.IsTrue(
            progressUpdates.Any(update => update.ProgressFraction is > 0 and < 1),
            "Expected at least one in-flight DEE progress update.");
        Assert.IsTrue(
            progressUpdates.Any(update =>
                !string.IsNullOrWhiteSpace(update.Summary)
                && update.Summary.Contains("DEE", StringComparison.OrdinalIgnoreCase)
                && update.ProgressFraction is > 0 and < 1),
            "Expected DEE progress updates to surface DEE stage summary.");
    }

    [TestMethod]
    public void NormalizeDisplayLineForTesting_DdpSwitchesFromWarmupToDeeStage()
    {
        var runner = CreateRunner();
        var request = CreateDdpRequest(@"D:\audio\input.wav", @"D:\audio\out");

        var warmupDisplay = runner.NormalizeDisplayLineForTesting(
            request,
            "ffmpeg -y -drc_scale 0 -i [input] ...",
            parsedProgress: null,
            deewPhase: "FfmpegPreparation");
        var deeDisplay = runner.NormalizeDisplayLineForTesting(
            request,
            "Running the following commands:",
            parsedProgress: null,
            deewPhase: "DeeEncoding");

        StringAssert.Contains(warmupDisplay, "FFmpeg", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(deeDisplay, "DEE", StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(deeDisplay.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ParseDeewProgressForTesting_WithRichFfmpegProgressLine_ReturnsNull()
    {
        const string line = "[ ffmpeg | demo.wav                           ] ----------------------- 100.00%";

        var parsed = CliAudioProcessingRunner.ParseDeewProgressForTesting(line);

        Assert.IsNull(parsed);
    }

    [TestMethod]
    public void ParseDeewProgressForTesting_WithRichDeeProgressLine_ParsesPercent()
    {
        const string line = "[ DEE: encode | demo.wav      (-18 dB) ] ------ ----------------- 25.00%";

        var parsed = CliAudioProcessingRunner.ParseDeewProgressForTesting(line);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(0.25, parsed.Value, 0.000001);
    }

    [TestMethod]
    public void ParseDeewProgressForTesting_WithAnsiRichDeeProgressLine_ParsesPercent()
    {
        const string line = "\u001b[38;5;39m[ DEE: measure | demo.wav ] ------------------------- 17.43%\u001b[0m";

        var parsed = CliAudioProcessingRunner.ParseDeewProgressForTesting(line);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(0.1743, parsed.Value, 0.000001);
    }

    [TestMethod]
    public void ParseDeewProgressForTesting_WithRealDeeProgressLine_ParsesPercent()
    {
        const string line = "[ DEE: measure | 5280 PID 4352 48000 8ch e... ] - ----------------------- 6.25%";

        var parsed = CliAudioProcessingRunner.ParseDeewProgressForTesting(line);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(0.0625, parsed.Value, 0.000001);
    }

    [TestMethod]
    public void TryBuildDdpSemanticFailureDetailForTesting_WithPcmChannelFailure_ReturnsDetail()
    {
        const string log = """
pcm_to_ddp: Check input and downmix config. Resulting output channels: 8 is not
allowed. Valid value(s): 1,2,6.
pcm_to_ddp: Check input and downmix config. Resulting output channels: 8 is not
allowed. Valid value(s): 1,2,6.
""";

        var detail = CliAudioProcessingRunner.TryBuildDdpSemanticFailureDetailForTesting(log);

        Assert.IsFalse(string.IsNullOrWhiteSpace(detail));
        StringAssert.Contains(detail!, "pcm_to_ddp:");
        StringAssert.Contains(detail, "Resulting output channels: 8 is not allowed");
        Assert.AreEqual(1, CountOccurrences(detail, "pcm_to_ddp:"));
    }

    [TestMethod]
    public void TryBuildDdpSemanticFailureDetailForTesting_WithNeutralWarning_ReturnsNull()
    {
        const string log = "Dolby Encoding Engine Wrapper started.";

        var detail = CliAudioProcessingRunner.TryBuildDdpSemanticFailureDetailForTesting(log);

        Assert.IsNull(detail);
    }

    [TestMethod]
    public void ParseOpusProgressForTesting_WithOutTimeAndDuration_ParsesPercent()
    {
        var parsed = CliAudioProcessingRunner.ParseOpusProgressForTesting("out_time=00:01:00.000000", 120d);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(0.5, parsed.Value, 0.000001);
    }

    [TestMethod]
    public void ParseOpusProgressForTesting_WithOutTimeUsAndDuration_ParsesPercent()
    {
        var parsed = CliAudioProcessingRunner.ParseOpusProgressForTesting("out_time_us=30000000", 120d);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(0.25, parsed.Value, 0.000001);
    }

    [TestMethod]
    public void ParseOpusProgressForTesting_WithoutDuration_ReturnsNull()
    {
        var parsed = CliAudioProcessingRunner.ParseOpusProgressForTesting("out_time=00:01:00.000000", null);

        Assert.IsNull(parsed);
    }

    [TestMethod]
    public async Task RunAsync_OpusWithoutRequestDuration_ReportsProgressFromRuntimeProbe()
    {
        var sourcePath = EnsureStereoSmokeWav();
        var outputPath = Path.Combine(AudioSmokeRoot, "opus-runtime-duration.opus");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var runner = CreateRunner(new StubToolProbeService(new Dictionary<RegisteredToolKind, string>
        {
            [RegisteredToolKind.Ffmpeg] = RequireDetectedPath("ffmpeg.exe"),
            [RegisteredToolKind.Ffprobe] = RequireDetectedPath("ffprobe.exe"),
            [RegisteredToolKind.OpusExt] = RequireDetectedPath("opusenc.exe")
        }));
        var request = CreateRequest(
            useOpusMappingFamily1: false,
            sourcePath: sourcePath,
            outputPath: outputPath,
            channelCount: 2,
            channelLayout: "stereo",
            sourceDurationSeconds: null);
        var progress = new RecordingProgress<AudioProcessingProgress>();

        var result = await runner.RunAsync(request, progress);

        var progressUpdates = progress.Snapshot();
        Assert.AreEqual(EncodingJobState.Completed, result.State);
        Assert.IsTrue(File.Exists(outputPath) && new FileInfo(outputPath).Length > 0);
        Assert.IsTrue(
            progressUpdates.Any(update => update.ProgressFraction is > 0 and < 1),
            "Expected Opus progress updates after runtime duration probing.");
    }

    private static readonly string AudioSmokeRoot = Path.Combine(
        Path.GetTempPath(),
        "FlowEncodeAudioSmoke");

    private static CliAudioProcessingRunner CreateRunner(StubToolProbeService? toolProbeService = null) =>
        new(toolProbeService ?? new StubToolProbeService(), new StubSettingsService());

    private static AudioProcessingRequest CreateRequest(
        bool useOpusMappingFamily1,
        string sourcePath = @"D:\audio\input.thd",
        string outputPath = @"D:\audio\output.opus",
        int? channelCount = 6,
        string? channelLayout = "5.1",
        double? sourceDurationSeconds = 120d)
    {
        return new AudioProcessingRequest(
            Guid.NewGuid(),
            sourcePath,
            outputPath,
            AudioProcessingMode.Opus,
            null,
            [],
            sourceDurationSeconds,
            channelCount,
            channelLayout,
            384,
            useOpusMappingFamily1);
    }

    private static AudioProcessingRequest CreateDdpRequest(
        string sourcePath,
        string outputDirectory)
    {
        return new AudioProcessingRequest(
            Guid.NewGuid(),
            sourcePath,
            outputDirectory,
            AudioProcessingMode.Ddp,
            null,
            [],
            1d,
            2,
            "stereo",
            null,
            false);
    }

    private static string EnsureSmokeWav()
    {
        Directory.CreateDirectory(AudioSmokeRoot);
        var sourcePath = Path.Combine(AudioSmokeRoot, "tiny.wav");
        if (File.Exists(sourcePath) && new FileInfo(sourcePath).Length > 0)
        {
            return sourcePath;
        }

        var ffmpegPath = RequireDetectedPath("ffmpeg.exe");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=1000:duration=1",
                "-c:a",
                "pcm_s16le",
                sourcePath
            }
        }) ?? throw new InvalidOperationException("Failed to start ffmpeg for audio smoke asset generation.");

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"Failed to generate smoke wav. ExitCode={process.ExitCode}. {stderr}");
        }

        return sourcePath;
    }

    private static string EnsureStereoSmokeWav()
    {
        Directory.CreateDirectory(AudioSmokeRoot);
        var sourcePath = Path.Combine(AudioSmokeRoot, "tiny-stereo.wav");
        if (File.Exists(sourcePath) && new FileInfo(sourcePath).Length > 0)
        {
            return sourcePath;
        }

        var ffmpegPath = RequireDetectedPath("ffmpeg.exe");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=1000:duration=1",
                "-filter_complex",
                "[0:a]pan=stereo|c0=c0|c1=c0[a]",
                "-map",
                "[a]",
                "-c:a",
                "pcm_s16le",
                sourcePath
            }
        }) ?? throw new InvalidOperationException("Failed to start ffmpeg for stereo audio smoke asset generation.");

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"Failed to generate stereo smoke wav. ExitCode={process.ExitCode}. {stderr}");
        }

        return sourcePath;
    }

    private static string GetDetectedPath(string executableName)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "where.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { executableName }
        }) ?? throw new InvalidOperationException($"Failed to locate {executableName}.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to locate {executableName}. {stderr}");
        }

        var path = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"No path returned for {executableName}.");
        }

        return path;
    }

    private static string RequireDetectedPath(string executableName)
    {
        try
        {
            return GetDetectedPath(executableName);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"Smoke test requires {executableName}: {ex.Message}");
            throw;
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;

        while ((startIndex = text.IndexOf(value, startIndex, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }

    private sealed class StubToolProbeService : IToolProbeService
    {
        private readonly Dictionary<RegisteredToolKind, string> _paths;

        public StubToolProbeService()
            : this(new Dictionary<RegisteredToolKind, string>())
        {
        }

        public StubToolProbeService(string eac3toPath)
            : this(new Dictionary<RegisteredToolKind, string>
            {
                [RegisteredToolKind.Eac3To] = eac3toPath
            })
        {
        }

        public StubToolProbeService(Dictionary<RegisteredToolKind, string> paths)
            : this(paths, clonePaths: true)
        {
        }

        private StubToolProbeService(Dictionary<RegisteredToolKind, string> paths, bool clonePaths)
        {
            _paths = clonePaths
                ? new Dictionary<RegisteredToolKind, string>(paths)
                : paths;
        }

        public Task<IReadOnlyList<ToolProbeResult>> ProbeAllAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ToolProbeResult> ProbeAsync(RegisteredToolKind kind, CancellationToken cancellationToken = default)
        {
            if (_paths.TryGetValue(kind, out var path))
            {
                return Task.FromResult(new ToolProbeResult(
                    kind,
                    ReadinessState.Ready,
                    ToolDetectionSource.Path,
                    "test",
                    path,
                    string.Empty,
                    string.Empty,
                    string.Empty));
            }

            throw new NotSupportedException();
        }

        public void InvalidateCache()
        {
        }
    }

    private sealed class StubSettingsService : IAppSettingsService
    {
        public AppSettings Load() => AppSettings.Default with { Language = AppLanguage.English };

        public void Save(AppSettings settings)
        {
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        private readonly ConcurrentQueue<T> _updates = new();

        public void Report(T value)
        {
            _updates.Enqueue(value);
        }

        public T[] Snapshot()
        {
            return _updates.ToArray();
        }
    }
}
