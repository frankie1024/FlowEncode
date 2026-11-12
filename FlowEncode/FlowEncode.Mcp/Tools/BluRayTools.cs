using System.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure.Automation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FlowEncode.Mcp.Tools;

[McpServerToolType]
public static class BluRayTools
{
    [McpServerTool(Name = "scan_bluray_disc", ReadOnly = true, Idempotent = true)]
    [Description("Lists the playlists of a Blu-ray folder with duration and chapter count, using DGDemux or eac3to. "
        + "Pass a playlistId to also list its video / audio / subtitle / chapter tracks (with language), which "
        + "start_bluray_demux takes as trackIds.")]
    public static async Task<BluRayScanResultView> ScanBluRayDisc(
        IBluRayDiscProbeService discProbeService,
        [Description("Absolute path of the Blu-ray folder (the one containing BDMV; BDMV or BDMV\\PLAYLIST also work).")] string discPath,
        [Description("Demux backend: dgdemux (default) or eac3to.")] string? backend = null,
        [Description("Optional playlist id from a previous scan; when set, its tracks are listed as well.")] string? playlistId = null,
        CancellationToken cancellationToken = default)
    {
        var discRoot = NormalizeDiscRoot(discPath);
        var demuxBackend = ParseBackend(backend);

        var playlists = await McpToolGuard.RunAsync(() => discProbeService.ScanDiscAsync(demuxBackend, discRoot, cancellationToken));
        IReadOnlyList<BluRayTrackView>? tracks = null;
        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            var playlist = FindPlaylist(playlists, playlistId, discRoot);
            var scan = await McpToolGuard.RunAsync(() => discProbeService.ScanPlaylistAsync(demuxBackend, discRoot, playlist, cancellationToken));
            tracks = scan.Tracks.Select(BluRayTrackView.From).ToList();
        }

