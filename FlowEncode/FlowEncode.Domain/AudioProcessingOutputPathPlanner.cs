namespace FlowEncode.Domain;

/// <summary>
/// Output naming shared by the desktop audio module and automation clients, so both produce
/// the same file for the same source and workflow.
/// </summary>
public static class AudioProcessingOutputPathPlanner
{
    private static readonly HashSet<string> UnsupportedDirectAudioContainerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv",
        ".mp4",
        ".m2ts",
        ".ts",
        ".avi",
        ".flv",
        ".mov"
    };

    /// <summary>
    /// eac3to reads elementary audio streams only; video containers have to be demuxed first.
    /// </summary>
    public static bool IsDirectSourceUnsupported(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        return !string.IsNullOrWhiteSpace(extension) && UnsupportedDirectAudioContainerExtensions.Contains(extension);
    }

    /// <summary>
    /// DDP (deew) writes into a directory and names the file itself; the other workflows produce a single file.
    /// </summary>
    public static string BuildOutputPath(
        string sourcePath,
        string outputDirectory,
        AudioProcessingMode workflow,
        AudioEac3ToOutputFormat? eac3ToOutputFormat)
    {
        if (workflow == AudioProcessingMode.Ddp)
        {
            return outputDirectory;
        }

        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "audio";
        }

        var extension = workflow switch
        {
            AudioProcessingMode.Eac3To when eac3ToOutputFormat == AudioEac3ToOutputFormat.Ac3 => "ac3",
            AudioProcessingMode.Eac3To => "flac",
            AudioProcessingMode.Opus => "opus",
            _ => "audio"
        };

        return Path.Combine(outputDirectory, $"{fileName}.{extension}");
    }
}
