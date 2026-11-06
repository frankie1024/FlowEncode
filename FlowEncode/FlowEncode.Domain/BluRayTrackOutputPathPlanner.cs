using System.Text;

namespace FlowEncode.Domain;

/// <summary>
/// Output naming for demuxed Blu-ray tracks, shared by the desktop module and automation clients.
/// </summary>
public static class BluRayTrackOutputPathPlanner
{
    public static string BuildOutputPrefixPath(string outputDirectory, BluRayPlaylistItem playlist)
    {
        return Path.Combine(outputDirectory, playlist.Id);
    }

    public static string BuildTrackOutputPath(
        BluRayDemuxBackend backend,
        string outputDirectory,
        BluRayPlaylistItem playlist,
        BluRayTrackItem track)
    {
        if (backend == BluRayDemuxBackend.DgDemux)
        {
            return Path.Combine(outputDirectory, $"{playlist.Id}.*");
        }

        var baseName = $"{playlist.Id}_T{track.Order:00}_{GetTrackKindToken(track.Kind)}";
        if (!string.IsNullOrWhiteSpace(track.Language))
        {
            baseName = $"{baseName}_{SanitizeFileToken(track.Language)}";
        }

        return track.Kind == BluRayTrackKind.Chapters
            ? Path.Combine(outputDirectory, $"{baseName}.txt")
            : Path.Combine(outputDirectory, $"{baseName}.*");
    }

    private static string GetTrackKindToken(BluRayTrackKind kind) => kind switch
    {
        BluRayTrackKind.Chapters => "chapters",
        BluRayTrackKind.Video => "video",
        BluRayTrackKind.Audio => "audio",
        BluRayTrackKind.Subtitle => "subtitle",
        _ => "track"
    };

    private static string SanitizeFileToken(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (!invalidCharacters.Contains(character))
            {
                builder.Append(char.IsWhiteSpace(character) ? '_' : char.ToLowerInvariant(character));
            }
        }

        return builder.Length == 0 ? "track" : builder.ToString();
    }
}
