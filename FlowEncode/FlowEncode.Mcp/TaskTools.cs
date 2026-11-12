using System.ComponentModel;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure.Automation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FlowEncode.Mcp;

[McpServerToolType]
public static class TaskTools
{
    [McpServerTool(Name = "get_app_status", ReadOnly = true, Idempotent = true)]
    [Description("Reports desktop availability, local automation switch, active task count and shared concurrency limit. Does not start the desktop.")]
    public static async Task<object> Status(DesktopBridge desktop, CancellationToken cancellationToken = default) =>
        new { status = await desktop.TryGetStatusAsync(cancellationToken), desktopPath = DesktopAppLocator.Find() };

    [McpServerTool(Name = "launch_desktop_app", Destructive = false, Idempotent = true)]
    [Description("Starts the desktop if needed. Task tools also start it automatically. Local MCP automation must be enabled in Settings.")]
    public static Task<DesktopTaskStatus> Launch(DesktopBridge desktop, CancellationToken cancellationToken = default) => desktop.EnsureRunningAsync(cancellationToken);

    [McpServerTool(Name = "get_capabilities", ReadOnly = true, Idempotent = true)]
    [Description("Detects actual local tools, versions, installed encoders, available task kinds and deew formats. Creating a plan checks the exact requested combination.")]
    public static Task<JsonElement> Capabilities(DesktopBridge desktop, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<JsonElement>(DesktopTaskMethods.Capabilities, null, cancellationToken, TimeSpan.FromMinutes(3));

    [McpServerTool(Name = "probe_media", ReadOnly = true, Idempotent = true)]
    [Description("Probes an existing media file with the desktop's ffprobe. Returns all streams, codecs, channels, layouts, profiles and format metadata. deew trackIndex counts audio streams from zero.")]
    public static Task<JsonElement> Probe(DesktopBridge desktop, [Description("Absolute path to the source media.")] string sourcePath, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<JsonElement>(DesktopTaskMethods.ProbeMedia, new ProbeMediaParameters(sourcePath), cancellationToken);

    [McpServerTool(Name = "list_templates", ReadOnly = true, Idempotent = true)]
    [Description("Lists the desktop's saved video templates with stable IDs and full parameter profiles.")]
    public static Task<IReadOnlyList<SavedTemplate>> Templates(DesktopBridge desktop, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<IReadOnlyList<SavedTemplate>>(DesktopTaskMethods.Templates, null, cancellationToken);

    [McpServerTool(Name = "get_default_profile", ReadOnly = true, Idempotent = true)]
    [Description("Returns the built-in video profile. Use its fields as the basis for explicit profile overrides when creating a video plan.")]
    public static Task<EncodingProfile> DefaultProfile(DesktopBridge desktop, EncoderKind encoder, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<EncodingProfile>(DesktopTaskMethods.DefaultProfile, new DefaultProfileParameters(encoder), cancellationToken);

    [McpServerTool(Name = "create_video_plan", ReadOnly = true)]
    [Description("Creates an immutable video plan with effective profile, resolved command, output and compatibility warnings. Does not start encoding. Choose templateId, explicit profile, or an encoder default. Output is a video elementary stream without audio/subtitles. Submit the returned planId after inspecting it.")]
    public static Task<TaskExecutionPlan> VideoPlan(DesktopBridge desktop, string sourcePath, string outputPath,
        EncodingProfile? profile = null, string? templateId = null, EncoderKind encoder = EncoderKind.X265,
        InputPipelineKind pipeline = InputPipelineKind.FfmpegPipe, EncoderArchitecture architecture = EncoderArchitecture.X64,
        bool av1anParallel = false, int? workers = null, bool overwriteOutput = false, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<TaskExecutionPlan>(DesktopTaskMethods.VideoPlan,
            new VideoPlanParameters(sourcePath, outputPath, profile, templateId, encoder, pipeline, architecture, av1anParallel, workers, overwriteOutput), cancellationToken);

    [McpServerTool(Name = "create_audio_plan", ReadOnly = true)]
    [Description("Creates an audio plan for deew/DDP, Opus or eac3to. For Ddp, outputPath is a directory and the deew object selects format/profile/bitrate/trackIndex. Optional deew fields follow its config defaults; format defaults to ddp. measureOnly returns measured loudness without media. Other modes use a full output filename. Non-Atmos container inputs require extracting a standalone audio track first.")]
    public static Task<TaskExecutionPlan> AudioPlan(DesktopBridge desktop, string sourcePath, string outputPath,
        AudioProcessingMode mode = AudioProcessingMode.Ddp, DeewOptions? deew = null, AudioEac3ToOutputFormat? eac3ToFormat = null,
        int? opusBitrateKbps = 192, bool useOpusMappingFamily1 = false, bool overwriteOutput = false, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<TaskExecutionPlan>(DesktopTaskMethods.AudioPlan,
            new AudioPlanParameters(sourcePath, outputPath, mode, deew, eac3ToFormat, opusBitrateKbps, useOpusMappingFamily1, overwriteOutput), cancellationToken);

    [McpServerTool(Name = "create_task_plan", ReadOnly = true)]
    [Description("Creates a typed task plan, including BluRay and AutoCompression. Supply exactly one request matching kind. Use scan_bluray_disc and scan_bluray_playlist to obtain actual playlist/track records. For all requests supply absolute paths and set overwriteOutput=false unless explicitly requested. The supplied request jobId is replaced by a new stable taskId.")]
    public static Task<TaskExecutionPlan> CreatePlan(DesktopBridge desktop, TaskWorkload request, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<TaskExecutionPlan>(DesktopTaskMethods.CreatePlan, request, cancellationToken);

    [McpServerTool(Name = "submit_plan", Destructive = false, Idempotent = true)]
    [Description("Accepts and starts a previously inspected immutable plan in the desktop task center. Keep the same idempotencyKey and planId when retrying after a lost response; this returns the original task and never submits it twice. Accepted work continues after MCP disconnects. Poll get_task by taskId.")]
    public static Task<TaskSubmission> Submit(DesktopBridge desktop, Guid planId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<TaskSubmission>(DesktopTaskMethods.Submit, new SubmitPlanParameters(planId, idempotencyKey), cancellationToken);

    [McpServerTool(Name = "list_tasks", ReadOnly = true, Idempotent = true)]
    [Description("Lists shared desktop task records from manual and MCP entry points, including stable IDs, progress, summaries, recent logs and actual artifacts. History remains available across desktop restarts.")]
    public static Task<IReadOnlyList<TaskSnapshot>> List(DesktopBridge desktop, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<IReadOnlyList<TaskSnapshot>>(DesktopTaskMethods.Tasks, null, cancellationToken);

    [McpServerTool(Name = "get_task", ReadOnly = true, Idempotent = true)]
    [Description("Gets one exact task by stable taskId: state, stage, fraction, bounded logs, error, and actual media/diagnostic/retained-temp artifacts. Completed measure-only tasks have no media. logLines is 0–2000.")]
    public static Task<TaskSnapshot> Get(DesktopBridge desktop, Guid taskId, int logLines = 200, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<TaskSnapshot>(DesktopTaskMethods.Task, new TaskIdParameters(taskId, logLines), cancellationToken);

    [McpServerTool(Name = "cancel_task", Destructive = true, Idempotent = true)]
    [Description("Cancels only the supplied stable taskId. A completed task is left unchanged. Queued work is withdrawn; running work terminates its process tree and performs normal cleanup. Poll get_task until terminal.")]
    public static Task<JsonElement> Cancel(DesktopBridge desktop, Guid taskId, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<JsonElement>(DesktopTaskMethods.Cancel, new TaskIdParameters(taskId), cancellationToken);

    [McpServerTool(Name = "retry_task", Destructive = false, Idempotent = true)]
    [Description("Revalidates and retries a failed/cancelled task as a new taskId using the same frozen parameters and output policy. Supply a new stable idempotencyKey for this retry and reuse it if the retry response is lost. Existing output is never implicitly overwritten.")]
    public static Task<TaskSubmission> Retry(DesktopBridge desktop, Guid taskId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<TaskSubmission>(DesktopTaskMethods.Retry, new RetryTaskParameters(taskId, idempotencyKey), cancellationToken);

    [McpServerTool(Name = "scan_bluray_disc", ReadOnly = true, Idempotent = true)]
    [Description("Scans a local Blu-ray source with the chosen installed backend and returns actual playlists.")]
    public static Task<IReadOnlyList<BluRayPlaylistItem>> ScanDisc(DesktopBridge desktop, BluRayDemuxBackend backend, string discPath, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<IReadOnlyList<BluRayPlaylistItem>>(DesktopTaskMethods.ScanDisc, new ScanDiscParameters(backend, discPath), cancellationToken);

    [McpServerTool(Name = "scan_bluray_playlist", ReadOnly = true, Idempotent = true)]
    [Description("Returns actual tracks for a previously scanned Blu-ray playlist. Use those records to build a BluRay typed execution plan.")]
    public static Task<BluRayPlaylistScanResult> ScanPlaylist(DesktopBridge desktop, BluRayDemuxBackend backend, string discPath, BluRayPlaylistItem playlist, CancellationToken cancellationToken = default) =>
        desktop.SendAsync<BluRayPlaylistScanResult>(DesktopTaskMethods.ScanPlaylist, new ScanPlaylistParameters(backend, discPath, playlist), cancellationToken);
}
