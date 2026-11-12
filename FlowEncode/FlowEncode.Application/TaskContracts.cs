using System.Text.Json.Serialization;
using FlowEncode.Domain;

namespace FlowEncode.Application;

public enum TaskKind { Video, Audio, BluRay, AutoCompression }
public enum TaskOrigin { Manual, Mcp }
public enum OutputArtifactKind { Media, Directory, Diagnostic, RetainedTemporary }

public sealed record OutputArtifact(string Path, OutputArtifactKind Kind, string Description = "");

public sealed record TaskWorkload(TaskKind Kind, EncodingJobRequest? Video = null, AudioProcessingRequest? Audio = null,
    BluRayDemuxRequest? BluRay = null, AutoCompressionRequest? AutoCompression = null)
{
    [JsonIgnore] public Guid TaskId => Kind switch { TaskKind.Video => Video!.JobId, TaskKind.Audio => Audio!.JobId, TaskKind.BluRay => BluRay!.JobId, _ => AutoCompression!.JobId };
    [JsonIgnore] public string SourcePath => Kind switch { TaskKind.Video => Video!.SourcePath, TaskKind.Audio => Audio!.SourcePath, TaskKind.BluRay => BluRay!.DiscPath, _ => AutoCompression!.SourcePath };
    [JsonIgnore] public string OutputPath => Kind switch { TaskKind.Video => Video!.OutputPath, TaskKind.Audio => Audio!.OutputPath, TaskKind.BluRay => BluRay!.OutputDirectory, _ => AutoCompression!.OutputPath };
    [JsonIgnore] public bool OutputIsDirectory => Kind == TaskKind.BluRay || Kind == TaskKind.Audio && Audio!.Mode == AudioProcessingMode.Ddp;
    [JsonIgnore] public bool ProducesMedia => Kind != TaskKind.Audio || Audio!.Mode != AudioProcessingMode.Ddp || !DeewArguments.Normalize(Audio.Deew).MeasureOnly;
    [JsonIgnore] public bool OverwriteOutput => Kind switch { TaskKind.Video => Video!.OverwriteOutput, TaskKind.Audio => Audio!.OverwriteOutput, TaskKind.BluRay => BluRay!.OverwriteOutput, _ => AutoCompression!.OverwriteOutput };

    public TaskWorkload WithTaskId(Guid id) => Kind switch
    {
        TaskKind.Video => this with { Video = Video! with { JobId = id } },
        TaskKind.Audio => this with { Audio = Audio! with { JobId = id } },
        TaskKind.BluRay => this with { BluRay = BluRay! with { JobId = id } },
        _ => this with { AutoCompression = AutoCompression! with { JobId = id } }
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || new object?[] { Video, Audio, BluRay, AutoCompression }.Count(x => x is not null) != 1
            || Kind switch { TaskKind.Video => Video is null, TaskKind.Audio => Audio is null, TaskKind.BluRay => BluRay is null, _ => AutoCompression is null })
            throw new ArgumentException("Supply exactly one request matching the task kind.");
        if (TaskId == Guid.Empty) throw new ArgumentException("A stable task ID is required.");
        switch (Kind)
        {
            case TaskKind.Video: RequestValidation.ValidateEncodingJobRequest(Video!); break;
            case TaskKind.Audio: RequestValidation.ValidateAudioProcessingRequest(Audio!); break;
            case TaskKind.BluRay:
                if (BluRay!.Playlist is null || BluRay.Selections.Count == 0 || !Enum.IsDefined(BluRay.Backend))
                    throw new ArgumentException("Select a valid Blu-ray playlist, backend and tracks.");
                break;
            case TaskKind.AutoCompression: RequestValidation.ValidateAutoCompressionRequest(AutoCompression!); break;
        }
        if (!Path.IsPathFullyQualified(SourcePath) || !Path.IsPathFullyQualified(OutputPath))
            throw new ArgumentException("Source and output paths must be absolute.");
        if (string.Equals(Path.GetFullPath(SourcePath), Path.GetFullPath(OutputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source and output must be different paths.");
    }
}

public sealed record TaskExecutionPlan(Guid PlanId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    TaskWorkload Request, string DisplayCommand, IReadOnlyList<OutputArtifact> ExpectedOutputs,
    IReadOnlyList<string> Warnings, string Fingerprint, Guid? RetryOfTaskId = null);

public sealed record TaskSnapshot(Guid TaskId, TaskKind Kind, TaskOrigin Origin, Guid? PlanId,
    EncodingJobState State, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    string SourcePath, string OutputPath, double? ProgressFraction, string Summary, string Stage,
    string Log, IReadOnlyList<OutputArtifact> Artifacts, string? Error = null, Guid? RetryOfTaskId = null);

public sealed record TaskUpdate(double? ProgressFraction, string Summary, string Stage, string Detail = "");
public sealed record TaskCompletion(EncodingJobState State, string Summary, string Log, IReadOnlyList<OutputArtifact> Artifacts);
public sealed record TaskSubmission(Guid TaskId, Guid? PlanId, bool AlreadyAccepted);
public sealed record TaskHistoryRecord(TaskSnapshot Snapshot, TaskWorkload Workload, string? IdempotencyKey);

public interface ITaskHistoryStore
{
    IReadOnlyList<TaskHistoryRecord> Load();
    void Save(TaskHistoryRecord record);
}

public interface ITaskPlanningService
{
    Task<TaskExecutionPlan> CreateAsync(TaskWorkload request, CancellationToken cancellationToken = default);
    Task<TaskSubmission> SubmitAsync(Guid planId, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<TaskSubmission> RetryAsync(Guid taskId, string idempotencyKey, CancellationToken cancellationToken = default);
}
