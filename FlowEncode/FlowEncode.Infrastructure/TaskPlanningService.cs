using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class TaskPlanningService(TaskCoordinator tasks, IAppSettingsService settings,
    IToolProbeService probes, IEncoderDiscoveryService encoders, IDeewPlanningService deew,
    IEncodingJobRunner videoRunner, IAudioProcessingRunner audioRunner, IBluRayDemuxRunner bluRayRunner,
    IAutoCompressionRunner autoRunner, TimeProvider? clock = null) : ITaskPlanningService
{
    private readonly object _plansGate = new();
    private readonly Dictionary<Guid, string> _plans = new();
    private readonly SemaphoreSlim _retryGate = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<TaskExecutionPlan> CreateAsync(TaskWorkload request, CancellationToken cancellationToken = default) =>
        CreatePlanAsync(request, null, cancellationToken);

    private async Task<TaskExecutionPlan> CreatePlanAsync(TaskWorkload request, Guid? retryOfTaskId, CancellationToken cancellationToken)
    {
        request = JsonSerializer.Deserialize<TaskWorkload>(JsonSerializer.Serialize(request))!.WithTaskId(Guid.NewGuid());
        request.Validate();
        var evidence = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        var plan = new TaskExecutionPlan(Guid.NewGuid(), now, now.AddMinutes(15), request, evidence.Command, evidence.Outputs,
            evidence.Warnings, evidence.Fingerprint, retryOfTaskId);
        var serialized = JsonSerializer.Serialize(plan);
        lock (_plansGate)
        {
            if (_plans.Count >= 500)
                foreach (var pair in _plans)
                    if (JsonSerializer.Deserialize<TaskExecutionPlan>(pair.Value)!.ExpiresAt <= now) _plans.Remove(pair.Key);
            if (_plans.Count >= 500) throw new InvalidOperationException("Too many pending plans. Submit a plan or wait for old plans to expire.");
            _plans.Add(plan.PlanId, serialized);
        }
        return plan;
    }

    public async Task<TaskSubmission> SubmitAsync(Guid planId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ValidateKey(idempotencyKey);
        if (tasks.FindSubmission(idempotencyKey, planId) is { } existing) return existing;
        string? serialized;
        lock (_plansGate) _plans.TryGetValue(planId, out serialized);
        if (serialized is null)
            return tasks.FindSubmission(idempotencyKey, planId)
                ?? throw new KeyNotFoundException("Plan not found. Create a new plan after restarting the desktop.");
        var plan = JsonSerializer.Deserialize<TaskExecutionPlan>(serialized)!;
        try
        {
            ThrowIfExpired(plan);
            await EnsureCurrentAsync(plan, cancellationToken).ConfigureAwait(false);
            ThrowIfExpired(plan);
        }
        catch
        {
            if (tasks.FindSubmission(idempotencyKey, planId) is { } acceptedDuringValidation) return acceptedDuringValidation;
            throw;
        }
        var receipt = tasks.Reserve(plan.Request, TaskOrigin.Mcp, plan.PlanId, idempotencyKey, plan.RetryOfTaskId,
            token => EnsureCurrentAsync(plan, token));
        if (!receipt.AlreadyAccepted)
        {
            lock (_plansGate) _plans.Remove(planId);
            // Accepted tasks outlive the client connection and its request cancellation token.
            _ = ExecuteAcceptedAsync(plan.Request);
        }
        return receipt;
    }

    public async Task<TaskSubmission> RetryAsync(Guid taskId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ValidateKey(idempotencyKey);
        await _retryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (tasks.FindSubmission(idempotencyKey) is { } existing)
            {
                if (tasks.Get(existing.TaskId).RetryOfTaskId != taskId)
                    throw new InvalidOperationException("This idempotency key belongs to another task.");
                return existing;
            }
            var snapshot = tasks.Get(taskId);
            if (snapshot.State is not (EncodingJobState.Failed or EncodingJobState.Cancelled))
                throw new InvalidOperationException("Only failed or cancelled tasks can be retried.");
            var plan = await CreatePlanAsync(tasks.GetRequest(taskId), taskId, cancellationToken).ConfigureAwait(false);
            try
            {
                return await SubmitAsync(plan.PlanId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_plansGate) _plans.Remove(plan.PlanId);
            }
        }
        finally { _retryGate.Release(); }
    }

    private void ThrowIfExpired(TaskExecutionPlan plan)
    {
        if (_clock.GetUtcNow() >= plan.ExpiresAt) throw new InvalidOperationException("The execution plan expired. Create and inspect a new plan.");
    }

    private async Task EnsureCurrentAsync(TaskExecutionPlan plan, CancellationToken token)
    {
        var current = await InspectAsync(plan.Request, token).ConfigureAwait(false);
        if (current.Fingerprint != plan.Fingerprint) throw new InvalidOperationException("The input, settings, configuration or tools changed after preview. Create a new plan.");
    }

    private async Task ExecuteAcceptedAsync(TaskWorkload request)
    {
        try
        {
            switch (request.Kind)
            {
                case TaskKind.Video: await videoRunner.RunAsync(request.Video!).ConfigureAwait(false); break;
                case TaskKind.Audio: await audioRunner.RunAsync(request.Audio!).ConfigureAwait(false); break;
                case TaskKind.BluRay: await bluRayRunner.RunAsync(request.BluRay!).ConfigureAwait(false); break;
                case TaskKind.AutoCompression: await autoRunner.RunAsync(request.AutoCompression!).ConfigureAwait(false); break;
            }
        }
        catch (Exception) { /* The coordinator records errors and cancellation in the task snapshot. */ }
    }

    private async Task<Inspection> InspectAsync(TaskWorkload request, CancellationToken token)
    {
        if (!File.Exists(request.SourcePath) && !Directory.Exists(request.SourcePath)) throw new FileNotFoundException("Source not found.", request.SourcePath);
        var stamps = new List<string> { JsonSerializer.Serialize(request), Stamp(request.SourcePath) };
        var currentSettings = settings.Load();
        // Appearance and browsing preferences cannot change an encoding plan.
        stamps.Add(JsonSerializer.Serialize(new { currentSettings.PreferSystemEncoders, currentSettings.WorkspaceRootPath, currentSettings.ManualToolPaths }));
        var warnings = new List<string>();
        IReadOnlyList<OutputArtifact> outputs = [new(request.OutputPath, request.OutputIsDirectory ? OutputArtifactKind.Directory : OutputArtifactKind.Media)];
        async Task<ToolProbeResult> Tool(RegisteredToolKind kind)
        {
            var probe = await probes.ProbeAsync(kind, token).ConfigureAwait(false);
            if (!probe.IsReady || !File.Exists(probe.ExecutablePath)) throw new InvalidOperationException($"{kind}: {probe.FailureReason}");
            stamps.Add(Stamp(probe.ExecutablePath) + "|" + probe.DetectedVersion);
            return probe;
        }
        string command;
        switch (request.Kind)
        {
            case TaskKind.Audio:
                var audio = request.Audio!;
                if (audio.Mode == AudioProcessingMode.Ddp)
                {
                    var inspect = await deew.InspectAsync(audio.SourcePath, audio.Deew ?? new(), token).ConfigureAwait(false);
                    warnings.AddRange(inspect.Diagnostics);
                    var tool = await Tool(RegisteredToolKind.Deew);
                    var config = Path.Combine(Path.GetDirectoryName(tool.ExecutablePath)!, "config.toml");
                    stamps.Add(File.Exists(config) ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(config, token))) : "config:missing");
                    var ffmpeg = await Tool(RegisteredToolKind.Ffmpeg); var ffprobe = await Tool(RegisteredToolKind.Ffprobe);
                    var env = DeewToolchain.StartInfo(tool.ExecutablePath, ffmpeg.ExecutablePath, ffprobe.ExecutablePath);
                    foreach (var name in new[] { "ffmpeg", "ffprobe" }.Concat(inspect.Profile.RequiredTools))
                        stamps.Add(Stamp(DeewToolchain.Resolve(tool.ExecutablePath, name, env.Environment["PATH"] ?? "")));
                    command = CommandLineDisplay.JoinArguments(new[] { tool.ExecutablePath }.Concat(DeewArguments.Build(audio.SourcePath, audio.OutputPath, inspect.Profile.Options)));
                    var outputName = inspect.ExpectedOutputFileName ?? (Path.GetFileNameWithoutExtension(audio.SourcePath) + inspect.Profile.Extension);
                    outputs = inspect.Profile.Options.MeasureOnly ? [] : [new(Path.Combine(audio.OutputPath, outputName), OutputArtifactKind.Media)];
                    warnings.Add("The preview shows the final directory. Execution uses a fresh staging directory and validates new media before publishing it.");
                }
                else
                {
                    if (audio.Mode == AudioProcessingMode.Opus) { await Tool(RegisteredToolKind.Ffmpeg); await Tool(RegisteredToolKind.Ffprobe); if (!audio.UseOpusMappingFamily1) await Tool(RegisteredToolKind.OpusExt); }
                    else { AudioSourceSupport.ValidateDeewInput(audio.SourcePath, new()); await Tool(RegisteredToolKind.Eac3To); }
                    command = audioRunner.BuildDisplayCommand(audio);
                }
                break;
            case TaskKind.Video:
                var video = request.Video!;
                var binary = encoders.ResolveEncoder(video.Profile.Kind, video.PreferredArchitecture, currentSettings.PreferSystemEncoders)
                    ?? throw new InvalidOperationException("The requested encoder is unavailable.");
                stamps.Add(Stamp(binary.ExecutablePath));
                var pipeline = video.PipelineKind == InputPipelineKind.Auto ? InputSourceSupport.ResolvePipelineKind(video.SourcePath) : video.PipelineKind;
                await Tool(RegisteredToolKind.Ffprobe);
                if (pipeline == InputPipelineKind.FfmpegPipe) await Tool(RegisteredToolKind.Ffmpeg);
                if (pipeline == InputPipelineKind.VapourSynth) await Tool(RegisteredToolKind.Vspipe);
                if (pipeline == InputPipelineKind.AviSynth) await Tool(RegisteredToolKind.Avs2PipeMod);
                if (video.UseAv1anParallelVideoEncoding) await Tool(RegisteredToolKind.Av1an);
                command = videoRunner is CoordinatedEncodingRunner validated
                    ? await Task.Run(() => validated.BuildValidatedDisplayCommand(video, token), token).ConfigureAwait(false)
                    : videoRunner.BuildDisplayCommand(video);
                warnings.Add("Video encoding produces a video elementary stream. Audio and subtitles are not automatically muxed into a container.");
                break;
            case TaskKind.BluRay:
                var bluray = request.BluRay!;
                BluRayDemuxSourceSupport.ValidateExecutionSource(bluray);
                await Tool(bluray.Backend == BluRayDemuxBackend.DgDemux ? RegisteredToolKind.DgDemux : RegisteredToolKind.Eac3To);
                if (bluray.Selections.Any(static s => BluRayDemuxSourceSupport.IsMkvChaptersTrack(s.Track)))
                    await Tool(RegisteredToolKind.Ffprobe);
                stamps.Add(Stamp(bluray.Playlist.SourcePath));
                command = bluRayRunner.BuildDisplayCommand(bluray);
                outputs = bluray.Selections.Select(s => new OutputArtifact(
                    bluray.Backend == BluRayDemuxBackend.Eac3To
                        ? Path.Combine(bluray.OutputDirectory, Path.GetFileName(s.OutputPath))
                        : Path.IsPathFullyQualified(s.OutputPath)
                            ? s.OutputPath
                            : Path.Combine(bluray.OutputDirectory, s.OutputPath),
                    OutputArtifactKind.Media)).ToArray();
                if (!request.OverwriteOutput)
                    EnsureNoBluRayTrackConflicts(bluray.OutputDirectory, outputs);
                break;
            default:
                await Tool(RegisteredToolKind.Av1an); await Tool(RegisteredToolKind.Ffprobe);
                var auto = request.AutoCompression!;
                if (Path.GetExtension(auto.SourcePath).Equals(".vpy", StringComparison.OrdinalIgnoreCase)) await Tool(RegisteredToolKind.Vspipe);
                if (Path.GetExtension(auto.SourcePath).Equals(".avs", StringComparison.OrdinalIgnoreCase)) await Tool(RegisteredToolKind.Avs2PipeMod);
                command = autoRunner is CoordinatedAutoCompressionRunner validatedAuto
                    ? await Task.Run(() => validatedAuto.BuildValidatedDisplayCommand(auto, token), token).ConfigureAwait(false)
                    : autoRunner.BuildDisplayCommand(auto);
                break;
        }
        if (!request.OverwriteOutput && outputs.Any(a => File.Exists(a.Path)
            || a.Kind == OutputArtifactKind.Media && Directory.Exists(a.Path)))
            throw new IOException("An expected output already exists. Choose another output or explicitly enable overwrite.");
        stamps.Add(command);
        stamps.Sort(StringComparer.Ordinal);
        return new(command, outputs, warnings, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", stamps)))));
    }

    private static void EnsureNoBluRayTrackConflicts(string outputDirectory, IReadOnlyList<OutputArtifact> outputs)
    {
        if (!Directory.Exists(outputDirectory))
            return;

        var patterns = outputs.Select(output => Path.GetFileName(output.Path))
            .Where(pattern => pattern.Contains('*') || pattern.Contains('?'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (patterns.Length == 0)
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(outputDirectory))
        {
            var name = Path.GetFileName(entry);
            if (patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)))
                throw new IOException($"An expected track output already exists: '{entry}'. Choose another output or explicitly enable overwrite.");
        }
    }

    private static string Stamp(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full)) { var f = new FileInfo(full); return $"{full}|{f.Length}|{f.LastWriteTimeUtc.Ticks}"; }
        if (Directory.Exists(full)) return $"{full}|dir|{Directory.GetLastWriteTimeUtc(full).Ticks}";
        throw new FileNotFoundException("A plan dependency disappeared.", full);
    }
    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) throw new ArgumentException("Supply a stable idempotency key of 1–200 characters. Reuse it only for retries of this submission.");
    }
    private sealed record Inspection(string Command, IReadOnlyList<OutputArtifact> Outputs, IReadOnlyList<string> Warnings, string Fingerprint);
}
