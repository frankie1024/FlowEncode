using System.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FlowEncode.Mcp.Tools;

[McpServerToolType]
public static class EncodingTools
{
    [McpServerTool(Name = "preview_encoding_command", ReadOnly = true, Idempotent = true)]
    [Description("Builds the exact command line FlowEncode would run for a video encode, without starting it. "
        + "Use this to check parameters before calling start_encoding_job. May probe the source with ffprobe/vspipe.")]
    public static async Task<EncodingCommandPreviewResult> PreviewEncodingCommand(
        IEncodingJobRunner runner,
        IProfileLibraryService profileLibrary,
        [Description("Absolute path of the source: .vpy, .avs, .mkv, .mp4, .avi, .flv, .y4m or .yuv.")] string sourcePath,
        [Description("Absolute output path. Defaults to the source folder with an auto-generated name.")] string? outputPath = null,
        [Description("Saved template id or name (see list_templates). Either this or 'encoder' is required.")] string? templateId = null,
        [Description("Encoder when no template is used: x264, x265 or svt-av1. Starts from FlowEncode's default profile.")] string? encoder = null,
        [Description("Optional overrides applied on top of the template / default profile.")] EncodingProfileOptions? overrides = null,
        [Description("Input pipeline: auto (by extension), vapoursynth, avisynth, ffmpeg, y4m or yuv.")] string? pipeline = null,
        [Description("Encode with Av1an chunked parallel encoding instead of a single encoder process.")] bool useAv1anParallel = false,
        [Description("Av1an worker count (only with useAv1anParallel).")] int? av1anWorkers = null,
        CancellationToken cancellationToken = default)
    {
        var request = await BuildRequestAsync(
            profileLibrary, sourcePath, outputPath, templateId, encoder, overrides, pipeline, useAv1anParallel, av1anWorkers, cancellationToken);

        var command = await McpToolGuard.RunAsync(
            () => Task.Run(() => runner.BuildDisplayCommand(request), cancellationToken));
        return new EncodingCommandPreviewResult(
            command,
            request.OutputPath,
            request.Profile.Kind.ToDisplayName(),
            ProfileView.From(request.Profile));
    }

