using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class Eac3ToBackendAdapter : CliBluRayDemuxBackendAdapterBase
{
    private static readonly Regex TitleLineRegex = new(@"^(?<index>\d+)\)\s+(?<source>.+?),\s+(?<duration>\d{1,2}:\d{2}:\d{2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ChapterSummaryRegex = new(@"^-\s*Chapters,\s*(?<count>\d+)\s+chapters$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrackLineRegex = new(@"^(?<index>\d+):\s+(?<body>.+)$", RegexOptions.Compiled);
    private static readonly Regex ProgressRegex = new(@"^process:\s*(?<value>\d{1,3}(?:\.\d+)?)\s*%$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly LocalAppPaths _appPaths;

    public Eac3ToBackendAdapter(
        IToolProbeService toolProbeService,
        LocalAppPaths appPaths)
        : base(toolProbeService)
    {
        _appPaths = appPaths;
    }

    public override BluRayDemuxBackend Backend => BluRayDemuxBackend.Eac3To;

    public override async Task<IReadOnlyList<BluRayPlaylistItem>> ScanDiscAsync(string discPath, CancellationToken cancellationToken = default)
    {
        var eac3ToPath = await ResolveToolPathAsync(RegisteredToolKind.Eac3To, cancellationToken);
        var startInfo = CreateStartInfo(eac3ToPath, _appPaths.LogsRootPath);
        startInfo.ArgumentList.Add(discPath);
        startInfo.ArgumentList.Add("-minDuration=0");

        var capture = await CaptureProcessAsync(startInfo, cancellationToken);
        if (capture.ExitCode != 0)
        {
            throw new InvalidOperationException(MeaningfulLineHelpers.FirstMeaningfulLine(capture.Output, "eac3to 无法读取蓝光目录。"));
        }

        var playlists = new List<BluRayPlaylistItem>();
        var lines = capture.Output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        for (var index = 0; index < lines.Count; index++)
        {
            var titleMatch = TitleLineRegex.Match(lines[index]);
            if (!titleMatch.Success)
            {
                continue;
            }

            var titleIndex = titleMatch.Groups["index"].Value;
            var playlistId = ResolvePlaylistId(titleMatch.Groups["source"].Value.Trim());
            if (string.IsNullOrWhiteSpace(playlistId))
            {
                continue;
            }

            var durationText = titleMatch.Groups["duration"].Value;
            int? chapterCount = null;

            if (index + 1 < lines.Count)
            {
                var chapterMatch = ChapterSummaryRegex.Match(lines[index + 1]);
                if (chapterMatch.Success
                    && int.TryParse(chapterMatch.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount))
                {
                    chapterCount = parsedCount;
                }
            }

            playlists.Add(new BluRayPlaylistItem(
                playlistId,
                $"{playlistId}.mpls",
                chapterCount.HasValue ? $"{durationText} · {chapterCount.Value} chapters" : durationText,
                Path.Combine(discPath, "BDMV", "PLAYLIST", $"{playlistId}.mpls"),
                $"{titleIndex})",
                durationText,
                TryParseDuration(durationText),
                chapterCount));
        }

        if (playlists.Count == 0)
        {
            throw new InvalidOperationException("eac3to 未返回可用播放列表。");
        }

        return playlists;
    }

    public override async Task<BluRayPlaylistScanResult> ScanPlaylistAsync(string discPath, BluRayPlaylistItem playlist, CancellationToken cancellationToken = default)
    {
        var eac3ToPath = await ResolveToolPathAsync(RegisteredToolKind.Eac3To, cancellationToken);
        var startInfo = CreateStartInfo(eac3ToPath, _appPaths.LogsRootPath);
        startInfo.ArgumentList.Add(discPath);
        startInfo.ArgumentList.Add(playlist.SelectionToken);

        var capture = await CaptureProcessAsync(startInfo, cancellationToken);
        if (capture.ExitCode != 0)
        {
            throw new InvalidOperationException(MeaningfulLineHelpers.FirstMeaningfulLine(capture.Output, "eac3to 无法读取播放列表。"));
        }

        var lines = capture.Output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var summary = lines.FirstOrDefault(static line => line.Contains("video track", StringComparison.OrdinalIgnoreCase))
            ?? playlist.Summary;
        var tracks = new List<BluRayTrackItem>();
        BluRayTrackItem? currentTrack = null;

        foreach (var line in lines)
        {
            var trackMatch = TrackLineRegex.Match(line);
            if (trackMatch.Success)
            {
                if (currentTrack is not null)
                {
                    tracks.Add(currentTrack);
                }

                var trackIndex = int.Parse(trackMatch.Groups["index"].Value, CultureInfo.InvariantCulture);
                var body = trackMatch.Groups["body"].Value.Trim();
                var language = ResolveLanguage(body);
                currentTrack = new BluRayTrackItem(
                    trackIndex.ToString(CultureInfo.InvariantCulture),
                    trackIndex,
                    trackIndex.ToString(CultureInfo.InvariantCulture),
                    ResolveTrackKind(body),
                    BuildDisplayName(trackIndex, body, language),
                    body,
                    language);
                continue;
            }

            if (currentTrack is not null && line.StartsWith('('))
            {
                currentTrack = currentTrack with
                {
                    Detail = $"{currentTrack.Detail}{Environment.NewLine}{line}"
                };
            }
        }

        if (currentTrack is not null)
        {
            tracks.Add(currentTrack);
        }

        if (tracks.Count == 0)
        {
            throw new InvalidOperationException("eac3to 未解析出可选轨道。");
        }

        return new BluRayPlaylistScanResult(
            Backend,
            discPath,
            playlist,
            tracks,
            $"{playlist.DisplayName} · {tracks.Count} items",
            capture.Output);
    }

    public override async Task<BluRayDemuxResult> RunAsync(
        BluRayDemuxRequest request,
        IProgress<BluRayDemuxProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var eac3ToPath = await ResolveToolPathAsync(RegisteredToolKind.Eac3To, cancellationToken);
        Directory.CreateDirectory(request.OutputDirectory);

        var startInfo = CreateStartInfo(eac3ToPath, _appPaths.LogsRootPath);
        startInfo.ArgumentList.Add(request.DiscPath);
        startInfo.ArgumentList.Add(request.Playlist.SelectionToken);

        foreach (var selection in request.Selections.OrderBy(static item => item.Track.Order))
        {
            startInfo.ArgumentList.Add($"{selection.Track.DemuxToken}:");
            startInfo.ArgumentList.Add(selection.OutputPath);
        }

        startInfo.ArgumentList.Add("-progressnumbers");

        return await RunProcessAsync(
            request,
            BuildDisplayCommand(request),
            startInfo,
            ParseProgress,
            IsSuccessfulTerminalLine,
            "eac3to 解复用中",
            "eac3to 解复用完成",
            "eac3to 解复用已取消",
            "eac3to 解复用失败",
            progress,
            cancellationToken);
    }

    public override string BuildDisplayCommand(BluRayDemuxRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("eac3to.exe ");
        builder.Append(Quote(request.DiscPath));
        builder.Append(' ');
        builder.Append(request.Playlist.SelectionToken);

        foreach (var selection in request.Selections.OrderBy(static item => item.Track.Order))
        {
            builder.Append(' ');
            builder.Append(selection.Track.DemuxToken);
            builder.Append(':');
            builder.Append(' ');
            builder.Append(Quote(selection.OutputPath));
        }

        builder.Append(" -progressnumbers");
        return builder.ToString();
    }

    private static string ResolvePlaylistId(string source)
    {
        var index = source.IndexOf(".mpls", StringComparison.OrdinalIgnoreCase);
        return index <= 0
            ? string.Empty
            : source[..index];
    }

    private static BluRayTrackKind ResolveTrackKind(string body)
    {
        if (body.StartsWith("Chapters", StringComparison.OrdinalIgnoreCase))
        {
            return BluRayTrackKind.Chapters;
        }

        if (body.StartsWith("Subtitle", StringComparison.OrdinalIgnoreCase))
        {
            return BluRayTrackKind.Subtitle;
        }

        return body.Contains("AVC", StringComparison.OrdinalIgnoreCase)
            || body.Contains("HEVC", StringComparison.OrdinalIgnoreCase)
            || body.Contains("VC-1", StringComparison.OrdinalIgnoreCase)
            || body.Contains("MPEG2", StringComparison.OrdinalIgnoreCase)
                ? BluRayTrackKind.Video
                : BluRayTrackKind.Audio;
    }

    private static string ResolveLanguage(string body)
    {
        var parts = body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        return body.StartsWith("Subtitle", StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : parts[1].All(char.IsLetter)
                ? parts[1]
                : string.Empty;
    }

    private static string BuildDisplayName(int trackIndex, string body, string language)
    {
        var coreLabel = body.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        return string.IsNullOrWhiteSpace(language)
            ? $"#{trackIndex:00} · {coreLabel}"
            : $"#{trackIndex:00} · {coreLabel} · {language}";
    }

    private static double? ParseProgress(string line)
    {
        var match = ProgressRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value / 100.0, 0.0, 1.0)
            : null;
    }

    private static bool IsSuccessfulTerminalLine(string line)
    {
        return line.Equals("Done.", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan? TryParseDuration(string value)
    {
        return TimeSpan.TryParseExact(
            value,
            ["h\\:mm\\:ss", "hh\\:mm\\:ss"],
            CultureInfo.InvariantCulture,
            out var duration)
            ? duration
            : null;
    }

}
