using System.Text;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class Deew5ExecutionTests
{
    [TestMethod]
    public async Task OutputCollision_PreflightsAllMediaBeforePublishingAnyFile()
    {
        using var fixture = new Fixture();
        var request = fixture.Request(scenario: "collision") with { OverwriteOutput = false };
        var source = Path.Combine(fixture.InputDirectory, "a-source.wav");
        File.Move(request.SourcePath, source);
        request = request with { SourcePath = source };
        Directory.CreateDirectory(request.OutputPath);
        var existing = Path.Combine(request.OutputPath, "z-conflict.ec3");
        await File.WriteAllTextAsync(existing, "existing output");

        var result = await fixture.Runner.RunAsync(request);

        Assert.AreEqual(EncodingJobState.Failed, result.State, result.Log);
        Assert.AreEqual("existing output", await File.ReadAllTextAsync(existing));
        Assert.IsFalse(File.Exists(Path.Combine(request.OutputPath,
            Path.GetFileNameWithoutExtension(request.SourcePath) + ".ec3")));
        Assert.AreEqual(0, result.OutputFiles!.Count);
        StringAssert.Contains(result.Summary, "Recoverable output retained:");
        Assert.AreEqual(Deew5ContractTests.Config, await File.ReadAllTextAsync(fixture.ConfigPath));
    }

    [TestMethod]
    public async Task Encoding_PublishesOnlyNewMediaAndRestoresConfig()
    {
        using var fixture = new Fixture();
        var request = fixture.Request(new(KeepLog: true));
        Directory.CreateDirectory(request.OutputPath);
        var existing = Path.Combine(request.OutputPath, "older.ec3");
        await File.WriteAllTextAsync(existing, "older output");
        var result = await fixture.Runner.RunAsync(request);
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        Assert.AreEqual(1, result.OutputFiles!.Count);
        Assert.AreEqual(3, result.DiagnosticFiles!.Count);
        Assert.IsTrue(result.OutputFiles.All(File.Exists));
        Assert.AreEqual("older output", await File.ReadAllTextAsync(existing));
        Assert.IsFalse(Directory.EnumerateFiles(fixture.InputDirectory).Any(p => Path.GetExtension(p) == ".ec3"));
        Assert.IsFalse(Directory.EnumerateFiles(request.OutputPath, "*", SearchOption.TopDirectoryOnly).Any(p => p.EndsWith(".html")));
        Assert.AreEqual(Deew5ContractTests.Config, await File.ReadAllTextAsync(fixture.ConfigPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(request.OutputPath, ".flowencode-temp")));
    }

    [TestMethod]
    [DataRow("empty")][DataRow("error-tail")]
    public async Task EmptyStagingOrTailError_CannotUseAnOldFileAsSuccess(string scenario)
    {
        using var fixture = new Fixture();
        var request = fixture.Request(scenario: scenario);
        Directory.CreateDirectory(request.OutputPath);
        var old = Path.Combine(request.OutputPath, "source.ec3"); await File.WriteAllTextAsync(old, "old");
        var result = await fixture.Runner.RunAsync(request);
        Assert.AreEqual(EncodingJobState.Failed, result.State, result.Log);
        Assert.AreEqual(0, result.OutputFiles!.Count);
        Assert.AreEqual("old", await File.ReadAllTextAsync(old));
        if (scenario == "error-tail") StringAssert.Contains(result.Summary, "error appended after done");
    }

    [TestMethod]
    [DataRow(5, "dee_measure_loudness")][DataRow(7, "dee_ddp_encoder")][DataRow(8, "dee_ddp_encoder")]
    public async Task Measurement_ReturnsLoudnessWithoutMediaOrFormatEncoder(int channels, string neededTool)
    {
        using var fixture = new Fixture([neededTool]);
        var result = await fixture.Runner.RunAsync(fixture.Request(new(Format: "aac", MeasureOnly: true, Dialnorm: -30), channels: channels));
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        Assert.AreEqual(-27, result.LoudnessDb); Assert.IsNull(result.DialnormDb);
        Assert.AreEqual(0, result.OutputFiles!.Count);
    }

    [TestMethod]
    public async Task Measurement_WithoutDbFailsEvenWithRequestedDialnorm()
    {
        using var fixture = new Fixture();
        var result = await fixture.Runner.RunAsync(fixture.Request(new(MeasureOnly: true, Dialnorm: -27), "missing-db"));
        Assert.AreEqual(EncodingJobState.Failed, result.State); Assert.IsNull(result.LoudnessDb);
    }

    [TestMethod]
    public async Task DirectDialnorm_IsNotReportedAsMeasuredLoudness()
    {
        using var fixture = new Fixture(["dee_ddp_encoder"]);
        var result = await fixture.Runner.RunAsync(fixture.Request(new(Dialnorm: -27)));
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        Assert.AreEqual(-27, result.DialnormDb); Assert.IsNull(result.LoudnessDb);
    }

    [TestMethod]
    [DataRow("success", true)][DataRow("failure", true)][DataRow("failure", false)]
    public async Task KeepTemp_PreservesOnlyRequestedIntermediates(string scenario, bool keep)
    {
        using var fixture = new Fixture();
        var request = fixture.Request(new(KeepTemp: keep), scenario);
        var result = await fixture.Runner.RunAsync(request);
        if (keep)
        {
            Assert.IsNotNull(result.RetainedTempPath); Assert.IsTrue(File.Exists(Path.Combine(result.RetainedTempPath, "intermediate.pcm")));
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(result.RetainedTempPath)!, "events.jsonl")));
        }
        else { Assert.IsNull(result.RetainedTempPath); Assert.IsFalse(Directory.Exists(Path.Combine(request.OutputPath, ".flowencode-temp"))); }
    }

    [TestMethod]
    [DataRow(true)][DataRow(false)]
    public async Task Cancellation_KillsWaitsDrainsAndRestoresBeforeReturning(bool abort)
    {
        using var fixture = new Fixture();
        using var cts = new CancellationTokenSource();
        var request = fixture.Request(new(KeepTemp: true, KeepLog: true), "cancel");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reporter = new InlineProgress(p => { if (p.PhaseLabel == "starting") started.TrySetResult(); });
        var run = fixture.Runner.RunAsync(request, reporter, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        if (abort) fixture.Runner.Abort(request.JobId); else cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(EncodingJobState.Cancelled, result.State, result.Log);
        Assert.IsNotNull(result.RetainedTempPath);
        var childId = int.Parse(await File.ReadAllTextAsync(Path.Combine(result.RetainedTempPath, "process-id.txt")));
        try
        {
            using var child = System.Diagnostics.Process.GetProcessById(childId);
            Assert.IsTrue(child.HasExited, "Cancellation must finish the child before returning.");
        }
        catch (ArgumentException) { /* The OS has already removed the exited process. */ }
        Assert.AreEqual(Deew5ContractTests.Config, await File.ReadAllTextAsync(fixture.ConfigPath));
        Assert.IsTrue(result.DiagnosticFiles!.Any(p => p.EndsWith("flowencode.log")));
    }

    [TestMethod]
    public async Task Exit130_IsCancellationEvenWithoutHostToken()
    {
        using var fixture = new Fixture();
        var result = await fixture.Runner.RunAsync(fixture.Request(scenario: "exit130"));
        Assert.AreEqual(EncodingJobState.Cancelled, result.State);
        Assert.AreEqual(0, result.OutputFiles!.Count);
    }

    [TestMethod]
    public async Task SelectedAudioTrack_IsUsedForLayoutDependenciesAndOutput()
    {
        using var fixture = new Fixture();
        var result = await fixture.Runner.RunAsync(fixture.Request(new(TrackIndex: 1, DdpMode: "bluray"), channels: 2));
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        StringAssert.EndsWith(result.OutputFiles![0], ".eb3");
        var tokens = CommandArgumentTokenizer.Tokenize(result.DisplayCommand).ToList();
        Assert.AreEqual("1", tokens[tokens.IndexOf("-ti") + 1]);
    }

    [TestMethod]
    public async Task MissingConfig_IsInitializedOnceAndNewBaselineIsPreserved()
    {
        using var fixture = new Fixture(); File.Delete(fixture.ConfigPath);
        var first = await fixture.Runner.RunAsync(fixture.Request());
        Assert.AreEqual(EncodingJobState.Completed, first.State, first.Log);
        var generated = await File.ReadAllBytesAsync(fixture.ConfigPath);
        var second = await fixture.Runner.RunAsync(fixture.Request());
        Assert.AreEqual(EncodingJobState.Completed, second.State, second.Log);
        CollectionAssert.AreEqual(generated, await File.ReadAllBytesAsync(fixture.ConfigPath));
    }

    [TestMethod]
    public async Task MalformedExistingConfig_IsNeverReset()
    {
        using var fixture = new Fixture(); const string invalid = "logo = 'bad'\n";
        await File.WriteAllTextAsync(fixture.ConfigPath, invalid);
        var result = await fixture.Runner.RunAsync(fixture.Request());
        Assert.AreEqual(EncodingJobState.Failed, result.State);
        Assert.AreEqual(invalid, await File.ReadAllTextAsync(fixture.ConfigPath));
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = Deew5ContractTests.NewRoot();
        public string InputDirectory => Path.Combine(_root, "input");
        public string ConfigPath => Path.Combine(_root, "config.toml");
        public CliAudioProcessingRunner Runner { get; }
        public IToolProbeService Probes { get; }
        public Fixture(string[]? tools = null)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "ToolStub"))) File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
            var extension = OperatingSystem.IsWindows() ? ".exe" : "";
            var stub = Path.Combine(_root, "FlowEncode.ToolStub" + extension);
            foreach (var name in new[] { "deew", "ffmpeg", "ffprobe" }.Concat(tools ?? ["dee_ddp_encoder", "dee_measure_loudness", "dee_shim"]))
            {
                var directory = name.StartsWith("dee_") && name != "dee_shim" ? Path.Combine(_root, "dee") : _root;
                Directory.CreateDirectory(directory);
                // All apphosts resolve the same managed stub assembly in their executable directory.
                foreach (var file in Directory.EnumerateFiles(_root, "FlowEncode.ToolStub.*").Where(p => !p.EndsWith(".exe")))
                    if (directory != _root) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
                var target = Path.Combine(directory, name + extension); File.Copy(stub, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Directory.CreateDirectory(InputDirectory);
            File.WriteAllText(ConfigPath, Deew5ContractTests.Config, new UTF8Encoding(false));
            Probes = new Probe(_root, extension);
            Runner = new CliAudioProcessingRunner(Probes, new Settings());
        }
        public AudioProcessingRequest Request(DeewOptions? options = null, string scenario = "success", int channels = 2)
        {
            var source = Path.Combine(InputDirectory, "中文 source.wav");
            File.WriteAllText(source, JsonSerializer.Serialize(new { scenario, channels }));
            return new(Guid.NewGuid(), source, Path.Combine(_root, "output"), AudioProcessingMode.Ddp, null, [], 60, channels, "stereo", null, false, options);
        }
        public void Dispose()
        {
            // Windows can briefly retain an image-file lock after the child has exited.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(_root, true); return; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(100); }
            }
        }
    }

    private sealed class Probe(string root, string extension) : IToolProbeService
    {
        public Task<ToolProbeResult> ProbeAsync(RegisteredToolKind kind, CancellationToken cancellationToken = default) => Task.FromResult(new ToolProbeResult(kind, ReadinessState.Ready, ToolDetectionSource.ManualSelection, "test", Path.Combine(root, kind.ToString().ToLowerInvariant() + extension), "test", "", ""));
        public Task<IReadOnlyList<ToolProbeResult>> ProbeAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void InvalidateCache() { }
    }
    private sealed class Settings : IAppSettingsService
    {
        public AppSettings Load() => AppSettings.Default;
        public void Save(AppSettings settings) { }
    }
    private sealed class InlineProgress(Action<AudioProcessingProgress> report) : IProgress<AudioProcessingProgress>
    { public void Report(AudioProcessingProgress value) => report(value); }
}
