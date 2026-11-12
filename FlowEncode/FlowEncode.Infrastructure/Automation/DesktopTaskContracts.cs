using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure.Automation;

public static class DesktopTaskMethods
{
    public const string Status = "app.status";
    public const string Capabilities = "app.capabilities";
    public const string ProbeMedia = "media.probe";
    public const string Templates = "templates.list";
    public const string DefaultProfile = "templates.default";
    public const string VideoPlan = "plan.video";
    public const string AudioPlan = "plan.audio";
    public const string CreatePlan = "plan.create";
    public const string Submit = "plan.submit";
    public const string Tasks = "tasks.list";
    public const string Task = "tasks.get";
    public const string Cancel = "tasks.cancel";
    public const string Retry = "tasks.retry";
    public const string ScanDisc = "bluray.scan";
    public const string ScanPlaylist = "bluray.playlist";
}
public sealed record DesktopTaskStatus(string Version, int ProcessId, bool IsReady, bool AutomationEnabled, int ActiveTasks, int MaximumConcurrency);
public sealed record VideoPlanParameters(string SourcePath, string OutputPath, EncodingProfile? Profile = null, string? TemplateId = null,
    EncoderKind Encoder = EncoderKind.X265, InputPipelineKind Pipeline = InputPipelineKind.FfmpegPipe,
    EncoderArchitecture Architecture = EncoderArchitecture.X64, bool Av1anParallel = false, int? Workers = null, bool OverwriteOutput = false);
public sealed record AudioPlanParameters(string SourcePath, string OutputPath, AudioProcessingMode Mode = AudioProcessingMode.Ddp,
    DeewOptions? Deew = null, AudioEac3ToOutputFormat? Eac3ToFormat = null, int? OpusBitrateKbps = 192,
    bool UseOpusMappingFamily1 = false, bool OverwriteOutput = false);
public sealed record SubmitPlanParameters(Guid PlanId, string IdempotencyKey);
public sealed record TaskIdParameters(Guid TaskId, int LogLines = 200);
public sealed record RetryTaskParameters(Guid TaskId, string IdempotencyKey);
public sealed record ProbeMediaParameters(string SourcePath);
public sealed record DefaultProfileParameters(EncoderKind Encoder);
public sealed record ScanDiscParameters(BluRayDemuxBackend Backend, string DiscPath);
public sealed record ScanPlaylistParameters(BluRayDemuxBackend Backend, string DiscPath, BluRayPlaylistItem Playlist);
