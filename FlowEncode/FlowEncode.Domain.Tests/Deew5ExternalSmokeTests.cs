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
    public async Task RealDeew55_ShortSampleEncodingMeasurementAndRecovery()
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

    [TestMethod]
    [TestCategory("ExternalToolSmoke")]
    public async Task RealDeew55_PhaseShiftNamingPipeRichMeasurementAnd96kHzLosslessRoundTrip()
    {
        var input = Environment.GetEnvironmentVariable("FLOWENCODE_DEEW5_SMOKE_INPUT");
        var deew = Environment.GetEnvironmentVariable("FLOWENCODE_DEEW5_SMOKE_TOOL");
        var ffmpeg = Environment.GetEnvironmentVariable("FLOWENCODE_DEEW5_SMOKE_FFMPEG");
        var output = Environment.GetEnvironmentVariable("FLOWENCODE_DEEW5_SMOKE_OUTPUT");
        if (new[] { input, deew, ffmpeg, output }.Any(string.IsNullOrWhiteSpace))
            Assert.Inconclusive("Set FLOWENCODE_DEEW5_SMOKE_INPUT/TOOL/FFMPEG/OUTPUT to isolated real-tool fixtures.");
        var root = Path.Combine(output!, "deew53");
        Directory.CreateDirectory(root);
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        var runner = new CliAudioProcessingRunner(new Probe(deew!, ffmpeg!, ffprobe), new Settings());
        var config = Path.Combine(Path.GetDirectoryName(deew)!, "config.toml");
        var original = await File.ReadAllBytesAsync(config);
        var records = new List<object>();

        async Task<AudioProcessingResult> Run(string source, DeewOptions options)
        {
            var job = Guid.NewGuid();
            var request = new AudioProcessingRequest(job, source, Path.Combine(root, job.ToString("N")), AudioProcessingMode.Ddp,
                null, [], null, null, null, null, false, options);
            var updates = new List<AudioProcessingProgress>();
            var result = await runner.RunAsync(request, new InlineProgress(updates.Add));
            var log = Path.Combine(root, job.ToString("N") + ".log");
            await File.WriteAllTextAsync(log, result.Log);
            records.Add(new { source, options, result.State, result.Summary, result.LoudnessDb, result.LoudnessMetrics,
                result.OutputFiles, log, ProgressUpdates = updates.Count });
            await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log + "\n" + result.Summary);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(config));
            return result;
        }

        await Run(input!, new(Format: "dd", NoPhaseShift: true, KeepLog: true));
        await Run(input!, new(NoPhaseShift: true, KeepLog: true));
        var named = await Run(input!, new(Format: "ddp", PipeMode: "always", OutputName: "custom-ddp-output", KeepLog: true));
        Assert.AreEqual("custom-ddp-output.ec3", Path.GetFileName(named.OutputFiles!.Single()));
        var templated = await Run(input!, new(Format: "ddp", PipeMode: "never", OutputTemplate: "{stem}.{format}.{channels}", KeepLog: true));
        StringAssert.EndsWith(Path.GetFileName(templated.OutputFiles!.Single()), ".ddp.5.1.ec3");
        var imsGeneral = await Run(input!, new(Format: "ac4-ims", KeepLog: true));
        StringAssert.EndsWith(imsGeneral.OutputFiles!.Single(), ".ac4");
        var imsMusic = await Run(input!, new(Format: "ac4-ims", Ac4ImsMode: "music", OutputName: "custom-ims-music", KeepLog: true));
        Assert.AreEqual("custom-ims-music.ac4", Path.GetFileName(imsMusic.OutputFiles!.Single()));
        // The selected Atmos output does not restrict measurement to Atmos input.
        var measured = await Run(input!, new(Format: "thd-atmos", MeasureOnly: true, DownmixChannels: 2, Drc: "none", KeepLog: true));
        Assert.IsNotNull(measured.LoudnessMetrics?.LoudnessRangeLu);
        Assert.IsNotNull(measured.LoudnessMetrics?.TruePeakDbtp);
        Assert.AreEqual(0, measured.OutputFiles!.Count);

        async Task Ffmpeg(params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo(ffmpeg!)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            var result = await ProcessProbeRunner.RunAsync(start, TimeSpan.FromSeconds(30), "Smoke ffmpeg timed out.", default);
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
        }

        var quietSource = Path.Combine(root, "quiet-48k.flac");
        await Ffmpeg("-y", "-v", "error", "-f", "lavfi", "-i", "anoisesrc=r=48000:d=3.072:a=0.003:s=531", "-ac", "2", "-c:a", "flac", quietSource);
        var quiet = await Run(quietSource, new(MeasureOnly: true, KeepLog: true));
        Assert.IsTrue(quiet.LoudnessDb is < -31, quiet.Summary);
        Assert.AreEqual(0, quiet.OutputFiles!.Count);

        var highRate = Path.Combine(root, "source-96k.flac");
        await Ffmpeg("-y", "-v", "error", "-f", "lavfi", "-i", "anoisesrc=r=96000:d=3.072:a=0.05:s=53", "-ac", "2", "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24", highRate);
        var encoded = await Run(highRate, new(Format: "thd", Drc: "film_light", KeepLog: true));
        var expected = Path.Combine(root, "source-96k.pcm");
        var decoded = Path.Combine(root, "decoded-96k.pcm");
        await Ffmpeg("-y", "-v", "error", "-i", highRate, "-c:a", "pcm_s24le", "-f", "s24le", expected);
        await Ffmpeg("-y", "-v", "error", "-drc_scale", "0", "-i", encoded.OutputFiles!.Single(), "-c:a", "pcm_s24le", "-f", "s24le", decoded);
        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(expected), await File.ReadAllBytesAsync(decoded), "96 kHz TrueHD must decode sample-for-sample, including stream length.");
        var resampled = Path.Combine(root, "source-88200.flac");
        await Ffmpeg("-y", "-v", "error", "-i", highRate, "-ar", "88200", "-c:a", "flac", resampled);
        await Run(resampled, new(Format: "thd", Drc: "film_light", KeepLog: true));
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
