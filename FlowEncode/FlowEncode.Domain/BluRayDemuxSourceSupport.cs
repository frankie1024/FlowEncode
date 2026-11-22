using System;
using System.IO;
using System.Linq;
using System.Text;

namespace FlowEncode.Domain;

public static class BluRayDemuxSourceSupport
{
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
}