        return new BluRayScanResultView(
            demuxBackend.ToString(),
            discRoot,
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

    [McpServerTool(Name = "preview_bluray_demux_command", ReadOnly = true, Idempotent = true)]
    [Description("Builds the demux command and per-track output paths for the selected tracks of a playlist, without running it.")]
    public static async Task<BluRayDemuxPreview> PreviewBluRayDemuxCommand(
        IBluRayDiscProbeService discProbeService,
        IBluRayDemuxRunner runner,
        [Description("Absolute path of the Blu-ray folder.")] string discPath,
        [Description("Playlist id from scan_bluray_disc.")] string playlistId,
        [Description("Track ids from scan_bluray_disc. Omit to use the tracks the backend selects by default; [\"all\"] selects every track.")] string[]? trackIds = null,
        [Description("Absolute output folder. Defaults to a folder named after the playlist next to the disc folder.")] string? outputDirectory = null,
        [Description("Demux backend: dgdemux (default) or eac3to.")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var request = await BuildRequestAsync(discProbeService, discPath, playlistId, trackIds, outputDirectory, backend, cancellationToken);
        return new BluRayDemuxPreview(
            runner.BuildDisplayCommand(request),
            request.OutputDirectory,
            request.Selections.Select(static selection => new BluRayTrackOutputView(BluRayTrackView.From(selection.Track), selection.OutputPath)).ToList());
    }

    [McpServerTool(Name = "start_bluray_demux", Destructive = false)]
    [Description("Demuxes the selected tracks of a Blu-ray playlist (video, audio, subtitles, chapters) in the FlowEncode "
        + "desktop app's Blu-ray module (starting the app if needed). Poll get_task_status with module=bluray.")]
    public static async Task<AutomationModuleStatus> StartBluRayDemux(
        DesktopBridge desktop,
        IBluRayDiscProbeService discProbeService,
        [Description("Absolute path of the Blu-ray folder.")] string discPath,
        [Description("Playlist id from scan_bluray_disc.")] string playlistId,
        [Description("Track ids from scan_bluray_disc. Omit to use the tracks the backend selects by default; [\"all\"] selects every track.")] string[]? trackIds = null,
        [Description("Absolute output folder. Defaults to a folder named after the playlist next to the disc folder.")] string? outputDirectory = null,
        [Description("Demux backend: dgdemux (default) or eac3to.")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var request = await BuildRequestAsync(discProbeService, discPath, playlistId, trackIds, outputDirectory, backend, cancellationToken);
        return await desktop.SendAsync<AutomationModuleStatus>(
            DesktopAutomationMethods.BluRayStart,
            new StartBluRayParams(request),
            cancellationToken);
    }

    private static async Task<BluRayDemuxRequest> BuildRequestAsync(
        IBluRayDiscProbeService discProbeService,
        string discPath,
        string playlistId,
        string[]? trackIds,
        string? outputDirectory,
        string? backend,
        CancellationToken cancellationToken)
    {
        var discRoot = NormalizeDiscRoot(discPath);
        var demuxBackend = ParseBackend(backend);
        var playlists = await McpToolGuard.RunAsync(() => discProbeService.ScanDiscAsync(demuxBackend, discRoot, cancellationToken));
        var playlist = FindPlaylist(playlists, playlistId, discRoot);
        var scan = await McpToolGuard.RunAsync(() => discProbeService.ScanPlaylistAsync(demuxBackend, discRoot, playlist, cancellationToken));

        var tracks = SelectTracks(scan.Tracks, trackIds);
        var fullOutputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(Path.GetDirectoryName(discRoot.TrimEnd('\\', '/')) ?? discRoot, $"{Path.GetFileName(discRoot.TrimEnd('\\', '/'))}_{scan.Playlist.Id}")
            : McpPaths.RequireAbsolute(outputDirectory, nameof(outputDirectory));
        if (File.Exists(fullOutputDirectory))
        {
            throw new McpException($"outputDirectory points to a file: {fullOutputDirectory}");
        }

        var selections = tracks
            .Select(track => new BluRayTrackSelection(
                track,
                BluRayTrackOutputPathPlanner.BuildTrackOutputPath(demuxBackend, fullOutputDirectory, scan.Playlist, track)))
            .ToList();

        return new BluRayDemuxRequest(
            Guid.NewGuid(),
            demuxBackend,
            discRoot,
            fullOutputDirectory,
            BluRayTrackOutputPathPlanner.BuildOutputPrefixPath(fullOutputDirectory, scan.Playlist),
            scan.Playlist,
            selections);
    }

    internal static IReadOnlyList<BluRayTrackItem> SelectTracks(IReadOnlyList<BluRayTrackItem> tracks, string[]? trackIds)
    {
        if (tracks.Count == 0)
        {
            throw new McpException("The playlist has no demuxable tracks.");
        }

        List<BluRayTrackItem> selected;
        if (trackIds is null || trackIds.Length == 0)
        {
            selected = tracks.Where(static track => track.IsSelectedByDefault).ToList();
            if (selected.Count == 0)
            {
                throw new McpException("No tracks are selected by default for this playlist. Pass trackIds from scan_bluray_disc.");
            }
        }
        else if (trackIds.Any(static id => string.Equals(id?.Trim(), "all", StringComparison.OrdinalIgnoreCase)))
        {
            selected = tracks.ToList();
        }
        else
        {
            selected = [];
            foreach (var id in trackIds)
            {
                var track = tracks.FirstOrDefault(item => string.Equals(item.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new McpException($"Track '{id}' is not part of this playlist. Known ids: {string.Join(", ", tracks.Select(static item => item.Id))}.");
                if (!selected.Contains(track))
                {
                    selected.Add(track);
                }
            }
        }

        return selected.OrderBy(static track => track.Order).ToList();
    }

    internal static string NormalizeDiscRoot(string? discPath)
    {
        var fullPath = McpPaths.RequireExistingDirectory(discPath, nameof(discPath));
        if (Directory.Exists(Path.Combine(fullPath, "BDMV", "PLAYLIST")))
        {
            return fullPath;
        }

        var name = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        if (name.Equals("BDMV", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(fullPath, "PLAYLIST")))
        {
            return Directory.GetParent(fullPath)!.FullName;
        }

        if (name.Equals("PLAYLIST", StringComparison.OrdinalIgnoreCase)
            && Directory.GetParent(fullPath) is { } bdmv
            && bdmv.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase)
            && bdmv.Parent is not null)
        {
            return bdmv.Parent.FullName;
        }

        throw new McpException($"{fullPath} is not a Blu-ray folder (expected BDMV\\PLAYLIST inside it).");
    }

    private static BluRayPlaylistItem FindPlaylist(IReadOnlyList<BluRayPlaylistItem> playlists, string? playlistId, string discRoot)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new McpException("'playlistId' is required. Call scan_bluray_disc to list playlists.");
        }

        return playlists.FirstOrDefault(item => string.Equals(item.Id, playlistId.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"Playlist '{playlistId}' was not found on {discRoot}.");
    }

    private static BluRayDemuxBackend ParseBackend(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? BluRayDemuxBackend.DgDemux : McpEnum.Parse<BluRayDemuxBackend>(value, "backend");
    }
}

public sealed record BluRayScanResultView(
    string Backend,
    string DiscPath,
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
    int Order,
    string Kind,
    string DisplayName,
    string Detail,
    string Language,
    bool IsSelectedByDefault)
{
    public static BluRayTrackView From(BluRayTrackItem track) => new(
        track.Id,
        track.Order,
        track.Kind.ToString(),
        track.DisplayName,
        track.Detail,
        track.Language,
        track.IsSelectedByDefault);
}

public sealed record BluRayTrackOutputView(BluRayTrackView Track, string OutputPath);

public sealed record BluRayDemuxPreview(
    string Command,
    string OutputDirectory,
    IReadOnlyList<BluRayTrackOutputView> Tracks);
