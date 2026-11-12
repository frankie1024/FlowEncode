using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

// Decorators keep the existing UI request/progress contracts while sharing admission, output claims and slots.
public sealed class CoordinatedEncodingRunner(LocalEncodingJobRunner inner, TaskCoordinator tasks) : IEncodingJobRunner
{
    public string BuildDisplayCommand(EncodingJobRequest request) => inner.BuildDisplayCommand(request);
    public Task ValidateSourceAsync(string sourcePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        inner.ValidateSourceAsync(sourcePath, progress, cancellationToken);
    internal string BuildValidatedDisplayCommand(EncodingJobRequest request, CancellationToken token) => inner.BuildValidatedDisplayCommand(request, token);
    public void AbortJob(Guid id) { try { tasks.Cancel(id); } catch (KeyNotFoundException) { } inner.AbortJob(id); }
    public Task<EncodingJobResult> RunAsync(EncodingJobRequest request, IProgress<EncodingJobProgress>? progress = null, CancellationToken cancellationToken = default) =>
        tasks.RunAsync(new(TaskKind.Video, Video: request), (updates, token) => inner.RunAsync(request,
            new Forward<EncodingJobProgress>(p => { updates.Report(new(p.ProgressFraction, p.Summary, p.IsSourcePreparation ? "preparing" : "encoding", p.ShouldShowInLog ? p.DetailLine : "")); progress?.Report(p); }), token),
            result => new(result.State, result.Summary, result.Log,
                (result.State == EncodingJobState.Completed && File.Exists(request.OutputPath) ? new[] { new OutputArtifact(request.OutputPath, OutputArtifactKind.Media, "Video elementary stream; audio/subtitles are not muxed") } : [])
                .Concat(File.Exists(result.LogFilePath) ? [new OutputArtifact(result.LogFilePath, OutputArtifactKind.Diagnostic)] : []).ToArray()), cancellationToken);
}

public sealed class CoordinatedAudioRunner(CliAudioProcessingRunner inner, TaskCoordinator tasks) : IAudioProcessingRunner
{
    public string BuildDisplayCommand(AudioProcessingRequest request) => inner.BuildDisplayCommand(request);
    public void Abort(Guid id) { try { tasks.Cancel(id); } catch (KeyNotFoundException) { } inner.Abort(id); }
    public Task<AudioProcessingResult> RunAsync(AudioProcessingRequest request, IProgress<AudioProcessingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        tasks.RunAsync(new(TaskKind.Audio, Audio: request), (updates, token) => inner.RunAsync(request,
            new Forward<AudioProcessingProgress>(p => { updates.Report(new(p.ProgressFraction, p.Summary, p.PhaseLabel ?? "audio", p.DetailLine)); progress?.Report(p); }), token),
            result => new(result.State, result.Summary, result.Log, AudioArtifacts(request, result)), cancellationToken);

    private static IReadOnlyList<OutputArtifact> AudioArtifacts(AudioProcessingRequest request, AudioProcessingResult result) =>
        (result.OutputFiles ?? (result.State == EncodingJobState.Completed && File.Exists(request.OutputPath) ? [request.OutputPath] : []))
        .Select(p => new OutputArtifact(p, OutputArtifactKind.Media))
        .Concat((result.DiagnosticFiles ?? []).Select(p => new OutputArtifact(p, OutputArtifactKind.Diagnostic)))
        .Concat(result.RetainedTempPath is string path ? [new OutputArtifact(path, OutputArtifactKind.RetainedTemporary, "May be incomplete after failure or cancellation")] : []).ToArray();
}

public sealed class CoordinatedBluRayRunner(CliBluRayDemuxRunner inner, TaskCoordinator tasks) : IBluRayDemuxRunner
{
    public string BuildDisplayCommand(BluRayDemuxRequest request) => inner.BuildDisplayCommand(request);
    public void Abort(Guid id, BluRayDemuxBackend backend) { try { tasks.Cancel(id); } catch (KeyNotFoundException) { } inner.Abort(id, backend); }
    public Task<BluRayDemuxResult> RunAsync(BluRayDemuxRequest request, IProgress<BluRayDemuxProgress>? progress = null, CancellationToken cancellationToken = default) =>
        tasks.RunAsync(new(TaskKind.BluRay, BluRay: request), (updates, token) => inner.RunAsync(request,
            new Forward<BluRayDemuxProgress>(p => { updates.Report(new(p.ProgressFraction, p.Summary, "demux", p.DetailLine)); progress?.Report(p); }), token),
            result => new(result.State, result.Summary, result.Log, result.State == EncodingJobState.Completed
                ? result.OutputPaths.Where(File.Exists).Select(p => new OutputArtifact(p, OutputArtifactKind.Media)).ToArray() : []), cancellationToken);
}

public sealed class CoordinatedAutoCompressionRunner(StructuredAv1anRunner inner, TaskCoordinator tasks) : IAutoCompressionRunner
{
    internal string BuildValidatedDisplayCommand(AutoCompressionRequest request, CancellationToken token) => inner.BuildValidatedDisplayCommand(request, token);
    public string BuildDisplayCommand(AutoCompressionRequest request) => inner.BuildDisplayCommand(request);
    public void Abort(Guid id) { try { tasks.Cancel(id); } catch (KeyNotFoundException) { } inner.Abort(id); }
    public Task<AutoCompressionResult> RunAsync(AutoCompressionRequest request, IProgress<AutoCompressionProgress>? progress = null, CancellationToken cancellationToken = default) =>
        tasks.RunAsync(new(TaskKind.AutoCompression, AutoCompression: request), (updates, token) => inner.RunAsync(request,
            new Forward<AutoCompressionProgress>(p => { updates.Report(new(p.ProgressFraction, p.Summary, p.Stage.ToString(), p.DetailLine)); progress?.Report(p); }), token),
            result => new(result.State, result.Summary, result.Log, result.State == EncodingJobState.Completed && File.Exists(request.OutputPath)
                ? [new OutputArtifact(request.OutputPath, OutputArtifactKind.Media)] : []), cancellationToken);
}

internal sealed class Forward<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
