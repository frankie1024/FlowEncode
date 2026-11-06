using System.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure.Automation;
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
    [Description("Adds a video encode to the FlowEncode desktop app's queue (starting the app if needed) and returns the job. "
        + "The job is visible and controllable in the app, follows its MaxConcurrentEncodingJobs limit and the "
        + "queue-completion action, and keeps running after this MCP session ends. Poll get_encoding_job for progress.")]
    public static async Task<AutomationJobSnapshot> StartEncodingJob(
        DesktopBridge desktop,
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
        [Description("Start as soon as a slot is free (default). False leaves it queued until start_queued_encoding_job.")] bool startImmediately = true,
        CancellationToken cancellationToken = default)
    {
        var request = await BuildRequestAsync(
            profileLibrary, sourcePath, outputPath, templateId, encoder, overrides, pipeline, useAv1anParallel, av1anWorkers, cancellationToken);

        request = request with { OverwriteOutput = overwrite };
        return await desktop.SendAsync<AutomationJobSnapshot>(
            DesktopAutomationMethods.QueueEnqueue,
            new EnqueueEncodingJobParams(request, startImmediately),
            cancellationToken);
    }

    [McpServerTool(Name = "get_encoding_job", ReadOnly = true)]
    [Description("Returns a queue job's state (Queued/Running/Completed/Failed/Cancelled), progress percent, fps, bitrate, ETA, "
        + "command line, sidecar log path once finished, and the most recent encoder log lines.")]
    public static Task<AutomationJobSnapshot> GetEncodingJob(
        DesktopBridge desktop,
        [Description("The jobId from start_encoding_job or list_encoding_jobs.")] string jobId,
        [Description("How many recent log lines to include (0-200). Defaults to 20.")] int logLines = 20,
        CancellationToken cancellationToken = default)
    {
        return desktop.SendIfRunningAsync<AutomationJobSnapshot>(
            DesktopAutomationMethods.QueueGet,
            new GetJobParams(McpJobIds.Parse(jobId), Math.Clamp(logLines, 0, 200)),
            McpServerText.DesktopNotRunning,
            cancellationToken);
    }

    [McpServerTool(Name = "list_encoding_jobs", ReadOnly = true)]
    [Description("Lists every job in the desktop app's encoding queue in queue order, including jobs the user added in the app.")]
    public static Task<IReadOnlyList<AutomationJobSnapshot>> ListEncodingJobs(
        DesktopBridge desktop,
        CancellationToken cancellationToken = default)
    {
        return desktop.SendIfRunningAsync<IReadOnlyList<AutomationJobSnapshot>>(
            DesktopAutomationMethods.QueueList,
            null,
            McpServerText.DesktopNotRunning,
            cancellationToken);
    }

    [McpServerTool(Name = "cancel_encoding_job", Destructive = true, Idempotent = true)]
    [Description("Cancels a queued or running job in the desktop queue and terminates its encoder processes. "
        + "The partial output is discarded.")]
    public static Task<AutomationJobSnapshot> CancelEncodingJob(
        DesktopBridge desktop,
        [Description("The jobId to cancel.")] string jobId,
        CancellationToken cancellationToken = default)
    {
        return SendJobCommandAsync(desktop, DesktopAutomationMethods.QueueCancel, jobId, cancellationToken);
    }

    [McpServerTool(Name = "start_queued_encoding_job", Destructive = false)]
    [Description("Starts a queued job now. When every concurrency slot is busy the job is moved to the front so it runs next.")]
    public static Task<AutomationJobSnapshot> StartQueuedEncodingJob(
        DesktopBridge desktop,
        [Description("The jobId of a Queued job.")] string jobId,
        CancellationToken cancellationToken = default)
    {
        return SendJobCommandAsync(desktop, DesktopAutomationMethods.QueueStart, jobId, cancellationToken);
    }

    [McpServerTool(Name = "restart_encoding_job", Destructive = false)]
    [Description("Queues a finished, failed or cancelled job again with the same parameters (as a new job id).")]
    public static Task<AutomationJobSnapshot> RestartEncodingJob(
        DesktopBridge desktop,
        [Description("The jobId of a Completed, Failed or Cancelled job.")] string jobId,
        CancellationToken cancellationToken = default)
    {
        return SendJobCommandAsync(desktop, DesktopAutomationMethods.QueueRestart, jobId, cancellationToken);
    }

    [McpServerTool(Name = "remove_encoding_job", Destructive = true, Idempotent = true)]
    [Description("Removes a job that is not running from the desktop queue and returns the remaining queue. Output files are not deleted.")]
    public static Task<IReadOnlyList<AutomationJobSnapshot>> RemoveEncodingJob(
        DesktopBridge desktop,
        [Description("The jobId to remove.")] string jobId,
        CancellationToken cancellationToken = default)
    {
        return desktop.SendIfRunningAsync<IReadOnlyList<AutomationJobSnapshot>>(
            DesktopAutomationMethods.QueueRemove,
            new JobIdParams(McpJobIds.Parse(jobId)),
            McpServerText.DesktopNotRunning,
            cancellationToken);
    }

    [McpServerTool(Name = "move_encoding_job", Destructive = false)]
    [Description("Reorders the desktop queue: moves a queued job to a 0-based position (0 = first, -1 = last). "
        + "Queued jobs cannot move ahead of running ones. Returns the queue in its new order.")]
    public static Task<IReadOnlyList<AutomationJobSnapshot>> MoveEncodingJob(
        DesktopBridge desktop,
        [Description("The jobId of a Queued job.")] string jobId,
        [Description("Target 0-based position among all jobs; -1 moves it to the end.")] int position,
        CancellationToken cancellationToken = default)
    {
        return desktop.SendIfRunningAsync<IReadOnlyList<AutomationJobSnapshot>>(
            DesktopAutomationMethods.QueueMove,
            new MoveJobParams(McpJobIds.Parse(jobId), position),
            McpServerText.DesktopNotRunning,
            cancellationToken);
    }

    private static Task<AutomationJobSnapshot> SendJobCommandAsync(
        DesktopBridge desktop,
        string method,
        string jobId,
        CancellationToken cancellationToken)
    {
        return desktop.SendIfRunningAsync<AutomationJobSnapshot>(
            method,
            new JobIdParams(McpJobIds.Parse(jobId)),
            McpServerText.DesktopNotRunning,
            cancellationToken);
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
