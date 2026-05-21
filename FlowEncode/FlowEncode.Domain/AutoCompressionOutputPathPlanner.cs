using System.Globalization;

namespace FlowEncode.Domain;

internal static class AutoCompressionOutputPathPlanner
{
    public static string BuildOutputPath(
        string sourcePath,
        string outputDirectory,
        EncoderKind encoderKind,
        AutoCompressionMetric metric,
        double targetQuality)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = EncodingOutputPathPlanner.DefaultBaseFileName;
        }

        return Path.Combine(
            outputDirectory,
            $"{fileName}.{GetEncoderToken(encoderKind)}.{GetMetricToken(metric)}{FormatQualityToken(targetQuality)}.mkv");
    }

    internal static string GetMetricToken(AutoCompressionMetric metric)
    {
        return metric switch
        {
            AutoCompressionMetric.Vmaf => "vmaf",
            AutoCompressionMetric.Ssimulacra2 => "ssimulacra2",
            AutoCompressionMetric.ButteraugliInf => "butteraugliinf",
            AutoCompressionMetric.Butteraugli3 => "butteraugli3",
            AutoCompressionMetric.Xpsnr => "xpsnr",
            AutoCompressionMetric.XpsnrWeighted => "xpsnrw",
            _ => "metric"
        };
    }

    private static string GetEncoderToken(EncoderKind encoderKind)
    {
        return encoderKind switch
        {
            EncoderKind.X264 => "x264",
            EncoderKind.X265 => "x265",
            EncoderKind.SvtAv1 => "av1",
            _ => "encode"
        };
    }

    private static string FormatQualityToken(double targetQuality)
    {
        var token = Math.Max(0, targetQuality).ToString("0.###", CultureInfo.InvariantCulture);
        return token.Replace(".", "p", StringComparison.Ordinal);
    }
}
