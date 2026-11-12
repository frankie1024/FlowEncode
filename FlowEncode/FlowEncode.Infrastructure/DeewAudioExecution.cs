using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed partial class CliAudioProcessingRunner
{
    private readonly ConcurrentDictionary<Guid, byte> _abortedDeew = new();

    private async Task<AudioProcessingResult> RunDdpAsync(AudioProcessingRequest request,
        IProgress<AudioProcessingProgress>? progress, CancellationToken token)
    {
        var options = DeewArguments.Normalize(request.Deew);
        AudioSourceSupport.ValidateDeewInput(request.SourcePath, options);
        var deew = await ResolveToolPathAsync(RegisteredToolKind.Deew, token);
        var ffmpeg = await ResolveToolPathAsync(RegisteredToolKind.Ffmpeg, token);
        var ffprobe = await ResolveToolPathAsync(RegisteredToolKind.Ffprobe, token);
        // Version validation precedes config initialization: never ask legacy deew to generate a config.
        var version = await DeewToolchain.ProbeAsync(DeewToolchain.StartInfo(deew, ffmpeg, ffprobe), "--version", token);
        var deewLine = version.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("deew ", StringComparison.OrdinalIgnoreCase)) ?? "";
        if (!DeewToolchain.IsSupportedVersion(deewLine)) throw new InvalidOperationException("FlowEncode 3.0 requires deew >=5.1.0. Legacy tools require FlowEncode 2.0.2. " + deewLine);
        var environment = DeewToolchain.StartInfo(deew, ffmpeg, ffprobe);
        var actualProbe = DeewToolchain.Resolve(deew, "ffprobe", environment.Environment["PATH"] ?? "");
        var actualFfmpeg = DeewToolchain.Resolve(deew, "ffmpeg", environment.Environment["PATH"] ?? "");
        var source = await FfprobeAudioSourceInfoService.ProbeAtPathAsync(actualProbe, request.SourcePath, options.TrackIndex ?? 0, false, token)
            ?? throw new InvalidOperationException("Cannot inspect the selected input audio track.");
        var outputRoot = Path.GetFullPath(request.OutputPath);
        if (options.TempMode == DeewTempMode.Custom && string.IsNullOrWhiteSpace(options.CustomTempRoot))
            throw new InvalidOperationException("Select a writable custom temporary directory.");
        var tempRoot = options.TempMode == DeewTempMode.Custom ? Path.GetFullPath(options.CustomTempRoot!) : outputRoot;
        var jobRoot = Path.Combine(tempRoot, ".flowencode-temp", "deew", request.JobId.ToString("N"));
        var temp = Path.Combine(jobRoot, "temp");
        var eventsPath = Path.Combine(jobRoot, "events.jsonl");
        var staging = GetStagingDirectory(request);
        var inputDirectory = Path.Combine(jobRoot, "input");
        var diagnostics = new List<string>();
        var outputs = new List<string>();
        var log = new StringBuilder();
        var gate = new object();
        void Log(string line) { lock (gate) log.AppendLine(line); }
        var state = EncodingJobState.Failed;
        var exit = -1;
        var summary = "deew failed";
        var preserveStaging = false;
        var mediaMigrationFailed = false;
        DeewEventsReader? reader = null;
        DeewConfigLease? lease = null;
        string? retainedTemp = null;
        string? externalTemp = null;
        string command = BuildDisplayCommand(request);
        try
        {
            Directory.CreateDirectory(jobRoot);
            // Create fresh files exclusively; stale events can never prove this task succeeded.
            using (new FileStream(eventsPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }
            Directory.CreateDirectory(temp);
            lease = await DeewConfigLease.AcquireAsync(deew, options.TempMode == DeewTempMode.DeewSettings ? null : temp,
                async ct => { await DeewToolchain.ProbeAsync(DeewToolchain.StartInfo(deew, ffmpeg, ffprobe), "-gc", ct); }, token);
            var warnings = new List<string>();
            var effectiveChannels = source.RequiresDdpDtsCompatibilityInput() ? 8 : source.Channels;
            var profile = DeewBitrates.Apply(DeewArguments.Resolve(options, effectiveChannels, lease.Snapshot.DdpMode, lease.Snapshot.AtmosMode, warnings), lease.Snapshot.Bitrates);
            if (options.Format.EndsWith("-atmos", StringComparison.Ordinal)
                && (!source.CodecName.Equals("truehd", StringComparison.OrdinalIgnoreCase) || !source.ProfileName.Contains("Atmos", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Atmos output requires a selected TrueHD Atmos input track.");
            if (options.TempMode == DeewTempMode.DeewSettings)
            {
                externalTemp = string.IsNullOrWhiteSpace(lease.Snapshot.TempPath) ? Path.Combine(Path.GetDirectoryName(deew)!, "temp") : Path.GetFullPath(lease.Snapshot.TempPath, Path.GetDirectoryName(deew)!);
                Log("deew manages its own temp directory: " + externalTemp + ". Host cancellation may leave intermediates; this directory is never recursively cleaned by FlowEncode.");
            }
            if (source.DurationSeconds is > 0 && source.SampleRate is > 0)
            {
                var estimatedBytes = source.DurationSeconds.Value * source.SampleRate.Value * Math.Min(effectiveChannels, 8) * 3;
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(externalTemp ?? temp)!);
                    if (drive.AvailableFreeSpace < estimatedBytes)
                        Log($"warning: Estimated PCM temporary space {estimatedBytes / (1024 * 1024 * 1024):0.0} GiB exceeds available space on {drive.Name}. Encoding may fail if the disk fills.");
                }
                catch (IOException) { }
            }
            options = profile.Options;
            foreach (var warning in warnings) Log("warning: " + warning);
            await DeewToolchain.ValidateAsync(deew, ffmpeg, ffprobe, profile, Log, token);
            command = $"{Quote(deew)} {string.Join(" ", DeewArguments.Build(request.SourcePath, request.OutputPath, options).Select(Quote))}";
            Log("Execution profile: " + profile);
            if (Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any())
                throw new InvalidOperationException("This job's staging directory is not empty: " + staging);
            Directory.CreateDirectory(staging);
            var input = request.SourcePath;
            if (source.RequiresDdpDtsCompatibilityInput())
            {
                if (options.Format.EndsWith("-atmos", StringComparison.Ordinal)) throw new InvalidOperationException("Atmos output requires TrueHD Atmos input.");
                Directory.CreateDirectory(inputDirectory);
                input = Path.Combine(inputDirectory, Path.GetFileNameWithoutExtension(input) + ".flac");
                var preparation = CreateDdpDtsPreparationStartInfo(actualFfmpeg, request.SourcePath, input);
                // The selected audio stream must be the same one used for probing and encoding.
                var map = preparation.ArgumentList.IndexOf("-map");
                if (map >= 0) preparation.ArgumentList[map + 1] = $"0:a:{options.TrackIndex ?? 0}";
                Log("Preparing DTS:X lossless 7.1 compatibility input; height metadata is not retained.");
                var prepared = await ExecuteSingleProcessAsync(request, preparation, Log, token, null);
                if (prepared.ExitCode != 0) throw new InvalidOperationException("DTS:X input preparation failed.");
                var preparedInfo = await FfprobeAudioSourceInfoService.ProbeAtPathAsync(actualProbe, input, 0, false, token);
                if (preparedInfo is not { Channels: 8, ChannelLayout: "7.1" }) throw new InvalidOperationException("DTS:X preparation did not produce a 7.1 layout.");
                options = options with { TrackIndex = null };
            }
            reader = new DeewEventsReader(profile, Log, (fraction, stage, db, origin) =>
                progress?.Report(new(request.JobId, EncodingJobState.Running, fraction,
                    "deew: " + stage, "deew: " + stage, new(null, null, null, null, db, origin), stage)));
            var info = DeewToolchain.StartInfo(deew, ffmpeg, ffprobe);
            foreach (var arg in DeewArguments.Build(input, staging, options)) info.ArgumentList.Add(arg);
            info.Environment["DEEW_EVENTS_FILE"] = eventsPath;
            progress?.Report(new(request.JobId, EncodingJobState.Running, null, "deew: starting", "deew: starting"));
            exit = await ExecuteDeewAsync(request.JobId, info, eventsPath, reader, Log, token);
            if (token.IsCancellationRequested || _abortedDeew.ContainsKey(request.JobId) || exit == 130) state = EncodingJobState.Cancelled;
            else if (exit != 0 || reader.Error is not null) summary = reader.Error ?? $"deew exited {exit}.";
            else if (options.MeasureOnly)
            {
                if (reader.Db.HasValue) { state = EncodingJobState.Completed; summary = $"Measured loudness: {reader.Db:0.##} dB"; }
                else summary = "deew exited without a valid measured loudness result.";
            }
            else
            {
                var media = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                    .Where(p => Path.GetExtension(p).Equals(profile.Extension, StringComparison.OrdinalIgnoreCase) && new FileInfo(p).Length > 0).ToArray();
                if (media.Length == 0) summary = "deew exited without producing a non-empty media file for this job.";
                else
                {
                    // Validate the entire manifest before publishing any file. Never publish diagnostics as media.
                    mediaMigrationFailed = true;
                    foreach (var file in media)
                    {
                        var target = Path.Combine(outputRoot, Path.GetRelativePath(staging, file));
                        ExecutionOutputStaging.FinalizeFile(file, target, request.JobId, request.OverwriteOutput);
                        outputs.Add(target);
                    }
                    mediaMigrationFailed = false;
                    state = EncodingJobState.Completed;
                    summary = "deew completed";
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { state = EncodingJobState.Cancelled; }
        catch (Exception ex) { summary = ex.Message; Log("ERROR: " + ex.Message); }
        finally
        {
            if (state == EncodingJobState.Cancelled) summary = "deew cancelled";
            try
            {
                if (options.KeepLog || state != EncodingJobState.Completed)
                {
                    var logRoot = Path.Combine(outputRoot, ".flowencode-artifacts", "deew", request.JobId.ToString("N"), "logs");
                    Directory.CreateDirectory(logRoot);
                    if (Directory.Exists(staging))
                        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Where(p => p.EndsWith(".deew-log.html", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".thd.log.html", StringComparison.OrdinalIgnoreCase)))
                        {
                            var target = Path.Combine(logRoot, Path.GetRelativePath(staging, file));
                            preserveStaging = true;
                            ExecutionOutputStaging.FinalizeFile(file, target, request.JobId);
                            diagnostics.Add(target);
                            preserveStaging = false;
                        }
                    var textLog = Path.Combine(logRoot, "flowencode.log");
                    await File.WriteAllTextAsync(textLog, log.ToString(), Encoding.UTF8, CancellationToken.None);
                    diagnostics.Add(textLog);
                }
            }
            catch (Exception ex) { preserveStaging = true; Log("Diagnostic preservation failed: " + ex.Message); summary += " Diagnostic preservation failed; staging retained: " + staging; }
            if (options.KeepTemp && Directory.Exists(temp) && Directory.EnumerateFileSystemEntries(temp).Any()) retainedTemp = temp;
            else ExecutionOutputStaging.CleanupStagedDirectory(temp, Log);
            ExecutionOutputStaging.CleanupStagedDirectory(inputDirectory, Log);
            try { File.Delete(eventsPath); } catch (Exception ex) { Log("Events cleanup: " + ex.Message); }
            if (!preserveStaging && !mediaMigrationFailed) ExecutionOutputStaging.CleanupStagedDirectory(staging, Log, 2);
            else summary += " Recoverable output retained: " + staging;
            if (retainedTemp is null) ExecutionOutputStaging.CleanupStagedDirectory(jobRoot, Log, 2);
            if (externalTemp is not null && Directory.Exists(externalTemp) && (options.KeepTemp || state != EncodingJobState.Completed)) retainedTemp = externalTemp;
            lease?.Dispose();
            if (lease?.RestorationError is string restorationError) { Log(restorationError); summary += " Config restoration requires attention: " + restorationError; }
            _abortedDeew.TryRemove(request.JobId, out _);
        }
        progress?.Report(new(request.JobId, state, state == EncodingJobState.Completed ? 1 : null, summary, summary));
        return new(request.JobId, state, exit, summary, log.ToString(), command, outputs, diagnostics, retainedTemp,
            reader is not null && (options.MeasureOnly || options.Format == "ac4" && options.Dialnorm is not null and not 0) ? reader.Db : null,
            options.Dialnorm is not null and not 0 ? options.Dialnorm : null);
    }

    private async Task<int> ExecuteDeewAsync(Guid jobId, ProcessStartInfo info, string eventsPath,
        DeewEventsReader reader, Action<string> log, CancellationToken token)
    {
        using var process = new Process { StartInfo = info };
        token.ThrowIfCancellationRequested();
        process.Start();
        using var execution = new ManagedProcessExecution(process);
        _activeExecutions[jobId] = execution;
        using var cancellation = token.Register(execution.Terminate);
        using var stopPolling = new CancellationTokenSource();
        var stdout = PumpAsync(process.StandardOutput, log, CancellationToken.None);
        var stderr = PumpAsync(process.StandardError, log, CancellationToken.None);
        var polling = Task.Run(async () =>
        {
            try
            {
                while (!stopPolling.IsCancellationRequested)
                {
                    await reader.ReadAvailableAsync(eventsPath, false, stopPolling.Token);
                    await Task.Delay(200, stopPolling.Token);
                }
            }
            catch (OperationCanceledException) when (stopPolling.IsCancellationRequested) { }
        });
        try
        {
            // Cancellation terminates the process tree, then we still drain streams and the event tail.
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) execution.Terminate();
            stopPolling.Cancel();
            await polling;
            using var tailDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await reader.ReadAvailableAsync(eventsPath, true, tailDeadline.Token); }
            catch (OperationCanceledException) { log("deew event tail read timed out."); }
            _activeExecutions.TryRemove(jobId, out _);
        }
    }
}
