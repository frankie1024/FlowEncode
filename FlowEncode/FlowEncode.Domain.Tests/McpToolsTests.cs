using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Infrastructure.Automation;
using FlowEncode.Mcp;
using FlowEncode.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class McpToolsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task StartEncodingJob_QueuesTheRequestInTheDesktopApp(bool overwrite, bool startImmediately)
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"flowencode-source-{Guid.NewGuid():N}.y4m");
        var outputPath = Path.ChangeExtension(sourcePath, ".264");
        File.WriteAllText(sourcePath, "source");
        EnqueueEncodingJobParams? received = null;
        await using var server = DesktopAutomationTests.StartServer((method, parameters) => method switch
        {
            DesktopAutomationMethods.AppStatus => Task.FromResult<object?>(CreateStatus()),
            DesktopAutomationMethods.QueueEnqueue => Task.FromResult<object?>(CreateSnapshot(
                (received = parameters!.Value.Deserialize<EnqueueEncodingJobParams>(DesktopAutomationProtocol.JsonOptions)!).Request)),
            _ => throw new DesktopAutomationException($"unexpected {method}")
        });

        try
        {
            var snapshot = await EncodingTools.StartEncodingJob(
                CreateBridge(server), new EmptyProfileLibrary(), sourcePath, outputPath,
                encoder: "x264", overwrite: overwrite, startImmediately: startImmediately).WaitAsync(Timeout);

            Assert.IsNotNull(received);
            Assert.AreEqual(overwrite, received.Request.OverwriteOutput);
            Assert.AreEqual(startImmediately, received.StartImmediately);
            Assert.AreEqual(outputPath, received.Request.OutputPath);
            Assert.AreEqual(EncoderKind.X264, received.Request.Profile.Kind);
            Assert.AreEqual(received.Request.JobId, snapshot.JobId);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [TestMethod]
    public async Task QueueTools_WhenDesktopIsNotRunning_ExplainInsteadOfLaunching()
    {
        var bridge = new DesktopBridge(
            new DesktopAutomationClient($"FlowEncode.Automation.Test.{Guid.NewGuid():N}"),
            NullLogger<DesktopBridge>.Instance);

        var error = await Assert.ThrowsExactlyAsync<McpException>(() => EncodingTools.ListEncodingJobs(bridge));

        StringAssert.Contains(error.Message, "not running");
    }

    [TestMethod]
    public async Task DesktopErrors_ReachTheClientAsToolErrors()
    {
        await using var server = DesktopAutomationTests.StartServer((_, _) => throw new DesktopAutomationException("Job abc is Running; only queued jobs can be reordered."));

        var error = await Assert.ThrowsExactlyAsync<McpException>(
            () => EncodingTools.MoveEncodingJob(CreateBridge(server), Guid.NewGuid().ToString(), 0));

        StringAssert.Contains(error.Message, "only queued jobs");
    }

    [TestMethod]
    public void McpEnum_AcceptsLooseSpellings()
    {
        Assert.AreEqual(AutoCompressionMetric.ButteraugliInf, McpEnum.Parse<AutoCompressionMetric>("butteraugli-inf", "metric"));
        Assert.AreEqual(SetupDependencyKind.FfmpegBundle, McpEnum.Parse<SetupDependencyKind>("ffmpeg_bundle", "dependency"));
        Assert.AreEqual(QueueCompletionAction.Shutdown, McpEnum.Parse<QueueCompletionAction>("SHUTDOWN", "action"));
        Assert.IsNull(McpEnum.ParseOptional<AppLanguage>(" ", "language"));
        Assert.ThrowsExactly<McpException>(() => McpEnum.Parse<AppLanguage>("klingon", "language"));
    }

    [TestMethod]
    public void ParseModule_AcceptsAliases()
    {
        Assert.AreEqual(DesktopAutomationModules.BluRay, AppTools.ParseModule("Blu-ray"));
        Assert.AreEqual(DesktopAutomationModules.AutoCompression, AppTools.ParseModule("auto_compression"));
        Assert.ThrowsExactly<McpException>(() => AppTools.ParseModule("video"));
    }

    [TestMethod]
    public async Task AudioRequest_DefaultsOpusSettingsFromTheSource()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"flowencode-audio-{Guid.NewGuid():N}.thd");
        File.WriteAllText(sourcePath, "audio");
        try
        {
            var surround = new FixedAudioInfo(new AudioSourceInfo("truehd", "", 8, "7.1", 48000, 24, 120.0));
            var request = await AudioTools.BuildRequestAsync(surround, sourcePath, "opus", null, null, null, null, CancellationToken.None);

            Assert.AreEqual(AudioProcessingMode.Opus, request.Mode);
            Assert.AreEqual(384, request.OpusBitrateKbps);
            Assert.IsTrue(request.UseOpusMappingFamily1);
            Assert.AreEqual(Path.ChangeExtension(sourcePath, ".opus"), request.OutputPath);
            Assert.AreEqual(8, request.SourceChannelCount);

            var flac = await AudioTools.BuildRequestAsync(surround, sourcePath, "FLAC", null, null, null, "-down16 -no2ndpass", CancellationToken.None);
            Assert.AreEqual(AudioEac3ToOutputFormat.Flac, flac.Eac3ToOutputFormat);
            CollectionAssert.AreEqual(new[] { "-down16", "-no2ndpass" }, flac.Eac3ToAdditionalArguments.ToArray());

            await Assert.ThrowsExactlyAsync<McpException>(
                () => AudioTools.BuildRequestAsync(surround, sourcePath, "opus", null, 128, null, null, CancellationToken.None));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [TestMethod]
    public async Task AudioRequest_RejectsVideoContainersForEac3To()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"flowencode-audio-{Guid.NewGuid():N}.mkv");
        File.WriteAllText(sourcePath, "video");
        try
        {
            var error = await Assert.ThrowsExactlyAsync<McpException>(
                () => AudioTools.BuildRequestAsync(new FixedAudioInfo(null), sourcePath, "ac3", null, null, null, null, CancellationToken.None));
            StringAssert.Contains(error.Message, "Demux");
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [TestMethod]
    public void BluRayTrackSelection_SupportsDefaultsAllAndExplicitIds()
    {
        IReadOnlyList<BluRayTrackItem> tracks =
        [
            new("2", 2, "2:", BluRayTrackKind.Video, "HEVC", "", "", true),
            new("5", 5, "5:", BluRayTrackKind.Subtitle, "PGS", "", "Chinese", false),
            new("3", 3, "3:", BluRayTrackKind.Audio, "TrueHD", "", "English", true),
        ];

        CollectionAssert.AreEqual(new[] { "2", "3" }, BluRayTools.SelectTracks(tracks, null).Select(static track => track.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "2", "3", "5" }, BluRayTools.SelectTracks(tracks, ["all"]).Select(static track => track.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "3", "5" }, BluRayTools.SelectTracks(tracks, ["5", "3", "5"]).Select(static track => track.Id).ToArray());
        Assert.ThrowsExactly<McpException>(() => BluRayTools.SelectTracks(tracks, ["9"]));
    }

    [TestMethod]
    public void AutoCompressionSearchProfile_MatchesTheDesktopForm()
    {
        Assert.IsNull(AutoCompressionTools.BuildSearchProfile(null, null, null, 1));
        Assert.AreEqual(
            new AutoCompressionSearchProfile("natural-cubic-polynomial", "median", "1280x720", 2, false),
            AutoCompressionTools.BuildSearchProfile("Median", "natural-cubic-polynomial", "1280x720", 2));
        Assert.ThrowsExactly<McpException>(() => AutoCompressionTools.BuildSearchProfile(null, "pchip-linear", null, 1));
        Assert.ThrowsExactly<McpException>(() => AutoCompressionTools.BuildSearchProfile("percentile", null, null, 1));
    }

    [TestMethod]
    public void AutoCompressionRequest_UsesTheDesktopOutputName()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"flowencode-ac-{Guid.NewGuid():N}.mkv");
        File.WriteAllText(sourcePath, "video");
        try
        {
            var request = AutoCompressionTools.BuildRequest(sourcePath, "svt-av1", "vmaf", 95, 4, null, " --preset 4 ", null, 0, null, null, null, 1);

            Assert.AreEqual(EncoderKind.SvtAv1, request.EncoderKind);
            Assert.AreEqual(AutoCompressionMetric.Vmaf, request.Metric);
            Assert.AreEqual("--preset 4", request.VideoParameters);
            Assert.IsNull(request.Workers);
            Assert.AreEqual(
                AutoCompressionOutputPathPlanner.BuildOutputPath(sourcePath, Path.GetDirectoryName(sourcePath)!, EncoderKind.SvtAv1, AutoCompressionMetric.Vmaf, 95),
                request.OutputPath);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [TestMethod]
    public void VapourSynthFrameSelection_ResolvesTimecodesAndRejectsOutOfRangeFrames()
    {
        var output = new VapourSynthPreviewOutputInfo(0, "out", 1920, 1080, 1000, 24000, 1001, "YUV420P10", 10);

        Assert.AreEqual(240, VapourSynthTools.ResolveFrame(output, null, "00:00:10.010"));
        Assert.AreEqual(999, VapourSynthTools.ResolveFrame(output, null, "01:00:00"), "clamped to the last frame");
        Assert.AreEqual(5, VapourSynthTools.ResolveFrame(output, 5, null));
        Assert.ThrowsExactly<McpException>(() => VapourSynthTools.ResolveFrame(output, 1000, null));
    }

    [TestMethod]
    public void StarterScript_LoadsTheSourceAndSetsAnOutput()
    {
        var script = VapourSynthTools.BuildStarterScript(@"D:\视频\a.mkv", "bs.VideoSource");

        StringAssert.Contains(script, "clip = core.bs.VideoSource(r\"D:\\视频\\a.mkv\")");
        StringAssert.Contains(script, "clip.set_output(0)");
    }

    [TestMethod]
    public void ProfileResolver_ParsesEncoderAliases()
    {
        Assert.AreEqual(EncoderKind.SvtAv1, EncodingProfileResolver.ParseEncoder("SVT-AV1"));
        Assert.AreEqual(EncoderKind.X265, EncodingProfileResolver.ParseEncoder("hevc"));
        Assert.AreEqual(RateControlMode.TwoPass, EncodingProfileResolver.ParseRateControl("2-pass"));
        Assert.ThrowsExactly<McpException>(() => EncodingProfileResolver.ParseEncoder("vp9"));
    }

    [TestMethod]
    public async Task ProfileResolver_AppliesOverridesOnTopOfDefaultProfile()
    {
        var profile = await EncodingProfileResolver.ResolveAsync(
            new EmptyProfileLibrary(),
            templateId: null,
            encoder: "x264",
            new EncodingProfileOptions { Quality = 21.5, Preset = "slow", OutputContainer = ".mkv" },
            CancellationToken.None);

        var defaults = DefaultEncodingProfiles.GetDefault(EncoderKind.X264);
        Assert.AreEqual(21.5, profile.Quality);
        Assert.AreEqual("slow", profile.Preset);
        Assert.AreEqual("mkv", profile.OutputContainer);
        Assert.AreEqual(defaults.AdditionalArguments, profile.AdditionalArguments);
    }

    [TestMethod]
    public async Task ProfileResolver_WhenNeitherTemplateNorEncoder_Throws()
    {
        await Assert.ThrowsExactlyAsync<McpException>(() => EncodingProfileResolver.ResolveAsync(
            new EmptyProfileLibrary(),
            templateId: null,
            encoder: null,
            options: null,
            CancellationToken.None));
    }

    private static DesktopBridge CreateBridge(DesktopAutomationServer server) =>
        new(new DesktopAutomationClient(server.PipeName), NullLogger<DesktopBridge>.Instance);

    private static AutomationAppStatus CreateStatus() => new(
        "2.0.1", Environment.ProcessId, true, "Ready", 1, QueueCompletionAction.None,
        new AutomationQueueSummary(0, 0, 0, 0, 0, 0), [], false, @"C:\workspace");

    private static AutomationJobSnapshot CreateSnapshot(EncodingJobRequest request) => new(
        request.JobId, 0, EncodingJobState.Queued, null, "Queued", "", false, null, null, null, null, null, null,
        request.Profile.Name, request.Profile.Kind.ToDisplayName(), request.SourcePath, request.OutputPath,
        request.UseAv1anParallelVideoEncoding, null, null, true, true, true, false, []);

    private sealed class FixedAudioInfo(AudioSourceInfo? info) : IAudioSourceInfoService
    {
        public Task<AudioSourceInfo?> ProbeAsync(string sourcePath, CancellationToken cancellationToken = default) => Task.FromResult(info);
    }

    private sealed class EmptyProfileLibrary : IProfileLibraryService
    {
        public Task<IReadOnlyList<SavedTemplate>> GetUserTemplatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SavedTemplate>>([]);

        public Task<SavedTemplate> ReadTemplateAsync(string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ExportTemplateAsync(SavedTemplate template, string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SavedTemplate> SaveTemplateAsync(string name, string notes, EncodingProfile profile, string? templateId = null, bool isPinned = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteTemplateAsync(string templateId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SavedTemplate> SetTemplatePinnedAsync(string templateId, bool isPinned, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CommandPreview> BuildPreviewAsync(EncodingProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
