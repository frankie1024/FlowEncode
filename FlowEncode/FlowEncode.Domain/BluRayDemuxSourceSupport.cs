using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FlowEncode.Domain;

public static class BluRayDemuxSourceSupport
{
    public const string MkvChaptersDemuxToken = "mkv-chapters";

    public static bool IsMkvChaptersTrack(BluRayTrackItem track)
    {
        return track.Kind == BluRayTrackKind.Chapters
            && string.Equals(track.DemuxToken, MkvChaptersDemuxToken, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMkvSourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return Path.GetExtension(path.Trim()).Equals(".mkv", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool SupportsFileSource(BluRayDemuxBackend backend, string? path)
    {
        return backend == BluRayDemuxBackend.Eac3To && IsMkvSourcePath(path);
    }

    public static bool IsMkvDemuxTarget(BluRayDemuxRequest request)
    {
        return IsMkvSourcePath(request.DiscPath) || IsMkvSourcePath(request.Playlist.SourcePath);
    }

    public static string ResolveExecutionSourcePath(BluRayDemuxRequest request)
    {
        return IsMkvSourcePath(request.Playlist.SourcePath)
            ? request.Playlist.SourcePath
            : request.DiscPath;
    }

    public static void ValidateExecutionSource(BluRayDemuxRequest request)
    {
        var isMkvTarget = IsMkvDemuxTarget(request);
        if (isMkvTarget)
        {
            if (request.Backend != BluRayDemuxBackend.Eac3To)
                throw new InvalidOperationException("MKV demuxing requires eac3to / MKV 解复用仅支持 eac3to 后端。");
            if (!File.Exists(request.DiscPath) && !Directory.Exists(request.DiscPath))
                throw new FileNotFoundException($"MKV source not found / 未找到 MKV 输入源：{request.DiscPath}", request.DiscPath);
        }
        else if (!Directory.Exists(request.DiscPath))
        {
            throw new DirectoryNotFoundException($"Blu-ray source directory not found / 未找到蓝光源目录：{request.DiscPath}");
        }

        if (!File.Exists(request.Playlist.SourcePath))
            throw new FileNotFoundException(
                isMkvTarget
                    ? "MKV source file not found / 未找到 MKV 源文件。"
                    : "Blu-ray playlist not found / 未找到蓝光播放列表。",
                request.Playlist.SourcePath);
    }

    public static bool IsBluRayDiscDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(directoryPath.Trim());
            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            if (Directory.Exists(Path.Combine(fullPath, "BDMV", "PLAYLIST")))
            {
                return true;
            }

            if (Path.GetFileName(fullPath).Equals("BDMV", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(fullPath, "PLAYLIST")))
            {
                return true;
            }

            if (Path.GetFileName(fullPath).Equals("PLAYLIST", StringComparison.OrdinalIgnoreCase))
            {
                var bdmvDirectory = Directory.GetParent(fullPath);
                return bdmvDirectory is not null
                    && bdmvDirectory.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase)
                    && bdmvDirectory.Parent is not null;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> FindMkvFilesInDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return [];
        }

        try
        {
            var fullPath = Path.GetFullPath(directoryPath.Trim());
            if (!Directory.Exists(fullPath))
            {
                return [];
            }

            return Directory.EnumerateFiles(fullPath, "*.mkv", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => Path.GetFileName(path), NaturalFileNameComparer.Instance)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? string.Format(CultureInfo.InvariantCulture, "{0:0} {1}", value, units[unitIndex])
            : string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[unitIndex]);
    }

    public static string? TryResolveBluRayDiscRootFromEntryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch
        {
            return null;
        }

        var fileName = Path.GetFileName(fullPath);
        var extension = Path.GetExtension(fullPath);
        var parentDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return null;
        }