    [McpServerTool(Name = "start_encoding_job", Destructive = false, OpenWorld = false)]
    [Description("Starts a video encode in the background and returns its jobId immediately. "
        + "Poll get_encoding_job for progress; encodes can take minutes to hours. Jobs beyond the "
        + "MaxConcurrentEncodingJobs setting wait in a queue. Jobs stop if the MCP server exits.")]
    public static async Task<EncodingJobStatus> StartEncodingJob(
        EncodingJobManager jobManager,
        IProfileLibraryService profileLibrary,
        [Description("Absolute path of the source: .vpy, .avs, .mkv, .mp4, .avi, .flv, .y4m or .yuv.")] string sourcePath,
        [Description("Absolute output path. Defaults to the source folder with an auto-generated name.")] string? outputPath = null,
        [Description("Saved template id or name (see list_templates). Either this or 'encoder' is required.")] string? templateId = null,
        [Description("Encoder when no template is used: x264, x265 or svt-av1. Starts from FlowEncode's default profile.")] string? encoder = null,
        [Description("Optional overrides applied on top of the template / default profile.")] EncodingProfileOptions? overrides = null,
        [Description("Input pipeline: auto (by extension), vapoursynth, avisynth, ffmpeg, y4m or yuv.")] string? pipeline = null,
        [Description("Encode with Av1an chunked parallel encoding instead of a single encoder process.")] bool useAv1anParallel = false,
        [Description("Av1an worker count (only with useAv1anParallel).")] int? av1anWorkers = null,
        [Description("Allow replacing an existing file at outputPath. Defaults to false.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var request = await BuildRequestAsync(
            profileLibrary, sourcePath, outputPath, templateId, encoder, overrides, pipeline, useAv1anParallel, av1anWorkers, cancellationToken);

        if (!overwrite && File.Exists(request.OutputPath))
        {
            throw new McpException($"Output file already exists: {request.OutputPath}. Pass overwrite=true or choose another outputPath.");
        }

        if (jobManager.List().Any(job => !job.IsFinished
                && string.Equals(job.Request.OutputPath, request.OutputPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new McpException($"Another active job is already writing to {request.OutputPath}.");
        }

        var job = await McpToolGuard.RunAsync(() => Task.FromResult(jobManager.Enqueue(request)));
        return job.GetStatus(logLineCount: 0);
    }

    [McpServerTool(Name = "get_encoding_job", ReadOnly = true)]
    [Description("Returns state (Queued/Running/Completed/Failed/Cancelled), progress percent, fps, bitrate, ETA, "
        + "the sidecar log path once finished, and the most recent encoder log lines.")]
    public static EncodingJobStatus GetEncodingJob(
        EncodingJobManager jobManager,
        [Description("The jobId returned by start_encoding_job.")] string jobId,
        [Description("How many recent log lines to include (0-200). Defaults to 20.")] int logLines = 20)
    {
        return FindJob(jobManager, jobId).GetStatus(logLines);
    }

    [McpServerTool(Name = "list_encoding_jobs", ReadOnly = true)]
    [Description("Lists every encoding job started by this MCP server session with its current state and progress.")]
    public static IReadOnlyList<EncodingJobStatus> ListEncodingJobs(EncodingJobManager jobManager)
    {
        return jobManager.List().Select(static job => job.GetStatus(logLineCount: 0)).ToList();
    }

    [McpServerTool(Name = "cancel_encoding_job", Destructive = true, Idempotent = true)]
    [Description("Cancels a queued or running encoding job and terminates its encoder processes. "
        + "The partial output is discarded.")]
    public static EncodingJobStatus CancelEncodingJob(
        EncodingJobManager jobManager,
        [Description("The jobId returned by start_encoding_job.")] string jobId)
    {
        var job = FindJob(jobManager, jobId);
        jobManager.Cancel(job.Request.JobId);
        return job.GetStatus(logLineCount: 5);
    }

    private static TrackedEncodingJob FindJob(EncodingJobManager jobManager, string jobId)
    {
        if (!Guid.TryParse(jobId, out var id))
        {
            throw new McpException($"'{jobId}' is not a valid job id.");
        }

        return jobManager.Find(id) ?? throw new McpException($"No encoding job with id {jobId}. Call list_encoding_jobs to see known jobs.");
    }

    private static async Task<EncodingJobRequest> BuildRequestAsync(
        IProfileLibraryService profileLibrary,
        string sourcePath,
        string? outputPath,
        string? templateId,
        string? encoder,
        EncodingProfileOptions? overrides,
        string? pipeline,
        bool useAv1anParallel,
        int? av1anWorkers,
        CancellationToken cancellationToken)
    {
        var fullSourcePath = McpPaths.RequireExistingFile(sourcePath, nameof(sourcePath));
        var pipelineKind = EncodingProfileResolver.ParsePipeline(pipeline);
        if (pipelineKind == InputPipelineKind.Auto)
        {
            // Fail early with the supported-extension message instead of deep inside the runner.
            McpToolGuard.Run(() => InputSourceSupport.ResolvePipelineKind(fullSourcePath));
        }

        var profile = await EncodingProfileResolver.ResolveAsync(profileLibrary, templateId, encoder, overrides, cancellationToken);
        var fullOutputPath = string.IsNullOrWhiteSpace(outputPath)
            ? EncodingOutputPathPlanner.BuildDefaultOutputPath(
                fullSourcePath,
                Path.GetDirectoryName(fullSourcePath) ?? Environment.CurrentDirectory,
                profile)
            : McpPaths.RequireAbsolute(outputPath, nameof(outputPath));

        if (string.Equals(fullOutputPath, fullSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException("outputPath must differ from sourcePath.");
        }

        var request = new EncodingJobRequest(
            Guid.NewGuid(),
            profile,
            fullSourcePath,
            fullOutputPath,
            pipelineKind,
            EncoderArchitecture.X64,
            useAv1anParallel,
            useAv1anParallel ? av1anWorkers : null);

        McpToolGuard.Run(() => RequestValidation.ValidateEncodingJobRequest(request));
        return request;
    }
}

public sealed record EncodingCommandPreviewResult(
    string Command,
    string OutputPath,
    string Encoder,
    ProfileView Profile);
