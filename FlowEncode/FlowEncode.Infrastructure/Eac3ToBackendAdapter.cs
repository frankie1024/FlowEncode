using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class Eac3ToBackendAdapter : CliBluRayDemuxBackendAdapterBase
{
    private static readonly Regex TitleLineRegex = new(@"^(?<index>\d+)\)\s+(?<body>.+)$", RegexOptions.Compiled);
    private static readonly Regex MplsFileRegex = new(@"(?<id>[^,\s\\/\(\)\[\]]+)\.mpls\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ChapterSummaryRegex = new(@"^-\s*Chapters,\s*(?<count>\d+)\s+chapters", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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

    protected override bool UseWindowsAnsiOutputDecoding => true;

    public override async Task<IReadOnlyList<BluRayPlaylistItem>> ScanDiscAsync(string discPath, CancellationToken cancellationToken = default)
    {
        if (BluRayDemuxSourceSupport.IsMkvSourcePath(discPath))
        {
            var mkvItem = await ScanMkvSourceAsync(discPath, cancellationToken);
            return [mkvItem];
        }

        if (Directory.Exists(discPath) && !BluRayDemuxSourceSupport.IsBluRayDiscDirectory(discPath))
        {
            var mkvFiles = BluRayDemuxSourceSupport.FindMkvFilesInDirectory(discPath);
            if (mkvFiles.Count > 0)
            {
                await ResolveToolPathAsync(RegisteredToolKind.Eac3To, cancellationToken);
                return BuildMkvDirectoryItems(mkvFiles);
            }
        }

        var eac3ToPath = await ResolveToolPathAsync(RegisteredToolKind.Eac3To, cancellationToken);
        Directory.CreateDirectory(_appPaths.LogsRootPath);

        var capture = await CaptureBluRayScanAsync(eac3ToPath, discPath, includeMinDuration: true, cancellationToken);
        var lines = NormalizeEac3ToOutputLines(capture.Output);
        var playlists = capture.ExitCode == 0 ? ParsePlaylists(discPath, lines) : [];

        if (playlists.Count == 0)
        {
            var fallbackCapture = await CaptureBluRayScanAsync(eac3ToPath, discPath, includeMinDuration: false, cancellationToken);
            var fallbackLines = NormalizeEac3ToOutputLines(fallbackCapture.Output);
            var fallbackPlaylists = fallbackCapture.ExitCode == 0 ? ParsePlaylists(discPath, fallbackLines) : [];
            if (fallbackPlaylists.Count > 0 || (capture.ExitCode != 0 && fallbackCapture.ExitCode == 0))
            {
                capture = fallbackCapture;
                lines = fallbackLines;
                playlists = fallbackPlaylists;
            }
        }

        if (capture.ExitCode != 0)
        {
            throw new InvalidOperationException(FirstEac3ToMeaningfulLine(lines, "eac3to 无法读取蓝光目录。"));
        }

        if (playlists.Count == 0)
        {
            var detail = FirstEac3ToMeaningfulLine(lines, string.Empty);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "eac3to 未返回可用播放列表。"
                : $"eac3to 未返回可用播放列表（{detail}）。");
        }

        return playlists;
    }

    public override async Task<BluRayPlaylistScanResult> ScanPlaylistAsync(string discPath, BluRayPlaylistItem playlist, CancellationToken cancellationToken = default)
    {
        var isMkvSource = BluRayDemuxSourceSupport.IsMkvSourcePath(playlist.SourcePath)
            || BluRayDemuxSourceSupport.IsMkvSourcePath(discPath);
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
        var lines = NormalizeEac3ToOutputLines(capture.Output);
        if (capture.ExitCode != 0)
        {
            throw new InvalidOperationException(FirstEac3ToMeaningfulLine(lines, "eac3to 无法读取播放列表。"));
        }

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
        var orderedSelections = stagedRequest.Selections.OrderBy(static item => item.Track.Order).ToList();
        var mkvChapterSelections = orderedSelections.Where(static item => BluRayDemuxSourceSupport.IsMkvChaptersTrack(item.Track)).ToList();
        var eac3ToSelections = orderedSelections.Where(static item => !BluRayDemuxSourceSupport.IsMkvChaptersTrack(item.Track)).ToList();
        var sourcePath = BluRayDemuxSourceSupport.ResolveExecutionSourcePath(stagedRequest);
        var displayCommand = BuildDisplayCommand(request);

        ExecutionOutputStaging.EnsureFreshDirectory(stagedRequest.OutputDirectory);

        var preserveStagedOutput = false;
        try
        {
            BluRayDemuxResult result;
            if (eac3ToSelections.Count == 0)
            {
                progress?.Report(new(request.JobId, EncodingJobState.Running, null, "正在提取 MKV 章节...", string.Empty));
                var chapterLog = await ExportMkvChaptersAsync(sourcePath, mkvChapterSelections, cancellationToken);
                result = new BluRayDemuxResult(
                    request.JobId,
                    EncodingJobState.Completed,
                    0,
                    "MKV 章节提取完成",
                    chapterLog,
                    displayCommand,
                    request.Selections.Select(static selection => selection.OutputPath).ToList());
            }
            else
            {
                var eac3ToPath = await ResolveToolPathAsync(RegisteredToolKind.Eac3To, cancellationToken);
                Directory.CreateDirectory(_appPaths.LogsRootPath);

                var startInfo = CreateStartInfo(eac3ToPath, _appPaths.LogsRootPath);
                startInfo.ArgumentList.Add(sourcePath);
                if (!string.IsNullOrWhiteSpace(stagedRequest.Playlist.SelectionToken))
                {
                    startInfo.ArgumentList.Add(stagedRequest.Playlist.SelectionToken);
                }

                foreach (var selection in eac3ToSelections)
                {
                    startInfo.ArgumentList.Add($"{selection.Track.DemuxToken}:");
                    startInfo.ArgumentList.Add(selection.OutputPath);
                }

                startInfo.ArgumentList.Add("-progressnumbers");

                result = await RunProcessAsync(
                    request,
                    displayCommand,
                    startInfo,
                    ParseProgress,
                    IsSuccessfulTerminalLine,
                    "eac3to 解复用中",
                    "eac3to 解复用完成",
                    "eac3to 解复用已取消",
                    "eac3to 解复用失败",
                    progress,
                    cancellationToken);

                if (result.State == EncodingJobState.Completed && mkvChapterSelections.Count > 0)
                {
                    var chapterLog = await ExportMkvChaptersAsync(sourcePath, mkvChapterSelections, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(chapterLog))
                    {
                        result = result with
                        {
                            Log = string.IsNullOrWhiteSpace(result.Log)
                                ? chapterLog
                                : $"{result.Log}{Environment.NewLine}{chapterLog}"
                        };
                    }
                }
            }

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
        var sourcePath = BluRayDemuxSourceSupport.ResolveExecutionSourcePath(request);
        var orderedSelections = request.Selections.OrderBy(static item => item.Track.Order).ToList();
        var eac3ToSelections = orderedSelections.Where(static item => !BluRayDemuxSourceSupport.IsMkvChaptersTrack(item.Track)).ToList();
        var mkvChapterSelections = orderedSelections.Where(static item => BluRayDemuxSourceSupport.IsMkvChaptersTrack(item.Track)).ToList();
        var commands = new List<string>();

        if (eac3ToSelections.Count > 0)
        {
            var builder = new StringBuilder();
            builder.Append("eac3to.exe ");
            builder.Append(Quote(sourcePath));
            if (!string.IsNullOrWhiteSpace(request.Playlist.SelectionToken))
            {
                builder.Append(' ');
                builder.Append(request.Playlist.SelectionToken);
            }

            foreach (var selection in eac3ToSelections)
            {
                builder.Append(' ');
                builder.Append(selection.Track.DemuxToken);
                builder.Append(':');
                builder.Append(' ');
                builder.Append(Quote(selection.OutputPath));
            }

            builder.Append(" -progressnumbers");
            commands.Add(builder.ToString());
        }

        foreach (var chapterSelection in mkvChapterSelections)
        {
            commands.Add($"ffprobe.exe -v error -show_chapters -of json {Quote(sourcePath)} -> {Quote(chapterSelection.OutputPath)}");
        }

        return string.Join(Environment.NewLine, commands);
    }

    private static IReadOnlyList<BluRayPlaylistItem> BuildMkvDirectoryItems(IReadOnlyList<string> mkvFiles)
    {
        var items = new List<BluRayPlaylistItem>(mkvFiles.Count);
        foreach (var mkvFile in mkvFiles)
        {
            var fullPath = Path.GetFullPath(mkvFile);
            var fileName = Path.GetFileName(fullPath);
            var summary = "MKV";
            try
            {
                var info = new FileInfo(fullPath);
                if (info.Exists)
                {
                    summary = $"MKV · {BluRayDemuxSourceSupport.FormatFileSize(info.Length)}";
                }
            }
            catch
            {
                // Fall back to plain MKV summary if file metadata cannot be read.
            }

            items.Add(new BluRayPlaylistItem(
                BluRayDemuxSourceSupport.SanitizeOutputStem(Path.GetFileNameWithoutExtension(fullPath)),
                fileName,
                summary,
                fullPath,
                string.Empty,
                string.Empty,
                null,
                null));
        }

        return items;
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
        var rawPath = BluRayDemuxSourceSupport.IsMkvSourcePath(playlist.SourcePath)
            ? playlist.SourcePath
            : discPath;
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
        var lines = NormalizeEac3ToOutputLines(capture.Output);
        if (capture.ExitCode != 0)
        {
            throw new InvalidOperationException(FirstEac3ToMeaningfulLine(lines, "eac3to 无法读取 MKV 文件。"));
        }

        var tracks = ParseTracks(lines);
        if (!tracks.Any(static track => track.Kind == BluRayTrackKind.Chapters))
        {
            var chapters = await ProbeMkvChaptersAsync(fullMkvPath, cancellationToken);
            if (chapters.Count > 0)
            {
                var nextOrder = tracks.Count > 0 ? tracks.Max(static track => track.Order) + 1 : 1;
                tracks.Add(new BluRayTrackItem(
                    "chapters",
                    nextOrder,
                    BluRayDemuxSourceSupport.MkvChaptersDemuxToken,
                    BluRayTrackKind.Chapters,
                    $"#{nextOrder:00} · Chapters",
                    $"Chapters, {chapters.Count} chapters",
                    string.Empty));
            }
        }

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

    private Task<ProcessCaptureResult> CaptureBluRayScanAsync(
        string eac3ToPath,
        string discPath,
        bool includeMinDuration,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(eac3ToPath, _appPaths.LogsRootPath);
        startInfo.ArgumentList.Add(discPath);
        if (includeMinDuration)
        {
            startInfo.ArgumentList.Add("-minDuration=0");
        }

        return CaptureProcessAsync(startInfo, cancellationToken);
    }

    internal static List<string> NormalizeEac3ToOutputLines(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var segments = output.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries);
        var normalizedLines = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var resolved = ApplyBackspaces(segment);
            var normalized = ConsoleOutputLineNormalizer.Normalize(resolved);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                normalizedLines.Add(normalized);
            }
        }

        return normalizedLines;
    }

    internal static List<BluRayPlaylistItem> ParsePlaylists(string discPath, IReadOnlyList<string> lines)
    {
        var playlists = new List<BluRayPlaylistItem>();
        for (var index = 0; index < lines.Count; index++)
        {
            var titleMatch = TitleLineRegex.Match(lines[index]);
            if (!titleMatch.Success)
            {
                continue;
            }

            var titleIndex = titleMatch.Groups["index"].Value;
            var body = titleMatch.Groups["body"].Value.Trim();
            var playlistId = ResolvePlaylistId(body);
            if (string.IsNullOrWhiteSpace(playlistId))
            {
                continue;
            }

            var durationText = TryExtractMkvDuration(body);
            int? chapterCount = null;

            for (var lookAhead = index + 1; lookAhead < lines.Count; lookAhead++)
            {
                if (TitleLineRegex.IsMatch(lines[lookAhead]))
                {
                    break;
                }

                var chapterMatch = ChapterSummaryRegex.Match(lines[lookAhead]);
                if (chapterMatch.Success
                    && int.TryParse(chapterMatch.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount))
                {
                    chapterCount = parsedCount;
                    break;
                }
            }

            var summary = BuildPlaylistSummary(durationText, chapterCount, body);
            playlists.Add(new BluRayPlaylistItem(
                playlistId,
                $"{playlistId}.mpls",
                summary,
                Path.Combine(discPath, "BDMV", "PLAYLIST", $"{playlistId}.mpls"),
                $"{titleIndex})",
                durationText,
                TryParseDuration(durationText),
                chapterCount));
        }

        return playlists;
    }

    private static string ApplyBackspaces(string value)
    {
        if (!value.Contains('\b', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character == '\b')
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string BuildPlaylistSummary(string durationText, int? chapterCount, string fallbackBody)
    {
        if (!string.IsNullOrWhiteSpace(durationText))
        {
            return chapterCount.HasValue
                ? $"{durationText} · {chapterCount.Value} chapters"
                : durationText;
        }

        return chapterCount.HasValue
            ? $"{chapterCount.Value} chapters"
            : fallbackBody;
    }

    private static string FirstEac3ToMeaningfulLine(IReadOnlyList<string> lines, string fallback)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)
                || line.Equals("Fast mode enabled", StringComparison.OrdinalIgnoreCase)
                || line.Equals("Keeping dialnorm", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("analyze:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line;
        }

        return lines.FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line)) ?? fallback;
    }

    private static string ResolvePlaylistId(string source)
    {
        var match = MplsFileRegex.Match(source);
        return match.Success ? match.Groups["id"].Value : string.Empty;
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

    private async Task<IReadOnlyList<MkvChapterEntry>> ProbeMkvChaptersAsync(
        string fullMkvPath,
        CancellationToken cancellationToken,
        bool throwOnError = false)
    {
        var ffprobePath = await TryResolveToolPathAsync(RegisteredToolKind.Ffprobe, cancellationToken);
        if (string.IsNullOrWhiteSpace(ffprobePath))
        {
            if (throwOnError)
            {
                throw new InvalidOperationException("未找到可用的 ffprobe，无法提取 MKV 章节。");
            }

            return [];
        }

        try
        {
            var startInfo = CreateStartInfo(ffprobePath);
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-show_chapters");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add(fullMkvPath);

            var result = await ProcessProbeRunner.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(30),
                "MKV chapter probe timed out / MKV 章节扫描超时。",
                cancellationToken);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                if (throwOnError)
                {
                    throw new InvalidOperationException(MeaningfulLineHelpers.FirstMeaningfulLine(result.StandardError, "ffprobe 无法读取 MKV 章节。"));
                }

                return [];
            }

            return ParseFfprobeChaptersJson(result.StandardOutput);
        }
        catch when (!throwOnError)
        {
            return [];
        }
    }

    private async Task<string> ExportMkvChaptersAsync(
        string sourcePath,
        IReadOnlyList<BluRayTrackSelection> chapterSelections,
        CancellationToken cancellationToken)
    {
        var chapters = await ProbeMkvChaptersAsync(sourcePath, cancellationToken, throwOnError: true);
        if (chapters.Count == 0)
        {
            throw new InvalidOperationException("未在 MKV 文件中检测到可导出的章节。");
        }

        var content = FormatOggChapters(chapters);
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var logLines = new List<string>(chapterSelections.Count);
        foreach (var selection in chapterSelections)
        {
            var directory = Path.GetDirectoryName(selection.OutputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(selection.OutputPath, content, utf8NoBom, cancellationToken);
            logLines.Add($"Exported {chapters.Count} MKV chapters -> {Path.GetFileName(selection.OutputPath)}");
        }

        return string.Join(Environment.NewLine, logLines);
    }

    internal static IReadOnlyList<MkvChapterEntry> ParseFfprobeChaptersJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("chapters", out var chaptersElement)
            || chaptersElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var entries = new List<MkvChapterEntry>();
        foreach (var item in chaptersElement.EnumerateArray())
        {
            var startMs = TryReadChapterStartMilliseconds(item);
            var title = string.Empty;
            if (item.TryGetProperty("tags", out var tags)
                && tags.ValueKind == JsonValueKind.Object
                && tags.TryGetProperty("title", out var titleElement)
                && titleElement.ValueKind == JsonValueKind.String)
            {
                title = titleElement.GetString()?.Trim() ?? string.Empty;
            }

            entries.Add(new MkvChapterEntry(startMs, title));
        }

        return entries;
    }

    internal static string FormatOggChapters(IReadOnlyList<MkvChapterEntry> chapters)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < chapters.Count; index++)
        {
            var number = index + 1;
            var chapter = chapters[index];
            builder.AppendLine($"CHAPTER{number:00}={FormatChapterTimecode(chapter.StartMilliseconds)}");
            builder.AppendLine($"CHAPTER{number:00}NAME={chapter.Title}");
        }

        return builder.ToString();
    }

    private static long TryReadChapterStartMilliseconds(JsonElement item)
    {
        if (item.TryGetProperty("start_time", out var startTimeElement))
        {
            var raw = startTimeElement.ValueKind == JsonValueKind.String
                ? startTimeElement.GetString()
                : startTimeElement.GetRawText();
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && !double.IsNaN(seconds)
                && !double.IsInfinity(seconds))
            {
                return Math.Max(0L, (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero));
            }
        }

        return 0L;
    }

    private static string FormatChapterTimecode(long totalMilliseconds)
    {
        var clamped = Math.Max(0L, totalMilliseconds);
        var hours = clamped / 3_600_000;
        var minutes = (clamped % 3_600_000) / 60_000;
        var seconds = (clamped % 60_000) / 1_000;
        var milliseconds = clamped % 1_000;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}.{3:000}",
            hours,
            minutes,
            seconds,
            milliseconds);
    }

    private sealed record MkvScanCacheEntry(string FileStamp, BluRayPlaylistScanResult ScanResult);

    internal sealed record MkvChapterEntry(long StartMilliseconds, string Title);
}
