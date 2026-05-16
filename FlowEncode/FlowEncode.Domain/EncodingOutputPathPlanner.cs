namespace FlowEncode.Domain;

public static class EncodingOutputPathPlanner
{
    public const string DefaultBaseFileName = "encode";
    public const string DefaultOutputExtension = "264";

    public static string BuildDefaultOutputPath(
        string sourcePath,
        string outputDirectory,
        EncodingProfile? profile)
    {
        var fileName = BuildDefaultOutputBaseFileName(sourcePath, profile);
        var extension = NormalizeOutputExtension(profile?.OutputContainer);
        return Path.Combine(outputDirectory, $"{fileName}.{extension}");
    }

    public static string BuildDefaultOutputBaseFileName(string sourcePath, EncodingProfile? profile)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = DefaultBaseFileName;
        }

        if (profile?.RateControl != RateControlMode.Crf)
        {
            return fileName;
        }

        return $"{fileName}-{GetEncoderToken(profile.Kind)}-crf{FormatTokenNumber(profile.Quality)}";
    }

    public static string NormalizeOutputExtension(string? outputContainer)
    {
        var extension = string.IsNullOrWhiteSpace(outputContainer)
            ? DefaultOutputExtension
            : outputContainer.Trim().TrimStart('.');
        return string.IsNullOrWhiteSpace(extension) ? DefaultOutputExtension : extension;
    }

    private static string GetEncoderToken(EncoderKind kind)
    {
        return kind switch
        {
            EncoderKind.X264 => "x264",
            EncoderKind.X265 => "x265",
            EncoderKind.SvtAv1 => "svtav1",
            _ => kind.ToString().ToLowerInvariant()
        };
    }

    private static string FormatTokenNumber(double value)
    {
        return value
            .ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture)
            .TrimEnd('0')
            .TrimEnd('.');
    }
}
