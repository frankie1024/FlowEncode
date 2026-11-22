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
    private static readonly Regex ChapterTrackCountRegex = new(@"^Chapters,\s*(?<count>\d+)\s+chapters", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MkvDurationRegex = new(@"\b(?<duration>\d{1,2}:\d{2}:\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex TrackLineRegex = new(@"^(?<index>\d+):\s+(?<body>.+)$", RegexOptions.Compiled);
    private static readonly Regex ProgressRegex = new(@"^process:\s*(?<value>\d{1,3}(?:\.\d+)?)\s*%$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] VideoCodecTokens =
    [
        "AVC",
        "HEVC",
        "H264",
        "H.264",
        "H265",
        "H.265",
        "VC-1",
        "MPEG1",
        "MPEG2",
        "MPEG4",
        "MPEG-4",
        "XVID",
        "DIVX",
        "VP8",
        "VP9",
        "AV1",
        "THEORA",
        "MJPG",
        "PRORES"
    ];

    private readonly LocalAppPaths _appPaths;
    private readonly object _mkvScanCacheGate = new();
    private MkvScanCacheEntry? _mkvScanCache;

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
        if (BluRayDemuxSourceSupport.IsMkvSourcePath(discPath))
        {
            var mkvItem = await ScanMkvSourceAsync(discPath, cancellationToken);
            return [mkvItem];
        }

        var eac3ToPath = await ResolveToolPathAsync(RegisteredToolKind.Eac3To, cancellationToken);
        Directory.CreateDirectory(_appPaths.LogsRootPath);
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
        var isMkvSource = BluRayDemuxSourceSupport.IsMkvSourcePath(discPath)
            || (string.IsNullOrWhiteSpace(playlist.SelectionToken) && BluRayDemuxSourceSupport.IsMkvSourcePath(playlist.SourcePath));
        if (isMkvSource)
        {
            return await ScanMkvPlaylistAsync(discPath, playlist, cancellationToken);
        }

        var eac3ToPath = await ResolveToolPathAsync(RegisteredToolKind.Eac3To, cancellationToken);
        Directory.CreateDirectory(_appPaths.LogsRootPath);
        var startInfo = CreateStartInfo(eac3ToPath, _appPaths.LogsRootPath);
        startInfo.ArgumentList.Add(discPath);
        if (!string.IsNullOrWhiteSpace(playlist.SelectionToken))
        {
            startInfo.ArgumentList.Add(playlist.SelectionToken);
        }

        var capture = await CaptureProcessAsync(startInfo, cancellationToken);
        if (capture.ExitCode != 0)
        {
            throw new InvalidOperationException(MeaningfulLineHelpers.FirstMeaningfulLine(capture.Output, "eac3to 无法读取播放列表。"));
        }

        var lines = capture.Output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var tracks = ParseTracks(lines);
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
        ValidateInput(request);
        var stagedRequest = CreateStagedRequest(request);
        var eac3ToPath = await ResolveToolPathAsync(RegisteredToolKind.Eac3To, cancellationToken);
        Directory.CreateDirectory(_appPaths.LogsRootPath);
        ExecutionOutputStaging.EnsureFreshDirectory(stagedRequest.OutputDirectory);

        var startInfo = CreateStartInfo(eac3ToPath, _appPaths.LogsRootPath);
        startInfo.ArgumentList.Add(stagedRequest.DiscPath);
        if (!string.IsNullOrWhiteSpace(stagedRequest.Playlist.SelectionToken))
        {
            startInfo.ArgumentList.Add(stagedRequest.Playlist.SelectionToken);
        }

        foreach (var selection in stagedRequest.Selections.OrderBy(static item => item.Track.Order))
        {
            startInfo.ArgumentList.Add($"{selection.Track.DemuxToken}:");
            startInfo.ArgumentList.Add(selection.OutputPath);
        }

        startInfo.ArgumentList.Add("-progressnumbers");

        var preserveStagedOutput = false;
        try
        {
            var result = await RunProcessAsync(
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

            if (result.State == EncodingJobState.Completed)
            {
                preserveStagedOutput = true;
                var outputs = ValidateStagedOutputs(request, stagedRequest.OutputDirectory);
                FinalizeStagedOutputDirectory(stagedRequest.OutputDirectory, request.OutputDirectory, request.OverwriteOutput);
                preserveStagedOutput = false;
                result = result with { OutputPaths = outputs };
                progress?.Report(new(request.JobId, EncodingJobState.Completed, 1, result.Summary, string.Empty));
            }

            return result;
        }
        finally
        {
            if (!preserveStagedOutput) CleanupStagedOutputDirectory(stagedRequest.OutputDirectory);
        }
    }

    public override string BuildDisplayCommand(BluRayDemuxRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("eac3to.exe ");
        builder.Append(Quote(request.DiscPath));
        if (!string.IsNullOrWhiteSpace(request.Playlist.SelectionToken))
        {
            builder.Append(' ');
            builder.Append(request.Playlist.SelectionToken);
        }

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

    private async Task<BluRayPlaylistItem> ScanMkvSourceAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(sourcePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"未找到 MKV 源文件：{fullPath}", fullPath);
        }

        var (playlist, scanResult) = await ProbeMkvCoreAsync(fullPath, existingPlaylist: null, cancellationToken);
        StoreMkvScanCache(fullPath, scanResult);
        return playlist;
    }

    private async Task<BluRayPlaylistScanResult> ScanMkvPlaylistAsync(
        string discPath,
        BluRayPlaylistItem playlist,
        CancellationToken cancellationToken)
    {
        var rawPath = BluRayDemuxSourceSupport.IsMkvSourcePath(discPath)
            ? discPath
            : playlist.SourcePath;
        var fullPath = Path.GetFullPath(rawPath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"未找到 MKV 源文件：{fullPath}", fullPath);
        }

        if (TryGetCachedMkvScan(fullPath, out var cachedResult))
        {
            return cachedResult with
            {
                DiscPath = discPath,
                Playlist = playlist
            };
        }

        var (_, scanResult) = await ProbeMkvCoreAsync(fullPath, playlist, cancellationToken);
        StoreMkvScanCache(fullPath, scanResult);
        return scanResult with { DiscPath = discPath };
    }

    private async Task<(BluRayPlaylistItem Playlist, BluRayPlaylistScanResult ScanResult)> ProbeMkvCoreAsync(
        string fullMkvPath,
        BluRayPlaylistItem? existingPlaylist,
        CancellationToken cancellationToken)
    {
        var eac3ToPath = await ResolveToolPathAsync(RegisteredToolKind.Eac3To, cancellationToken);
        Directory.CreateDirectory(_appPaths.LogsRootPath);
        var startInfo = CreateStartInfo(eac3ToPath, _appPaths.LogsRootPath);
        startInfo.ArgumentList.Add(fullMkvPath);

        var capture = await CaptureProcessAsync(startInfo, cancellationToken);
        if (capture.ExitCode != 0)
        {
            throw new InvalidOperationException(MeaningfulLineHelpers.FirstMeaningfulLine(capture.Output, "eac3to 无法读取 MKV 文件。"));
        }

        var lines = capture.Output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var tracks = ParseTracks(lines);
        if (tracks.Count == 0)
        {
            throw new InvalidOperationException("eac3to 未从 MKV 文件解析出可选轨道。");
        }

        var headerLine = lines.FirstOrDefault(static line => line.StartsWith("MKV,", StringComparison.OrdinalIgnoreCase));
        var durationText = TryExtractMkvDuration(headerLine);
        var chapterCount = TryExtractChapterCount(tracks);
        var fileName = Path.GetFileName(fullMkvPath);
        var playlist = existingPlaylist ?? new BluRayPlaylistItem(
            BluRayDemuxSourceSupport.SanitizeOutputStem(Path.GetFileNameWithoutExtension(fullMkvPath)),
            fileName,
            !string.IsNullOrWhiteSpace(headerLine)
                ? headerLine
                : BuildMkvFallbackSummary(durationText, tracks.Count, chapterCount),
            fullMkvPath,
            string.Empty,
            durationText,
            TryParseDuration(durationText),
            chapterCount);

        var scanResult = new BluRayPlaylistScanResult(
            Backend,
            fullMkvPath,
            playlist,
            tracks,
            $"{playlist.DisplayName} · {tracks.Count} items",
            capture.Output);

        return (playlist, scanResult);
    }

    private void StoreMkvScanCache(string fullMkvPath, BluRayPlaylistScanResult scanResult)
    {
        var cacheKey = TryBuildMkvFileStamp(fullMkvPath);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        lock (_mkvScanCacheGate)
        {
            _mkvScanCache = new MkvScanCacheEntry(cacheKey, scanResult);
        }
    }

    private bool TryGetCachedMkvScan(string fullMkvPath, out BluRayPlaylistScanResult scanResult)
    {
        scanResult = null!;
        var cacheKey = TryBuildMkvFileStamp(fullMkvPath);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return false;
        }

        lock (_mkvScanCacheGate)
        {
            if (_mkvScanCache is not null && string.Equals(_mkvScanCache.FileStamp, cacheKey, StringComparison.OrdinalIgnoreCase))
            {
                scanResult = _mkvScanCache.ScanResult;
                return true;
            }
        }

        return false;
    }

    private static string? TryBuildMkvFileStamp(string fullMkvPath)
    {
        try
        {
            var info = new FileInfo(fullMkvPath);
            return info.Exists
                ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static List<BluRayTrackItem> ParseTracks(IReadOnlyList<string> lines)
    {
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

            if (currentTrack is not null && (line.StartsWith('(') || line.StartsWith('"')))
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

        return tracks;
    }

    private static string TryExtractMkvDuration(string? headerLine)
    {
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return string.Empty;
        }

        var match = MkvDurationRegex.Match(headerLine);
        return match.Success ? match.Groups["duration"].Value : string.Empty;
    }

    private static int? TryExtractChapterCount(IReadOnlyList<BluRayTrackItem> tracks)
    {
        foreach (var track in tracks)
        {
            if (track.Kind != BluRayTrackKind.Chapters)
            {
                continue;
            }

            var match = ChapterTrackCountRegex.Match(track.Detail);
            if (match.Success
                && int.TryParse(match.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                return count;
            }
        }

        return null;
    }

    private static string BuildMkvFallbackSummary(string durationText, int trackCount, int? chapterCount)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(durationText))
        {
            parts.Add(durationText);
        }

        if (chapterCount.HasValue)
        {
            parts.Add($"{chapterCount.Value} chapters");
        }

        parts.Add($"{trackCount} tracks");
        return string.Join(" · ", parts);
    }

    private static string ResolvePlaylistId(string source)
    {
        var index = source.IndexOf(".mpls", StringComparison.OrdinalIgnoreCase);
        return index <= 0
            ? string.Empty
            : source[..index];
    }

    internal static BluRayTrackKind ResolveTrackKind(string body)
    {
        if (body.StartsWith("Chapters", StringComparison.OrdinalIgnoreCase))
        {
            return BluRayTrackKind.Chapters;
        }

        if (body.StartsWith("Subtitle", StringComparison.OrdinalIgnoreCase))
        {
            return BluRayTrackKind.Subtitle;
        }

        if (body.StartsWith("Video", StringComparison.OrdinalIgnoreCase))
        {
            return BluRayTrackKind.Video;
        }

        foreach (var token in VideoCodecTokens)
        {
            if (body.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return BluRayTrackKind.Video;
            }
        }

        return BluRayTrackKind.Audio;
    }

    internal static string ResolveLanguage(string body)
    {
        var parts = body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        var candidate = parts[1].Trim();
        if (candidate.Length >= 3 && candidate.StartsWith('[') && candidate.EndsWith(']'))
        {
            candidate = candidate[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        if (body.StartsWith("Subtitle", StringComparison.OrdinalIgnoreCase))
        {
            return char.IsDigit(candidate[0]) ? string.Empty : candidate;
        }

        return candidate.All(static character => char.IsLetter(character) || character == '-')
            ? candidate
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

    private static BluRayDemuxRequest CreateStagedRequest(BluRayDemuxRequest request)
    {
        var stagedOutputDirectory = CreateStagedOutputDirectory(request.OutputDirectory, "bluray-demux", request.JobId);
        var stagedSelections = request.Selections
            .Select(selection => selection with
            {
                OutputPath = Path.Combine(stagedOutputDirectory, Path.GetFileName(selection.OutputPath))
            })
            .ToList();

        return request with
        {
            OutputDirectory = stagedOutputDirectory,
            OutputPrefixPath = Path.Combine(stagedOutputDirectory, Path.GetFileName(request.OutputPrefixPath)),
            Selections = stagedSelections
        };
    }

    private static void FinalizeStagedOutputDirectory(string stagedOutputDirectory, string finalOutputDirectory, bool overwrite)
    {
        try
        {
            ExecutionOutputStaging.FinalizeDirectory(stagedOutputDirectory, finalOutputDirectory, overwrite);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"eac3to completed but failed to finalize the output directory '{finalOutputDirectory}'. {ex.Message}",
                ex);
        }
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

    private sealed record MkvScanCacheEntry(string FileStamp, BluRayPlaylistScanResult ScanResult);
}
