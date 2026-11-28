using System.Collections.Concurrent;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class Deew55ExecutionTests
{
    [TestMethod]
    public async Task OutputNameAndConfigTemplate_MatchPlanPreviewCollisionChecksAndPublishedOutput()
    {
        using var fixture = new Deew5ExecutionTests.Fixture(["dee_ddp_encoder"]);
        var configWithTemplate = Deew5ContractTests.Config.Replace("[output]", "[output]\ntemplate = '{stem}.{lang}'");
        await File.WriteAllTextAsync(fixture.ConfigPath, configWithTemplate);

        var source = Path.Combine(fixture.InputDirectory, "Movie DELAY -5ms.wav");
        await File.WriteAllTextAsync(source, JsonSerializer.Serialize(new { scenario = "success", channels = 6, language = "eng", title = "Main" }));
        var outputDir = Path.Combine(Path.GetDirectoryName(fixture.InputDirectory)!, "out-template");

        var settings = new SharedTaskCoordinatorTests.Settings();
        var tasks = new TaskCoordinator(settings);
        var runner = new CoordinatedAudioRunner(fixture.Runner, tasks);
        var plans = new TaskPlanningService(tasks, settings, fixture.Probes, null!, new DeewPlanningService(fixture.Probes), null!, runner, null!, null!);

        // Config template predicts Movie.eng.ec3 and executes to that exact filename.
        var reqTemplate = new AudioProcessingRequest(Guid.NewGuid(), source, outputDir, AudioProcessingMode.Ddp, null, [], 60, 6, "5.1", null, false, new(), OverwriteOutput: false);
        var planTemplate = await plans.CreateAsync(new(TaskKind.Audio, Audio: reqTemplate));
        Assert.AreEqual(Path.Combine(outputDir, "Movie.eng.ec3"), planTemplate.ExpectedOutputs.Single().Path);

        var resultTemplate = await fixture.Runner.RunAsync(reqTemplate);
        Assert.AreEqual(EncodingJobState.Completed, resultTemplate.State, resultTemplate.Log);
        Assert.AreEqual(Path.Combine(outputDir, "Movie.eng.ec3"), resultTemplate.OutputFiles!.Single());

        // Existing templated file blocks a second non-overwrite plan.
        await Assert.ThrowsExactlyAsync<IOException>(() => plans.CreateAsync(new(TaskKind.Audio, Audio: reqTemplate)));

        // Explicit -on overrides config template cleanly.
        var reqNamed = reqTemplate with { JobId = Guid.NewGuid(), Deew = new(OutputName: "CustomTrack") };
        var planNamed = await plans.CreateAsync(new(TaskKind.Audio, Audio: reqNamed));
        Assert.AreEqual(Path.Combine(outputDir, "CustomTrack.ec3"), planNamed.ExpectedOutputs.Single().Path);
        StringAssert.Contains(planNamed.DisplayCommand, "-on CustomTrack");

        var resultNamed = await fixture.Runner.RunAsync(reqNamed);
        Assert.AreEqual(EncodingJobState.Completed, resultNamed.State, resultNamed.Log);
        Assert.AreEqual(Path.Combine(outputDir, "CustomTrack.ec3"), resultNamed.OutputFiles!.Single());
        Assert.AreEqual(configWithTemplate, await File.ReadAllTextAsync(fixture.ConfigPath));
    }

    [TestMethod]
    public async Task PipedProgressAndPipeModeOverride_RunToCompletionWithoutResettingProgress()
    {
        using var fixture = new Deew5ExecutionTests.Fixture(["dee_ddp_encoder"]);
        var settings = new SharedTaskCoordinatorTests.Settings();
        var tasks = new TaskCoordinator(settings);
        var runner = new CoordinatedAudioRunner(fixture.Runner, tasks);
        var coordinatorFractions = new ConcurrentQueue<double?>();
        tasks.Changed += (_, _) =>
        {
            var snap = tasks.List().FirstOrDefault();
            if (snap is { State: EncodingJobState.Running }) coordinatorFractions.Enqueue(snap.ProgressFraction);
        };
        var updates = new ConcurrentQueue<AudioProcessingProgress>();
        var request = fixture.Request(new(PipeMode: "always"), scenario: "piped-progress", channels: 6);
        var result = await runner.RunAsync(request, new InlineProgress(updates.Enqueue));

        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        var pipeTokens = CommandArgumentTokenizer.Tokenize(result.DisplayCommand).ToList();
        Assert.AreEqual("always", pipeTokens[pipeTokens.IndexOf("-p") + 1]);

        var seenNumeric = false;
        double last = 0;
        foreach (var update in updates.Where(u => u.State == EncodingJobState.Running))
        {
            if (update.ProgressFraction.HasValue)
            {
                seenNumeric = true;
                Assert.IsTrue(update.ProgressFraction.Value >= last, $"Runner progress regressed from {last} to {update.ProgressFraction.Value}.");
                last = update.ProgressFraction.Value;
            }
            else
            {
                Assert.IsFalse(seenNumeric, "Runner progress must not drop back to null after numeric progress was reported.");
            }
        }
        Assert.IsTrue(seenNumeric);

        var coordSeenNumeric = false;
        double coordLast = 0;
        foreach (var fraction in coordinatorFractions)
        {
            if (fraction.HasValue)
            {
                coordSeenNumeric = true;
                Assert.IsTrue(fraction.Value >= coordLast, $"TaskCoordinator progress regressed from {coordLast} to {fraction.Value}.");
                coordLast = fraction.Value;
            }
            else
            {
                Assert.IsFalse(coordSeenNumeric, "TaskCoordinator progress must not drop back to null after numeric progress was reported.");
            }
        }
        Assert.IsTrue(coordSeenNumeric);
    }

    [TestMethod]
    public async Task TrueHddOptionsOnMeasureOnlyAndExplicitS3aOff_ReachCommandAndSuppressConfigWarning()
    {
        using var fixture = new Deew5ExecutionTests.Fixture(["dee_dthd_encoder", "dee_shim", "dee_measure_loudness", "truehdd"]);
        var config = "surround_3db_attenuation = true\n" + Deew5ContractTests.Config;
        await File.WriteAllTextAsync(fixture.ConfigPath, config);

        // -s3a off suppresses the TrueHD surround attenuation warning even when config enables it.
        var thdRequest = fixture.Request(new(Format: "thd", Surround3dbAttenuation: false), channels: 6);
        var thdPlan = await new DeewPlanningService(fixture.Probes).InspectAsync(thdRequest.SourcePath, thdRequest.Deew!);
        Assert.IsFalse(thdPlan.TrueHdSurroundAttenuation);
        Assert.IsFalse(thdPlan.Diagnostics.Contains(DeewConfigEditor.TrueHdSurroundAttenuationWarning));
        var thdResult = await fixture.Runner.RunAsync(thdRequest);
        Assert.AreEqual(EncodingJobState.Completed, thdResult.State, thdResult.Log);
        var thdTokens = CommandArgumentTokenizer.Tokenize(thdResult.DisplayCommand).ToList();
        Assert.AreEqual("off", thdTokens[thdTokens.IndexOf("-s3a") + 1]);
        Assert.IsFalse(thdResult.Log.Contains(DeewConfigEditor.TrueHdSurroundAttenuationWarning, StringComparison.Ordinal));

        // -mo with TrueHD Atmos keeps -wm and -bc.
        var moSource = Path.Combine(fixture.InputDirectory, "atmos.mkv");
        await File.WriteAllTextAsync(moSource, JsonSerializer.Serialize(new
        { scenario = "rich-measure", channels = 8, codec = "truehd", profile = "Dolby TrueHD + Dolby Atmos", sampleRate = "48000" }));
        var moRequest = fixture.Request(new(MeasureOnly: true, WarpMode: "normal", BedConform: true)) with { SourcePath = moSource };
        var moResult = await fixture.Runner.RunAsync(moRequest);
        Assert.AreEqual(EncodingJobState.Completed, moResult.State, moResult.Log);
        var moTokens = CommandArgumentTokenizer.Tokenize(moResult.DisplayCommand).ToList();
        Assert.AreEqual("normal", moTokens[moTokens.IndexOf("-wm") + 1]);
        Assert.AreEqual("on", moTokens[moTokens.IndexOf("-bc") + 1]);
    }

    [TestMethod]
    public async Task Ac4Ims_PlanAndExecutionUseAc4ImsEncoderAndAc4Extension()
    {
        using var fixture = new Deew5ExecutionTests.Fixture(["dee_ac4ims_encoder"]);
        var request = fixture.Request(new(Format: "ac4-ims", Ac4ImsMode: "music"), channels: 6);
        var plan = await new DeewPlanningService(fixture.Probes).InspectAsync(request.SourcePath, request.Deew!);
        CollectionAssert.AreEqual(new[] { "dee_ac4ims_encoder" }, plan.Profile.RequiredTools.ToArray());
        Assert.AreEqual("music_light", plan.Profile.Drc);
        Assert.AreEqual(".ac4", plan.Profile.Extension);

        var result = await fixture.Runner.RunAsync(request);
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        StringAssert.EndsWith(result.OutputFiles!.Single(), ".ac4");
        var imsTokens = CommandArgumentTokenizer.Tokenize(result.DisplayCommand).ToList();
        Assert.AreEqual("music", imsTokens[imsTokens.IndexOf("--ac4-ims-mode") + 1]);
    }

    [TestMethod]
    [DataRow("ddp-atmos", "dee_ddpjoc_encoder", ".ec3", false)]
    [DataRow("thd-atmos", "dee_dthd_encoder", ".thd", false)]
    [DataRow("ac4-atmos", "dee_ac4ajoc_encoder", ".ac4", true)]
    [DataRow("ac4-ims", "dee_ac4ims_encoder", ".ac4", true)]
    public async Task SpatialSources_KeepOriginalTrackAndUseHarletty(string format, string encoder, string extension, bool auro)
    {
        using var fixture = new Deew5ExecutionTests.Fixture([encoder, "harletty", "dee_shim"]);
        var request = fixture.Request(new(Format: format, TrackIndex: 1, OutputTemplate: "{stem}.{track}",
            WarpMode: "loro", BedConform: false), scenario: "spatial");
        var source = Path.ChangeExtension(request.SourcePath, ".mkv");
        await File.WriteAllTextAsync(source, JsonSerializer.Serialize(auro
            ? new { scenario = "spatial", codec = "flac", profile = "", layout = "7.1", auro = (string?)"13.1" }
            : new { scenario = "spatial", codec = "dts", profile = "DTS-HD MA + DTS:X", layout = "5.1.2", auro = (string?)null }));
        request = request with { SourcePath = source };

        var plan = await new DeewPlanningService(fixture.Probes).InspectAsync(source, request.Deew!);
        Assert.AreEqual(auro ? "13.1" : null, plan.Source.Auro3DLayout);
        Assert.AreEqual(auro ? 8 : 6, plan.Source.DeewChannelCount);
        CollectionAssert.Contains(plan.Profile.RequiredTools.ToArray(), "harletty");
        CollectionAssert.DoesNotContain(plan.Profile.RequiredTools.ToArray(), "truehdd");
        Assert.AreEqual("loro", plan.Profile.Options.WarpMode);
        Assert.IsNull(plan.Profile.Options.BedConform);
        Assert.IsTrue(plan.Diagnostics.Any(message => message.Contains("Bed conform is always enabled")));

        var updates = new ConcurrentQueue<AudioProcessingProgress>();
        var result = await fixture.Runner.RunAsync(request, new InlineProgress(updates.Enqueue));
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        StringAssert.EndsWith(result.OutputFiles!.Single(), ".1" + extension);
        Assert.IsTrue(updates.Any(update => update.PhaseLabel == "harletty decode"));
        var command = CommandArgumentTokenizer.Tokenize(result.DisplayCommand).ToList();
        Assert.AreEqual("loro", command[command.IndexOf("-wm") + 1]);
        CollectionAssert.DoesNotContain(command, "-bc");
    }

    [TestMethod]
    public async Task UnsupportedAtmosSource_ReturnsFailedJobWithDiagnostics()
    {
        using var fixture = new Deew5ExecutionTests.Fixture(["dee_ddpjoc_encoder", "dee_shim"]);
        var request = fixture.Request(new(Format: "ddp-atmos"), channels: 6);

        var result = await fixture.Runner.RunAsync(request);

        Assert.AreEqual(EncodingJobState.Failed, result.State);
        StringAssert.Contains(result.Summary, "Atmos output requires");
        Assert.AreEqual(0, result.OutputFiles!.Count);
        Assert.AreEqual(Deew5ContractTests.Config, await File.ReadAllTextAsync(fixture.ConfigPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AuroCarrier_IsIdentifiedWhenUsingChannelBasedProcessing(bool measureOnly)
    {
        using var fixture = new Deew5ExecutionTests.Fixture(["dee_ddp_encoder", "dee_measure_loudness", "harletty", "dee_shim"]);
        var request = fixture.Request(new(TrackIndex: 1, MeasureOnly: measureOnly), channels: 8);
        await File.WriteAllTextAsync(request.SourcePath, JsonSerializer.Serialize(new { codec = "flac", channels = 8, auro = "13.1" }));

        var plan = await new DeewPlanningService(fixture.Probes).InspectAsync(request.SourcePath, request.Deew!);

        Assert.AreEqual("13.1", plan.Source.Auro3DLayout);
        Assert.IsFalse(plan.Profile.HasSpatialDecode);
    }

    private sealed class InlineProgress(Action<AudioProcessingProgress> report) : IProgress<AudioProcessingProgress>
    { public void Report(AudioProcessingProgress value) => report(value); }
}
