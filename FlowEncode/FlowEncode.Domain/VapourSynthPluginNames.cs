using System.Text.RegularExpressions;

namespace FlowEncode.Domain;

public static class VapourSynthPluginNames
{
    public const string FillBorders = "vapoursynth-fillborders";
    public const string PngExport = "vsfpng";
    public static IReadOnlyList<string> Required { get; } = Array.AsReadOnly(new[] { FillBorders, PngExport });

    public static IReadOnlyList<string> DefaultPlugins { get; } = Array.AsReadOnly<string>(
        [.. Required, "vapoursynth-autocrop", "vapoursynth-cas", "vapoursynth-dedot", "vapoursynth-edgefixer", "vapoursynth-scxvid"]);

    public static string? Normalize(string? value)
    {
        var name = value?.Trim();
        return name is not null && Regex.IsMatch(name, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9._-]*[a-zA-Z0-9])?\z")
            ? Regex.Replace(name.ToLowerInvariant(), @"[-_.]+", "-")
            : null;
    }

    public static bool IsManagedSeparately(string name) => name is "vapoursynth" or "vsjetpack" or "awsmfunc" or "pip" or "setuptools" or "wheel";
}
