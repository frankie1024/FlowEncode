using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class Deew5ExternalSmokeTests
{
    [TestMethod]
    [TestCategory("ExternalToolSmoke")]
    public async Task RealDeew51_ShortSampleEncodingMeasurementAndRecovery()
    {
        var input = Environment.GetEnvironmentVariable("FLOWENCODE_DEEW5_SMOKE_INPUT");
        var deew = Environment.GetEnvironmentVariable("FLOWENCODE_DEEW5_SMOKE_TOOL");
        var ffmpeg = Environment.GetEnvironmentVariable("FLOWENCODE_DEEW5_SMOKE_FFMPEG");
        var output = Environment.GetEnvironmentVariable("FLOWENCODE_DEEW5_SMOKE_OUTPUT");
        if (new[] { input, deew, ffmpeg, output }.Any(string.IsNullOrWhiteSpace))
            Assert.Inconclusive("Set FLOWENCODE_DEEW5_SMOKE_INPUT/TOOL/FFMPEG/OUTPUT to a pre-cut short sample and isolated writable deew directory.");
        Assert.IsTrue(File.Exists(input)); Assert.IsTrue(File.Exists(deew)); Assert.IsTrue(File.Exists(ffmpeg));
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        var runner = new CliAudioProcessingRunner(new Probe(deew!, ffmpeg!, ffprobe), new Settings());
        var configPath = Path.Combine(Path.GetDirectoryName(deew)!, "config.toml");
        var original = File.Exists(configPath) ? await File.ReadAllBytesAsync(configPath) : null;
        var records = new List<object>();
        Directory.CreateDirectory(output!);
        var cases = new[] { new DeewOptions(KeepLog: true), new DeewOptions(DdpMode: "bluray", KeepLog: true),
            new DeewOptions(Dialnorm: -27, KeepLog: true), new DeewOptions(MeasureOnly: true, KeepLog: true),
            new DeewOptions(Format: "dd", KeepLog: true), new DeewOptions(Format: "aac", KeepLog: true),
            new DeewOptions(Format: "thd", KeepLog: true), new DeewOptions(Format: "ac4", Dialnorm: -27, KeepLog: true) }
            .Select(o => (Input: input!, Options: o)).ToList();
        foreach (var name in new[] { "odyssey-71-8s.flac", "odyssey-70-8s.flac", "odyssey-dtsx-8s.dts" })
        {
            var extra = Path.Combine(Path.GetDirectoryName(input)!, name);
            if (File.Exists(extra))
                foreach (var o in new[] { new DeewOptions(KeepLog: true), new DeewOptions(DdpMode: "bluray", KeepTemp: true, KeepLog: true), new DeewOptions(MeasureOnly: true, KeepLog: true) })
                    cases.Add((extra, o));
        }
        foreach (var (sample, options) in cases)
        {
            var job = Guid.NewGuid();
            var request = new AudioProcessingRequest(job, sample, Path.Combine(output!, job.ToString("N")), AudioProcessingMode.Ddp, null, [], null, null, null, null, false, options);
            var updates = new List<AudioProcessingProgress>();
            var result = await runner.RunAsync(request, new InlineProgress(updates.Add));
            var logPath = Path.Combine(output!, job.ToString("N") + ".log");
            await File.WriteAllTextAsync(logPath, result.Log);
            records.Add(new { sample, options, result.State, result.ExitCode, result.Summary, result.OutputFiles, result.DiagnosticFiles, result.RetainedTempPath, result.LoudnessDb, result.DialnormDb, ProgressUpdates = updates.Count, logPath });
            await File.WriteAllTextAsync(Path.Combine(output!, "results.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log + "\n" + result.Summary);
            if (options.MeasureOnly) { Assert.IsNotNull(result.LoudnessDb); Assert.AreEqual(0, result.OutputFiles!.Count); }
            else foreach (var media in result.OutputFiles!)
            {
                var check = new System.Diagnostics.ProcessStartInfo(ffprobe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=codec_name,channels,channel_layout,duration", "-of", "json", media }) check.ArgumentList.Add(arg);
                var actual = await ProcessProbeRunner.RunAsync(check, TimeSpan.FromSeconds(20), "Output probe timed out.", default);
                Assert.AreEqual(0, actual.ExitCode, actual.StandardError);
                await File.WriteAllTextAsync(media + ".probe.json", actual.StandardOutput);
                using var parsed = JsonDocument.Parse(actual.StandardOutput);
                var streams = parsed.RootElement.GetProperty("streams");
                Assert.IsTrue(streams.GetArrayLength() > 0);
                var codec = options.Format switch { "dd" => "ac3", "aac" => "aac", "thd" => "truehd", "ac4" => "ac4", _ => "eac3" };
                Assert.IsTrue(streams.EnumerateArray().Any(s => s.GetProperty("codec_name").GetString() == codec));
            }
            var restored = await File.ReadAllBytesAsync(configPath);
            if (original is null) original = restored; else CollectionAssert.AreEqual(original, restored, "Config was not restored exactly.");
        }
    }

    private sealed class Probe(string deew, string ffmpeg, string ffprobe) : IToolProbeService
    {
        public Task<ToolProbeResult> ProbeAsync(RegisteredToolKind kind, CancellationToken cancellationToken = default) => Task.FromResult(new ToolProbeResult(kind, ReadinessState.Ready, ToolDetectionSource.ManualSelection, "external smoke", kind switch { RegisteredToolKind.Deew => deew, RegisteredToolKind.Ffmpeg => ffmpeg, RegisteredToolKind.Ffprobe => ffprobe, _ => throw new NotSupportedException() }, "", "", ""));
        public Task<IReadOnlyList<ToolProbeResult>> ProbeAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void InvalidateCache() { }
    }
    private sealed class Settings : IAppSettingsService { public AppSettings Load() => AppSettings.Default; public void Save(AppSettings settings) { } }
    private sealed class InlineProgress(Action<AudioProcessingProgress> report) : IProgress<AudioProcessingProgress> { public void Report(AudioProcessingProgress value) => report(value); }
}
