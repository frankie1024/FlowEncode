using System;
using System.Linq;

namespace FlowEncode.Domain;

public static class AudioSourceSupport
{
    private static readonly IReadOnlyList<string> FfmpegBackedPreferredPickerExtensions =
    [
        ".flac",
        ".wav",
        ".w64",
        ".aiff",
        ".aif",
        ".aifc",
        ".caf",
        ".ac3",
        ".eac3",
        ".ec3",
        ".eb3",
        ".dts",
        ".dtshd",
        ".thd",
        ".truehd",
        ".mlp",
        ".mka",
        ".opus",
        ".ogg",
        ".oga",
        ".aac",
        ".adts",
        ".m4a",
        ".m4b",
        ".mp3"
    ];

    private static readonly IReadOnlyList<string> Eac3ToPreferredPickerExtensions =
    [
        ".flac",
        ".wav",
        ".w64",
        ".rf64",
        ".ac3",
        ".eac3",
        ".ec3",
        ".dts",
        ".dtshd",
        ".thd",
        ".truehd",
        ".mlp",
        ".pcm",
        ".raw",
        ".mpa"
    ];

    private static readonly IReadOnlyList<string> AllSupportedExtensions = FfmpegBackedPreferredPickerExtensions
        .Concat(Eac3ToPreferredPickerExtensions)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static IReadOnlyList<string> PreferredPickerExtensions => FfmpegBackedPreferredPickerExtensions;

    public static IReadOnlyList<string> SupportedDropExtensions => AllSupportedExtensions;

    public static IReadOnlyList<string> GetPreferredPickerExtensions(AudioProcessingMode? mode)
    {
        return mode == AudioProcessingMode.Eac3To
            ? Eac3ToPreferredPickerExtensions
            : FfmpegBackedPreferredPickerExtensions;
    }

    public static string PreferredPickerPattern => BuildDialogPattern(PreferredPickerExtensions);

    public static string GetPreferredPickerPattern(AudioProcessingMode? mode) => BuildDialogPattern(GetPreferredPickerExtensions(mode));

    public static bool IsSupportedExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return SupportedDropExtensions.Any(candidate => string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildDialogPattern(IReadOnlyList<string> extensions) => string.Join(";", extensions.Select(static extension => $"*{extension}"));
}
