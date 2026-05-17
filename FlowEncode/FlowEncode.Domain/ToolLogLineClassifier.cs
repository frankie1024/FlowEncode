using System.Globalization;
using System.Text.RegularExpressions;

namespace FlowEncode.Domain;

public static class ToolLogLineClassifier
{
    private static readonly Regex OpusEncDisplayProgressRegex = new(@"^\[[\|/\\\- ]\]\s+\d{2}:\d{2}:\d{2}\.\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex Av1anPercentProgressRegex = new(@"(?<!\d)(?<value>\d{1,3}(?:\.\d+)?)\s*%(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Av1anFractionProgressRegex = new(@"(?<!\d)(?<done>\d{1,7})\s*\/\s*(?<total>\d{1,7})(?!\d)", RegexOptions.Compiled);

    public static bool IsAudioTransientLine(AudioProcessingMode? mode, string? line)
    {
        var normalized = ConsoleOutputLineNormalizer.Normalize(line);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return mode switch
        {
            AudioProcessingMode.Eac3To => normalized.StartsWith("process:", StringComparison.OrdinalIgnoreCase),
            AudioProcessingMode.Ddp => LooksLikeDeewConsoleProgressLine(normalized),
            AudioProcessingMode.Opus => normalized.StartsWith("process:", StringComparison.OrdinalIgnoreCase)
                || LooksLikeOpusCliProgressLine(normalized),
            _ => false
        };
    }

    public static bool IsBluRayTransientLine(BluRayDemuxBackend? backend, string? line)
    {
        var normalized = ConsoleOutputLineNormalizer.Normalize(line);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return backend switch
        {
            BluRayDemuxBackend.DgDemux => LooksLikeDgDemuxProgressLine(normalized),
            BluRayDemuxBackend.Eac3To => IsEac3ToAnalyzeLine(normalized) || IsEac3ToProcessLine(normalized),
            _ => false
        };
    }

    public static bool IsAutoCompressionTransientLine(string? line)
    {
        var normalized = ConsoleOutputLineNormalizer.Normalize(line);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.StartsWith("process:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.Contains("completed", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("finished", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("done", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var looksLikeStageLine =
            normalized.Contains("chunk", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("scene", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("probe", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("target quality", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("split", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("progress", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("encoding", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("encode", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("scenecut", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeStageLine)
        {
            return false;
        }

        if (Av1anPercentProgressRegex.IsMatch(normalized))
        {
            return true;
        }

        var fractionMatch = Av1anFractionProgressRegex.Match(normalized);
        if (fractionMatch.Success
            && int.TryParse(fractionMatch.Groups["done"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var done)
            && int.TryParse(fractionMatch.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            && total > 0
            && done >= 0
            && done <= total)
        {
            return true;
        }

        return false;
    }

    public static bool LooksLikeDeewConsoleProgressLine(string? line)
    {
        var normalized = ConsoleOutputLineNormalizer.Normalize(line);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.StartsWith("Stage progress:", StringComparison.OrdinalIgnoreCase)
            || (normalized.IndexOf('%') >= 0
                && normalized.IndexOf('[') >= 0
                && normalized.IndexOf(']') >= 0);
    }

    private static bool LooksLikeOpusCliProgressLine(string? line)
    {
        var normalized = ConsoleOutputLineNormalizer.Normalize(line);
        return !string.IsNullOrWhiteSpace(normalized) && OpusEncDisplayProgressRegex.IsMatch(normalized);
    }

    private static bool LooksLikeDgDemuxProgressLine(string? line)
    {
        var normalized = ConsoleOutputLineNormalizer.Normalize(line);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (!normalized.EndsWith('%'))
        {
            return false;
        }

        var numericPart = normalized[..^1].Trim();
        if (string.IsNullOrWhiteSpace(numericPart))
        {
            return false;
        }

        foreach (var character in numericPart)
        {
            if (!char.IsDigit(character) && character != '.')
            {
                return false;
            }
        }

        return double.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            && percent >= 0
            && percent <= 100;
    }

    public static bool IsEac3ToAnalyzeLine(string? line)
    {
        var normalized = ConsoleOutputLineNormalizer.Normalize(line);
        return normalized.StartsWith("analyze:", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEac3ToProcessLine(string? line)
    {
        var normalized = ConsoleOutputLineNormalizer.Normalize(line);
        return normalized.StartsWith("process:", StringComparison.OrdinalIgnoreCase);
    }
}
