using System.Diagnostics;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class BusinessExecutionSafetyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutoEncode_MissingScriptDependency_IsRejectedBeforeBackendLaunch(bool structured)
    {
        using var f = new Fixture("success", structured);
        var source = Path.Combine(f.Root, "missing-source.vpy");
        var media = Path.Combine(f.Root, "missing.avc");
        File.WriteAllText(source, JsonSerializer.Serialize(new { source = media }));
        var request = f.AutoRequest() with { SourcePath = source };
        var runner = f.AutoRunner(structured);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runner.RunAsync(request));

        StringAssert.Contains(error.Message, media);
        Assert.IsFalse(File.Exists(Path.Combine(f.Root, "av1an-calls.log")));
        Assert.IsFalse(File.Exists(request.OutputPath));
    }

    [TestMethod]
    [DataRow("failure-then-complete")]
    [DataRow("complete-then-failure")]
    public async Task StructuredAutoEncode_ZeroExitCannotOverrideFailureEvents(string scenario)
    {
        using var f = new Fixture(scenario);
        var updates = new List<AutoCompressionProgress>();
        var request = f.AutoRequest();
        var result = await f.AutoRunner(true).RunAsync(request, new Inline<AutoCompressionProgress>(updates.Add));

        Assert.AreEqual(EncodingJobState.Failed, result.State, result.Log);
        StringAssert.Contains(result.Summary, "test backend failure");
        Assert.IsFalse(updates.Any(p => p.State == EncodingJobState.Completed));
        Assert.IsFalse(File.Exists(request.OutputPath));
    }

    [TestMethod]
    public async Task StructuredAutoEncode_CancelEventCannotBeOverriddenByCompletedEvent()
    {
        using var f = new Fixture("cancel-then-complete");
        var request = f.AutoRequest();
        var result = await f.AutoRunner(true).RunAsync(request);
        Assert.AreEqual(EncodingJobState.Cancelled, result.State);
        Assert.IsFalse(File.Exists(request.OutputPath));
    }

    [TestMethod]
    [DataRow(false, "empty-output")]
    [DataRow(true, "empty-output")]
    [DataRow(false, "broken-output")]
    [DataRow(true, "broken-output")]
    public async Task AutoEncode_InvalidOutputIsNeverPublished(bool structured, string scenario)
    {
        using var f = new Fixture(scenario, structured);
        var request = f.AutoRequest();
        File.WriteAllText(request.OutputPath, "existing video");
        var updates = new List<AutoCompressionProgress>();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            f.AutoRunner(structured).RunAsync(request, new Inline<AutoCompressionProgress>(updates.Add)));

        Assert.AreEqual("existing video", File.ReadAllText(request.OutputPath));
        Assert.IsFalse(updates.Any(p => p.State == EncodingJobState.Completed));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutoEncode_ReportsCompletionOnlyAfterOutputIsPublished(bool structured)
    {
        using var f = new Fixture("success", structured);
        var request = f.AutoRequest();
        var finalSeen = false;
        var result = await f.AutoRunner(structured).RunAsync(request, new Inline<AutoCompressionProgress>(p =>
        {
            if (p.State != EncodingJobState.Completed) return;
            Assert.IsTrue(File.Exists(request.OutputPath), "A completion event must refer to published output.");
            finalSeen = true;
        }));
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        Assert.IsTrue(finalSeen);
    }

    [TestMethod]
    [DataRow(BluRayDemuxBackend.DgDemux)]
    [DataRow(BluRayDemuxBackend.Eac3To)]
    public async Task BluRay_MissingPlaylistIsRejectedBeforeToolLaunch(BluRayDemuxBackend backend)
    {
        using var f = new Fixture("success");
        var request = f.DemuxRequest(backend);
        File.Delete(request.Playlist.SourcePath);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => f.DemuxRunner(backend).RunAsync(request));
        Assert.IsFalse(File.Exists(Path.Combine(f.Root, backend.ToString().ToLowerInvariant() + "-calls.log")));
    }

    [TestMethod]
    [DataRow(BluRayDemuxBackend.DgDemux, "empty-output")]
    [DataRow(BluRayDemuxBackend.Eac3To, "empty-output")]
    [DataRow(BluRayDemuxBackend.DgDemux, "no-output")]
    [DataRow(BluRayDemuxBackend.Eac3To, "no-output")]
    public async Task BluRay_ZeroExitWithoutSelectedOutputsCannotComplete(BluRayDemuxBackend backend, string scenario)
    {
        using var f = new Fixture(scenario);
        var updates = new List<BluRayDemuxProgress>();
        var request = f.DemuxRequest(backend);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            f.DemuxRunner(backend).RunAsync(request, new Inline<BluRayDemuxProgress>(updates.Add)));
        Assert.IsFalse(updates.Any(p => p.State == EncodingJobState.Completed));
        Assert.IsFalse(Directory.Exists(request.OutputDirectory));
    }

    [TestMethod]
    [DataRow(BluRayDemuxBackend.DgDemux)]
    [DataRow(BluRayDemuxBackend.Eac3To)]
    public async Task BluRay_PublishesRealArtifactPathsBeforeCompletion(BluRayDemuxBackend backend)
    {
        using var f = new Fixture("success");
        var request = f.DemuxRequest(backend);
        var finalSeen = false;
        var result = await f.DemuxRunner(backend).RunAsync(request, new Inline<BluRayDemuxProgress>(p =>
        {
            if (p.State != EncodingJobState.Completed) return;
            Assert.IsTrue(Directory.Exists(request.OutputDirectory));
            Assert.IsTrue(Directory.GetFiles(request.OutputDirectory).Any());
            finalSeen = true;
        }));
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        Assert.IsTrue(finalSeen);
        Assert.IsTrue(result.OutputPaths.All(File.Exists));
        Assert.IsFalse(result.OutputPaths.Any(p => p.Contains('*')));
    }

    [TestMethod]
    public async Task Eac3ToAudio_EmptyOutputCannotReplaceExistingFile()
    {
        using var f = new Fixture("empty-output");
        var source = Path.Combine(f.Root, "source.wav");
        File.WriteAllText(source, "audio");
        var output = Path.Combine(f.Root, "output.flac");
        File.WriteAllText(output, "existing audio");
        var request = new AudioProcessingRequest(Guid.NewGuid(), source, output,
            AudioProcessingMode.Eac3To, AudioEac3ToOutputFormat.Flac, [], null, null, null, null, false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new CliAudioProcessingRunner(f.Probes, f.Settings).RunAsync(request));
        Assert.AreEqual("existing audio", File.ReadAllText(output));
    }

    [TestMethod]
    public async Task Av1anPreflight_CancellationKillsTheProbeProcess()
    {
        using var f = new Fixture("probe-hang");
        using var cancellation = new CancellationTokenSource();
        var pending = LegacyAv1anCliFallbackRunner.EnsureAv1anRuntimeReadyAsync(Path.Combine(f.Root, "av1an.exe"), AppLanguage.English, cancellation.Token);
        var pidFile = Path.Combine(f.Root, "probe-pid.txt");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(pidFile)) await Task.Delay(20, deadline.Token);
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        try { using var process = Process.GetProcessById(pid); Assert.IsTrue(process.HasExited); }
        catch (ArgumentException) { }
    }

    private sealed class Inline<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Deew5ContractTests.NewRoot();
        public LocalAppPaths Paths { get; }
        public IAppSettingsService Settings { get; }
        public IToolProbeService Probes { get; }
        public Fixture(string scenario, bool structured = true)
        {
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ToolStub")))
                File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
            foreach (var name in new[] { "av1an", "vspipe", "ffprobe", "dgdemux", "eac3to" })
                File.Copy(Path.Combine(Root, "FlowEncode.ToolStub.exe"), Path.Combine(Root, name + ".exe"));
            File.WriteAllText(Path.Combine(Root, "business-scenario.txt"), scenario);
            Paths = new LocalAppPaths(Path.Combine(Root, "state"), Root, [Path.Combine(Root, "workspace")]);
            Settings = new SharedTaskCoordinatorTests.Settings { Value = AppSettings.Default with
            {
                Language = AppLanguage.English,
                ManualToolPaths = new Dictionary<string, string>
                {
                    [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Av1an)] = Path.Combine(Root, "av1an.exe"),
                    [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Vspipe)] = Path.Combine(Root, "vspipe.exe"),
                    [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Ffprobe)] = Path.Combine(Root, "ffprobe.exe")
                }
            }};
            Probes = new ProbesImpl(Root, structured);
        }
        public IAutoCompressionRunner AutoRunner(bool structured) => structured
            ? new StructuredAv1anRunner(Paths, Settings, Probes) : new LegacyAv1anCliFallbackRunner(Paths, Settings);
        public AutoCompressionRequest AutoRequest()
        {
            var source = Path.Combine(Root, "source.y4m");
            File.WriteAllText(source, "YUV4MPEG2 W16 H16 F24:1 C420\n");
            return new(Guid.NewGuid(), source, Path.Combine(Root, "output.mkv"), EncoderKind.X264, AutoCompressionMetric.Vmaf, 95, 4, "", "", 1);
        }
        public IBluRayDemuxBackendAdapter DemuxRunner(BluRayDemuxBackend backend) => backend == BluRayDemuxBackend.DgDemux
            ? new DgDemuxBackendAdapter(Probes) : new Eac3ToBackendAdapter(Probes, Paths);
        public BluRayDemuxRequest DemuxRequest(BluRayDemuxBackend backend)
        {
            var playlist = Path.Combine(Root, "disc", "BDMV", "PLAYLIST", "00001.mpls");
            Directory.CreateDirectory(Path.GetDirectoryName(playlist)!);
            File.WriteAllText(playlist, "playlist");
            var output = Path.Combine(Root, "demux-output");
            var track = new BluRayTrackItem("1011", 1, "1011", BluRayTrackKind.Video, "video", "", "eng");
            return new(Guid.NewGuid(), backend, Path.Combine(Root, "disc"), output, Path.Combine(output, "00001"),
                new("00001", "playlist", "", playlist, "1)", "", null, null),
                [new(track, Path.Combine(output, "00001.*"))]);
        }
        public void Dispose()
        {
            for (var i = 0; ; i++)
                try { Directory.Delete(Root, true); break; }
                catch (IOException) when (i < 20) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(50); }
        }
    }
    private sealed class ProbesImpl(string root, bool structured) : IToolProbeService
    {
        public Task<ToolProbeResult> ProbeAsync(RegisteredToolKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolProbeResult(kind, ReadinessState.Ready, ToolDetectionSource.ManualSelection, "test",
                Path.Combine(root, kind.ToString().ToLowerInvariant() + ".exe"), "1.0", "", "", IsProtocolCompatible: structured));
        public Task<IReadOnlyList<ToolProbeResult>> ProbeAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void InvalidateCache() { }
    }
}
