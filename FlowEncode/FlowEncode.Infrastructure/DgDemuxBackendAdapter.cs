using System.Globalization;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class DgDemuxBackendAdapter : CliBluRayDemuxBackendAdapterBase
{
    private static readonly Regex PlaylistLineRegex = new(@"^(?<id>\d{5})\.mpls\s+(?<duration>\d{1,2}:\d{2}:\d{2})\s+\[(?<chapters>\d+)\s+chapters\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StreamLineRegex = new(@"^\*?\s*(?<pid>\d+):\s+(?<body>.+)$", RegexOptions.Compiled);
    private static readonly Regex ChapterLineRegex = new(@"^Chapters\s+\[(?<count>\d+)\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LanguageRegex = new(@"\[(?<lang>[^\]]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex ProgressRegex = new(@"(?<value>\d{1,3}(?:\.\d+)?)\s*%\s*$", RegexOptions.Compiled);

    public DgDemuxBackendAdapter(IToolProbeService toolProbeService)
        : base(toolProbeService)
    {
    }

    public override BluRayDemuxBackend Backend => BluRayDemuxBackend.DgDemux;

    public override async Task<IReadOnlyList<BluRayPlaylistItem>> ScanDiscAsync(string discPath, CancellationToken cancellationToken = default)
    {
        var dgDemuxPath = await ResolveToolPathAsync(RegisteredToolKind.DgDemux, cancellationToken);
        var startInfo = CreateStartInfo(dgDemuxPath);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(discPath);

        var capture = await CaptureProcessAsync(startInfo, cancellationToken);
        if (capture.ExitCode != 0)
        {
            throw new InvalidOperationException(MeaningfulLineHelpers.FirstMeaningfulLine(capture.Output, "DGDemux 无法读取蓝光目录。"));
        }

        var playlists = capture.Output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParsePlaylist)
            .Where(static item => item is not null)
            .Select(static item => item!)
            .ToList();

        if (playlists.Count == 0)
        {
            throw new InvalidOperationException("DGDemux 未返回可用播放列表。");
        }

        return playlists;

        BluRayPlaylistItem? ParsePlaylist(string line)
        {
            var match = PlaylistLineRegex.Match(line);
            if (!match.Success)
            {
                return null;
            }

            var id = match.Groups["id"].Value;
            var durationText = match.Groups["duration"].Value;
            var chapterCount = int.TryParse(match.Groups["chapters"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedChapterCount)
                ? parsedChapterCount
                : (int?)null;

            return new BluRayPlaylistItem(
                id,
                $"{id}.mpls",
                chapterCount.HasValue ? $"{durationText} · {chapterCount.Value} chapters" : durationText,
                Path.Combine(discPath, "BDMV", "PLAYLIST", $"{id}.mpls"),
                Path.Combine(discPath, "BDMV", "PLAYLIST", $"{id}.mpls"),
                durationText,
                TryParseDuration(durationText),
                chapterCount);
        }
    }

    public override async Task<BluRayPlaylistScanResult> ScanPlaylistAsync(string discPath, BluRayPlaylistItem playlist, CancellationToken cancellationToken = default)
    {
        var dgDemuxPath = await ResolveToolPathAsync(RegisteredToolKind.DgDemux, cancellationToken);
        var startInfo = CreateStartInfo(dgDemuxPath);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(playlist.SourcePath);

        var capture = await CaptureProcessAsync(startInfo, cancellationToken);
        if (capture.ExitCode != 0)
        {
            throw new InvalidOperationException(MeaningfulLineHelpers.FirstMeaningfulLine(capture.Output, "DGDemux 无法读取播放列表。"));
        }

        var lines = capture.Output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var streamSectionReached = false;
        var fileSectionReached = false;
        var fileCount = 0;
        var trackItems = new List<BluRayTrackItem>();
        var order = 1;

        foreach (var line in lines)
        {
            if (line.Equals("Files:", StringComparison.OrdinalIgnoreCase))
            {
                fileSectionReached = true;
                streamSectionReached = false;
                continue;
            }

            if (line.Equals("Streams:", StringComparison.OrdinalIgnoreCase))
            {
                streamSectionReached = true;
                fileSectionReached = false;
                continue;
            }

            if (fileSectionReached && line.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase))
            {
                fileCount++;
                continue;
            }

            if (!streamSectionReached)
            {
                continue;
            }

            var chapterMatch = ChapterLineRegex.Match(line);
            if (chapterMatch.Success)
            {
                var count = int.TryParse(chapterMatch.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount)
                    ? parsedCount
                    : 0;

                trackItems.Add(new BluRayTrackItem(
                    "chapters",
                    order++,
                    "Chapters",
                    BluRayTrackKind.Chapters,
                    $"Chapters · {count}",
                    line,
                    string.Empty));

                continue;
            }

            var streamMatch = StreamLineRegex.Match(line);
            if (!streamMatch.Success)
            {
                continue;
            }

            var pid = streamMatch.Groups["pid"].Value;
            var body = streamMatch.Groups["body"].Value.Trim();
            var language = string.Empty;

            var languageMatch = LanguageRegex.Match(body);
            if (languageMatch.Success)
            {
                language = languageMatch.Groups["lang"].Value.Trim();
                body = body[..languageMatch.Index].TrimEnd();
            }

            var kind = ResolveTrackKind(body);
            trackItems.Add(new BluRayTrackItem(
                pid,
                order++,
                pid,
                kind,
                BuildDisplayName(pid, kind, body, language),
                string.IsNullOrWhiteSpace(language) ? body : $"{body} [{language}]",
                language));
        }

        if (trackItems.Count == 0)
        {
            throw new InvalidOperationException("DGDemux 未解析出可选轨道。");
        }

        return new BluRayPlaylistScanResult(
            Backend,
            discPath,
            playlist,
            trackItems,
            fileCount > 0
                ? $"{playlist.DisplayName} · {fileCount} m2ts · {trackItems.Count} items"
                : $"{playlist.DisplayName} · {trackItems.Count} items",
            capture.Output);
    }

    public override async Task<BluRayDemuxResult> RunAsync(
        BluRayDemuxRequest request,
        IProgress<BluRayDemuxProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dgDemuxPath = await ResolveToolPathAsync(RegisteredToolKind.DgDemux, cancellationToken);
        Directory.CreateDirectory(request.OutputDirectory);

        var startInfo = CreateStartInfo(dgDemuxPath);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(request.Playlist.SourcePath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(request.OutputPrefixPath);
        startInfo.ArgumentList.Add("-demux");
        startInfo.ArgumentList.Add(string.Join(',', request.Selections.Select(static selection => selection.Track.DemuxToken)));

        return await RunProcessAsync(
            request,
            BuildDisplayCommand(request),
            startInfo,
            ParseProgress,
            IsSuccessfulTerminalLine,
            "DGDemux 解复用中",
            "DGDemux 解复用完成",
            "DGDemux 解复用已取消",
            "DGDemux 解复用失败",
            progress,
            cancellationToken);
    }

    public override string BuildDisplayCommand(BluRayDemuxRequest request)
    {
        var selectionToken = string.Join(',', request.Selections.Select(static selection => selection.Track.DemuxToken));
        return $"DGDemux.exe -i {Quote(request.Playlist.SourcePath)} -o {Quote(request.OutputPrefixPath)} -demux {selectionToken}";
    }

    private static BluRayTrackKind ResolveTrackKind(string body)
    {
        if (body.StartsWith("Video", StringComparison.OrdinalIgnoreCase))
        {
            return BluRayTrackKind.Video;
        }

        if (body.Contains("subtitle", StringComparison.OrdinalIgnoreCase))
        {
            return BluRayTrackKind.Subtitle;
        }

        return body.Contains("AC3", StringComparison.OrdinalIgnoreCase)
            || body.Contains("DTS", StringComparison.OrdinalIgnoreCase)
            || body.Contains("TrueHD", StringComparison.OrdinalIgnoreCase)
            || body.Contains("PCM", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Audio", StringComparison.OrdinalIgnoreCase)
                ? BluRayTrackKind.Audio
                : BluRayTrackKind.Other;
    }

    private static string BuildDisplayName(string pid, BluRayTrackKind kind, string body, string language)
    {
        return kind switch
        {
            BluRayTrackKind.Video => $"{pid} · {body}",
            BluRayTrackKind.Subtitle when !string.IsNullOrWhiteSpace(language) => $"{pid} · {body} · {language}",
            BluRayTrackKind.Audio when !string.IsNullOrWhiteSpace(language) => $"{pid} · {body} · {language}",
            _ => $"{pid} · {body}"
        };
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
        return line.Equals("Done!", StringComparison.OrdinalIgnoreCase);
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
