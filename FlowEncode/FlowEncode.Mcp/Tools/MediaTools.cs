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

    [McpServerTool(Name = "scan_bluray_disc", ReadOnly = true, Idempotent = true)]
    [Description("Lists the playlists of a Blu-ray folder (the folder that contains BDMV) with duration and chapter count, "
        + "using DGDemux or eac3to. Pass a playlist id to also list its video/audio/subtitle tracks.")]
    public static async Task<BluRayScanResultView> ScanBluRayDisc(
        IBluRayDiscProbeService discProbeService,
        [Description("Absolute path of the Blu-ray folder.")] string discPath,
        [Description("Demux backend: dgdemux (default) or eac3to.")] string? backend = null,
        [Description("Optional playlist id from a previous scan; when set, its tracks are listed as well.")] string? playlistId = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = McpPaths.RequireExistingDirectory(discPath, nameof(discPath));
        var demuxBackend = ParseBackend(backend);

        var playlists = await McpToolGuard.RunAsync(() => discProbeService.ScanDiscAsync(demuxBackend, fullPath, cancellationToken));
        IReadOnlyList<BluRayTrackView>? tracks = null;
        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            var playlist = playlists.FirstOrDefault(item => string.Equals(item.Id, playlistId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new McpException($"Playlist '{playlistId}' was not found on {fullPath}.");
            var scan = await McpToolGuard.RunAsync(() => discProbeService.ScanPlaylistAsync(demuxBackend, fullPath, playlist, cancellationToken));
            tracks = scan.Tracks
                .Select(static track => new BluRayTrackView(track.Id, track.Kind.ToString(), track.DisplayName, track.Detail, track.Language))
                .ToList();
        }

        return new BluRayScanResultView(
            demuxBackend.ToString(),
            playlists
                .Select(static playlist => new BluRayPlaylistView(
                    playlist.Id,
                    playlist.DisplayName,
                    playlist.Summary,
                    playlist.DurationText,
                    playlist.ChapterCount))
                .ToList(),
            tracks);
    }

    private static BluRayDemuxBackend ParseBackend(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return BluRayDemuxBackend.DgDemux;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "dgdemux" => BluRayDemuxBackend.DgDemux,
            "eac3to" => BluRayDemuxBackend.Eac3To,
            _ => throw new McpException($"Unknown backend '{value}'. Use dgdemux or eac3to.")
        };
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

public sealed record BluRayScanResultView(
    string Backend,
    IReadOnlyList<BluRayPlaylistView> Playlists,
    IReadOnlyList<BluRayTrackView>? Tracks);

public sealed record BluRayPlaylistView(
    string Id,
    string DisplayName,
    string Summary,
    string DurationText,
    int? ChapterCount);

public sealed record BluRayTrackView(
    string Id,
    string Kind,
    string DisplayName,
    string Detail,
    string Language);
