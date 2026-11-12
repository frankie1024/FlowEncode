using System.Runtime.Versioning;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass, SupportedOSPlatform("windows")]
[TestCategory("ExternalToolSmoke")]
public sealed class ToolchainRuntimeSmokeTests
{
    private string Root => Environment.GetEnvironmentVariable("FLOWENCODE_COMPAT_TEST_ROOT")
        ?? throw new AssertInconclusiveException("Set FLOWENCODE_COMPAT_TEST_ROOT to a prepared compatibility fixture directory.");
    private string Probe => Directory.GetFiles(Path.Combine(Root, "tools", "ffmpeg-9.0.2-full_build-shared"), "ffprobe.exe", SearchOption.AllDirectories).Single();
    private string Ffmpeg => Path.Combine(Path.GetDirectoryName(Probe)!, "ffmpeg.exe");
    private string Encoder(EncoderKind kind, bool old = false) => kind switch
    {
        EncoderKind.X264 => Environment.GetEnvironmentVariable("FLOWENCODE_COMPAT_X264")
            ?? throw new AssertInconclusiveException("Set FLOWENCODE_COMPAT_X264 to a multilib x264 executable."),
        EncoderKind.X265 => old ? Path.Combine(Root, "tools", "x265-4.3-x64", "x265.exe")
            : Environment.GetEnvironmentVariable("FLOWENCODE_COMPAT_X265") ?? Path.Combine(Root, "script-build", "windows-x64", "Release", "x265.exe"),
        _ => Directory.GetFiles(Path.Combine(Root, "tools", "svt-av1-4.3.0-x64"), "SvtAv1EncApp.exe", SearchOption.AllDirectories).Single()
    };

    [TestMethod]
    [DataRow(EncoderKind.X264, 8, 32.0)] [DataRow(EncoderKind.X264, 10, 32.0)]
    [DataRow(EncoderKind.X265, 8, 32.0)] [DataRow(EncoderKind.X265, 10, 32.0)]
    [DataRow(EncoderKind.SvtAv1, 8, 32.0)] [DataRow(EncoderKind.SvtAv1, 10, 32.0)]
    [DataRow(EncoderKind.SvtAv1, 10, 32.25)]
    public async Task RealRunner_PreservesDepthAndCompletesQpEncoding(EncoderKind kind, int depth, double quality)
    {
        var testDirectory = Path.Combine(Root, "runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var profile = ToolchainCompatibilityTests.Profile(kind) with
        {
            Preset = kind == EncoderKind.SvtAv1 ? "10" : "ultrafast", Quality = quality,
            OutputContainer = kind == EncoderKind.X264 ? "264" : kind == EncoderKind.X265 ? "hevc" : "ivf",
            AdditionalArguments = kind == EncoderKind.SvtAv1 ? "--lp 2" : ""
        };
        var request = new EncodingJobRequest(Guid.NewGuid(), profile, Path.Combine(Root, $"source{depth}.y4m"),
            Path.Combine(testDirectory, "result." + profile.OutputContainer), InputPipelineKind.Y4mFile, EncoderArchitecture.X64);
        var runner = Runner(kind, Encoder(kind), testDirectory);
        var display = runner.BuildDisplayCommand(request);
        var result = await runner.RunAsync(request);
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        if (kind == EncoderKind.SvtAv1)
        {
            StringAssert.Contains(display, EncoderCliParameters.BuildSvtQpArguments(quality));
            StringAssert.Contains(result.Log, "CQP /");
        }
        else StringAssert.Contains(display, "--output-depth " + depth);
        EncodedMediaVerifier.VerifyVideo(Probe, request.OutputPath, new VideoOutputContract(kind, depth, "420", 24), default);
    }

    [TestMethod]
    public async Task RealRunner_Old8BitX265IsBlockedBefore10BitJobStarts()
    {
        var directory = Path.Combine(Root, "runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var profile = ToolchainCompatibilityTests.Profile(EncoderKind.X265) with { Preset = "ultrafast", OutputContainer = "hevc" };
        var request = new EncodingJobRequest(Guid.NewGuid(), profile, Path.Combine(Root, "source10.y4m"),
            Path.Combine(directory, "result.hevc"), InputPipelineKind.Y4mFile, EncoderArchitecture.X64);
        var runner = Runner(EncoderKind.X265, Encoder(EncoderKind.X265, old: true), directory);
        var result = await runner.RunAsync(request);
        Assert.AreEqual(EncodingJobState.Failed, result.State, result.Log);
        StringAssert.Contains(result.Log, "10-bit");
        Assert.IsFalse(File.Exists(request.OutputPath));
    }

    [TestMethod]
    public void StableFfmpeg_ProvidesPairAndRequiredWorkflowCapabilities()
    {
        FfmpegCapabilityProbe.ValidatePair(Ffmpeg, Probe);
        FfmpegCapabilityProbe.RequireVideo(Ffmpeg);
        FfmpegCapabilityProbe.RequireOpus(Ffmpeg, true, default);
        FfmpegCapabilityProbe.RequireOpus(Ffmpeg, false, default);
    }

    private LocalEncodingJobRunner Runner(EncoderKind kind, string encoder, string directory)
    {
        var paths = new LocalAppPaths(Path.Combine(directory, "state"), Path.Combine(directory, "install"), [directory]);
        var settings = new Settings(AppSettings.Default with
        {
            Language = AppLanguage.English,
            ManualToolPaths = new Dictionary<string, string>
            {
                [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Ffmpeg)] = Ffmpeg,
                [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Ffprobe)] = Probe
            }
        });
        return new LocalEncodingJobRunner(paths, new Discovery(kind, encoder), settings);
    }

    private sealed class Settings(AppSettings settings) : IAppSettingsService
    {
        public AppSettings Load() => settings;
        public void Save(AppSettings value) => settings = value;
    }
    private sealed class Discovery(EncoderKind kind, string path) : IEncoderDiscoveryService
    {
        public DiscoveredEncoderBinary? ResolveEncoder(EncoderKind value, EncoderArchitecture architecture, bool preferSystem) =>
            value == kind ? new(kind, EncoderArchitecture.X64, path, EncoderBinarySource.Path, "test", "test") : null;
        public IReadOnlyList<DiscoveredEncoderBinary> DiscoverSystemBinaries() => [ResolveEncoder(kind, EncoderArchitecture.X64, false)!];
        public void InvalidateCache() { }
    }
}
