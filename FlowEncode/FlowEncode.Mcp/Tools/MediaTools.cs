using System.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FlowEncode.Mcp.Tools;

[McpServerToolType]
public static class MediaTools
{
    [McpServerTool(Name = "probe_video_source", ReadOnly = true, Idempotent = true)]
    [Description("Reads resolution, frame rate, frame count, bit depth, pixel format and HDR colour metadata of a video "
        + "source the way the encoder pipeline will see it (ffprobe for containers, vspipe for .vpy scripts).")]
    public static async Task<VideoSourceInfoView> ProbeVideoSource(
        LocalAppPaths paths,
        IAppSettingsService settingsService,
        [Description("Absolute path of a .vpy, .mkv, .mp4, .avi, .flv or .y4m source.")] string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = McpPaths.RequireExistingFile(sourcePath, nameof(sourcePath));
        var probe = new SourceVideoInfoProbe(new ExternalToolLocator(paths, settingsService));

        var info = await McpToolGuard.RunAsync(() => Task.Run(
            () => probe.Probe(fullPath, InputSourceSupport.ResolvePipelineKind(fullPath), cancellationToken: cancellationToken),
            cancellationToken));
        if (info is null)
        {
            throw new McpException($"Could not read video metadata from {fullPath}. Raw .yuv sources carry no metadata.");
        }

        return new VideoSourceInfoView(
            info.Width,
            info.Height,
            info.FramesPerSecond is { } fps ? Math.Round(fps, 3) : null,
            info.FpsNumerator,
            info.FpsDenominator,
            info.TotalFrames,
            info.BitDepth,
            info.PixelFormat,
            info.ColorRange,
            info.ColorPrimaries,
            info.ColorTransfer,
            info.ColorMatrix,
            info.ChromaLocation,
            info.MasteringDisplay,
            info.ContentLightLevel);
    }

    [McpServerTool(Name = "probe_audio_source", ReadOnly = true, Idempotent = true)]
    [Description("Reads codec, channel layout, sample rate, bit depth and duration of an audio file or the first audio "
        + "track of a container using ffprobe.")]
    public static async Task<AudioSourceInfoView> ProbeAudioSource(
        IAudioSourceInfoService audioSourceInfoService,
        [Description("Absolute path of an audio file or a container with an audio track.")] string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = McpPaths.RequireExistingFile(sourcePath, nameof(sourcePath));
        var info = await McpToolGuard.RunAsync(() => audioSourceInfoService.ProbeAsync(fullPath, cancellationToken))
            ?? throw new McpException($"No audio stream could be read from {fullPath}. Is FFprobe installed?");

        return new AudioSourceInfoView(
            info.CodecName,
            string.IsNullOrWhiteSpace(info.ProfileName) ? null : info.ProfileName,
            info.Channels,
            info.ChannelLayout,
            info.SampleRate,
            info.BitDepth,
            info.DurationSeconds,
            info.IsLossless());
    }
}

public sealed record VideoSourceInfoView(
    int Width,
    int Height,
    double? FramesPerSecond,
    int? FpsNumerator,
    int? FpsDenominator,
    long? TotalFrames,
    int BitDepth,
    string PixelFormat,
    string? ColorRange,
    string? ColorPrimaries,
    string? ColorTransfer,
    string? ColorMatrix,
    string? ChromaLocation,
    string? MasteringDisplay,
    string? ContentLightLevel);

public sealed record AudioSourceInfoView(
    string Codec,
    string? Profile,
    int Channels,
    string ChannelLayout,
    int? SampleRate,
    int? BitDepth,
    double? DurationSeconds,
    bool IsLossless);