        if (fileName.Equals("index.bdmv", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("MovieObject.bdmv", StringComparison.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(parentDirectory).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            {
                return Directory.GetParent(parentDirectory)?.FullName;
            }
        }

        if (extension.Equals(".mpls", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(parentDirectory).Equals("PLAYLIST", StringComparison.OrdinalIgnoreCase))
        {
            var bdmvDirectory = Directory.GetParent(parentDirectory);
            if (bdmvDirectory is not null
                && bdmvDirectory.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            {
                return bdmvDirectory.Parent?.FullName;
            }
        }

        return null;
    }

    public static string SanitizeOutputStem(string? value, string fallback = "source")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (character is '*' or '?' || invalidCharacters.Contains(character))
            {
                continue;
            }

            builder.Append(character);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    public static string SanitizeFileToken(string? value, string fallback = "track")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (character is '*' or '?' || invalidCharacters.Contains(character))
            {
                continue;
            }

            builder.Append(char.IsWhiteSpace(character) ? '_' : char.ToLowerInvariant(character));
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }

    public static string ResolveEac3ToTrackOutputExtension(BluRayTrackItem track)
    {
        if (track.Kind == BluRayTrackKind.Chapters)
        {
            return ".txt";
        }

        if (track.Kind == BluRayTrackKind.Subtitle)
        {
            var descriptor = string.IsNullOrWhiteSpace(track.Detail) ? track.DisplayName : track.Detail;
            if (descriptor.Contains("(SRT)", StringComparison.OrdinalIgnoreCase)
                || descriptor.Contains("(UTF8)", StringComparison.OrdinalIgnoreCase))
            {
                return ".srt";
            }

            if (descriptor.Contains("(ASS)", StringComparison.OrdinalIgnoreCase))
            {
                return ".ass";
            }

            if (descriptor.Contains("(SSA)", StringComparison.OrdinalIgnoreCase))
            {
                return ".ssa";
            }

            if (descriptor.Contains("(PGS)", StringComparison.OrdinalIgnoreCase))
            {
                return ".sup";
            }
        }

        return ".*";
    }

    public static string ResolveTrackOutputPath(
        BluRayDemuxBackend backend,
        string outputDirectory,
        BluRayPlaylistItem playlist,
        BluRayTrackItem track)
    {
        if (backend == BluRayDemuxBackend.DgDemux)
        {
            return Path.Combine(outputDirectory, $"{playlist.Id}.*");
        }

        var stem = SanitizeOutputStem(playlist.Id, "source");
        var baseName = $"{stem}_T{track.Order:00}_{GetTrackKindToken(track.Kind)}";
        if (!string.IsNullOrWhiteSpace(track.Language))
        {
            baseName = $"{baseName}_{SanitizeFileToken(track.Language)}";
        }

        return Path.Combine(outputDirectory, $"{baseName}{ResolveEac3ToTrackOutputExtension(track)}");
    }

    private static string GetTrackKindToken(BluRayTrackKind kind) => kind switch
    {
        BluRayTrackKind.Chapters => "chapters",
        BluRayTrackKind.Video => "video",
        BluRayTrackKind.Audio => "audio",
        BluRayTrackKind.Subtitle => "subtitle",
        _ => "track"
    };

    private sealed class NaturalFileNameComparer : IComparer<string>
    {
        public static NaturalFileNameComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var ix = 0;
            var iy = 0;
            while (ix < x.Length && iy < y.Length)
            {
                if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
                {
                    var sx = ix;
                    while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                    var sy = iy;
                    while (iy < y.Length && char.IsDigit(y[iy])) iy++;

                    var nx = x.AsSpan(sx, ix - sx).TrimStart('0');
                    var ny = y.AsSpan(sy, iy - sy).TrimStart('0');
                    if (nx.Length != ny.Length) return nx.Length.CompareTo(ny.Length);
                    var numCmp = nx.SequenceCompareTo(ny);
                    if (numCmp != 0) return numCmp;
                    if ((ix - sx) != (iy - sy)) return (ix - sx).CompareTo(iy - sy);
                    continue;
                }

                var cx = char.ToUpperInvariant(x[ix]);
                var cy = char.ToUpperInvariant(y[iy]);
                if (cx != cy) return cx.CompareTo(cy);
                ix++;
                iy++;
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}
