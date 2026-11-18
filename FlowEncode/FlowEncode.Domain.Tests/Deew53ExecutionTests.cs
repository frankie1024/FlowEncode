using System.Collections.Concurrent;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class Deew53ExecutionTests
{
    [TestMethod]
    [DataRow("rich-measure", -27d)][DataRow("quiet-measure", -45d)]
    public async Task MeasurementMetrics_ReachLiveProgressMcpSnapshotAndReloadedHistory(string scenario, double loudness)
    {
        using var fixture = new Deew5ExecutionTests.Fixture(["dee_measure_loudness"]);
        var settings = new SharedTaskCoordinatorTests.Settings();
        var history = new TaskHistoryStore(new LocalAppPaths(fixture.InputDirectory));
        var tasks = new TaskCoordinator(settings, history);
        var runner = new CoordinatedAudioRunner(fixture.Runner, tasks);
        var updates = new ConcurrentQueue<AudioProcessingProgress>();
        var request = fixture.Request(new(Format: "thd-atmos", MeasureOnly: true, Dialnorm: -30), scenario);
        var result = await runner.RunAsync(request, new InlineProgress(updates.Enqueue));
        var expected = new DeewLoudnessMetrics("dialogue", 16, -18.9, 13.1, -4.9);

        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        Assert.AreEqual(expected, result.LoudnessMetrics);
        Assert.AreEqual(loudness, result.LoudnessDb);
        Assert.IsNull(result.DialnormDb);
        Assert.AreEqual(0, result.OutputFiles!.Count);
        Assert.IsTrue(updates.Any(u => u.State == EncodingJobState.Running && u.Telemetry?.LoudnessMetrics == expected));
        var snapshot = tasks.Get(request.JobId);
        Assert.AreEqual(expected, snapshot.AudioTelemetry?.LoudnessMetrics);
        Assert.AreEqual(loudness, snapshot.AudioTelemetry?.DeewDb);
        Assert.IsNull(snapshot.AudioTelemetry?.Remaining);
        var restored = new TaskCoordinator(settings, history).Get(request.JobId);
        Assert.AreEqual(snapshot.AudioTelemetry, restored.AudioTelemetry);
    }

    [TestMethod]
    [DataRow("ddp")][DataRow("ddp-atmos")][DataRow("thd-atmos")]
    public async Task AtmosSourceMeasurement_PreviewAndExecutionUseSourceToolsWithoutTheAtmosEncoder(string format)
    {
        using var fixture = new Deew5ExecutionTests.Fixture(["dee_measure_loudness", "truehdd"]);
        var request = fixture.Request(new(Format: format, MeasureOnly: true));
        var container = Path.ChangeExtension(request.SourcePath, ".mkv");
        File.Delete(request.SourcePath);
        await File.WriteAllTextAsync(container, JsonSerializer.Serialize(new
        { scenario = "rich-measure", channels = 8, codec = "truehd", profile = "Dolby TrueHD + Dolby Atmos", sampleRate = "48000" }));
        request = request with { SourcePath = container };
        var plan = await new DeewPlanningService(fixture.Probes).InspectAsync(container, request.Deew!);
        CollectionAssert.AreEqual(new[] { "dee_measure_loudness", "truehdd" }, plan.Profile.RequiredTools.ToArray());
        Assert.IsNull(plan.Profile.Extension);
        var result = await fixture.Runner.RunAsync(request);
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        Assert.AreEqual(0, result.OutputFiles!.Count);
        Assert.IsNotNull(result.LoudnessMetrics);
    }

    [TestMethod]
    [DataRow("88200")][DataRow("96000")]
    public async Task TrueHdAtHighSampleRates_PlanRequiresShimWithFilmDrc(string rate)
    {
        using var fixture = new Deew5ExecutionTests.Fixture(["dee_dthd_encoder", "dee_shim"]);
        var request = fixture.Request(new(Format: "thd", Drc: "film_light"));
        await File.WriteAllTextAsync(request.SourcePath, JsonSerializer.Serialize(new
        { scenario = "success", channels = 2, codec = "flac", sampleRate = rate }));
        var plan = await new DeewPlanningService(fixture.Probes).InspectAsync(request.SourcePath, request.Deew!);
        Assert.IsTrue(plan.Profile.RequiredTools.Contains("dee_shim"));
        var result = await fixture.Runner.RunAsync(request);
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        Assert.IsTrue(result.OutputFiles!.Single().EndsWith(".thd"));
        Assert.AreEqual(Deew5ContractTests.Config, await File.ReadAllTextAsync(fixture.ConfigPath));
    }

    private sealed class InlineProgress(Action<AudioProcessingProgress> report) : IProgress<AudioProcessingProgress>
    { public void Report(AudioProcessingProgress value) => report(value); }
}
