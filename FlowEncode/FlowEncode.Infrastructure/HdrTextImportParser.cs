using System.Globalization;
using System.Text.RegularExpressions;

namespace FlowEncode.Infrastructure;

internal static partial class HdrTextImportParser
{
    private const string ColorRangeKey = "color range";
    private const string ColorPrimariesKey = "color primaries";
    private const string TransferCharacteristicsKey = "transfer characteristics";
    private const string TransferKey = "transfer";
    private const string MatrixCoefficientsKey = "matrix coefficients";
    private const string MatrixKey = "matrix";
    private const string MasteringDisplayColorPrimariesKey = "mastering display color primaries";
    private const string MasteringDisplayLuminanceKey = "mastering display luminance";
    private const string MaximumContentLightLevelKey = "maximum content light level";
    private const string MaximumFrameAverageLightLevelKey = "maximum frame-average light level";
    private const string MaxCllCombinedKey = "maxcll / maxfall";
    private const string MaxCllKey = "maxcll";
    private const string MaxFallKey = "maxfall";

    private static readonly IReadOnlyDictionary<string, string> ColorPrimariesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["bt.2020"] = "bt2020",
        ["bt2020"] = "bt2020",
        ["rec.2020"] = "bt2020",
        ["rec2020"] = "bt2020",
        ["bt.709"] = "bt709",
        ["bt709"] = "bt709",
        ["rec.709"] = "bt709",
        ["rec709"] = "bt709"
    };

    private static readonly IReadOnlyDictionary<string, string> TransferMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["pq"] = "smpte2084",
        ["smpte st 2084"] = "smpte2084",
        ["smpte2084"] = "smpte2084",
        ["st 2084"] = "smpte2084",
        ["hlg"] = "arib-std-b67",
        ["arib std-b67"] = "arib-std-b67",
        ["arib-std-b67"] = "arib-std-b67",
        ["bt.709"] = "bt709",
        ["bt709"] = "bt709"
    };

    private static readonly IReadOnlyDictionary<string, string> MatrixMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["bt.2020 non-constant"] = "bt2020nc",
        ["bt2020 non-constant"] = "bt2020nc",
        ["bt.2020 non constant"] = "bt2020nc",
        ["bt2020 non constant"] = "bt2020nc",
        ["bt2020nc"] = "bt2020nc",
        ["bt.2020 constant"] = "bt2020c",
        ["bt2020 constant"] = "bt2020c",
        ["bt2020c"] = "bt2020c",
        ["bt.709"] = "bt709",
        ["bt709"] = "bt709"
    };

    private static readonly IReadOnlyDictionary<string, string> RangeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["limited"] = "limited",
        ["tv"] = "limited",
        ["full"] = "full",
        ["pc"] = "full"
    };

    public static HdrTextImportParseResult Parse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return HdrTextImportParseResult.Failure();
        }

        var fields = ParseFields(rawText);
        var range = ParseMappedValue(fields, [ColorRangeKey], RangeMap);
        var colorPrimaries = ParseMappedValue(fields, [ColorPrimariesKey], ColorPrimariesMap);
        var transfer = ParseMappedValue(fields, [TransferCharacteristicsKey, TransferKey], TransferMap);
        var matrix = ParseMappedValue(fields, [MatrixCoefficientsKey, MatrixKey], MatrixMap);
        var maxCll = ParseMaxCll(fields);
        var masteringDisplay = ParseMasteringDisplay(fields);

        var arguments = new List<string>();
        AddArgument(arguments, "--range", range);
        AddArgument(arguments, "--colorprim", colorPrimaries);
        AddArgument(arguments, "--transfer", transfer);
        AddArgument(arguments, "--colormatrix", matrix);
        AddArgument(arguments, "--master-display", masteringDisplay, quoteValue: true);
        AddArgument(arguments, "--max-cll", maxCll, quoteValue: true);

        return arguments.Count == 0
            ? HdrTextImportParseResult.Failure()
            : new HdrTextImportParseResult(true, string.Join(' ', arguments));
    }

    private static void AddArgument(ICollection<string> arguments, string optionName, string? value, bool quoteValue = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        arguments.Add(quoteValue
            ? $"{optionName} {CommandLineDisplay.Quote(value)}"
            : $"{optionName} {value}");
    }

    private static Dictionary<string, string> ParseFields(string rawText)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = rawText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Replace('：', ':').Trim();
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
            {
                continue;
            }

            var key = NormalizeToken(line[..separatorIndex]);
            var value = line[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            fields[key] = value;
        }

        return fields;
    }

    private static string? ParseMappedValue(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, string> map)
    {
        string? rawValue = null;
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out rawValue))
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var normalized = NormalizeToken(rawValue);
        return map.TryGetValue(normalized, out var mapped)
            ? mapped
            : null;
    }

    private static string? ParseMaxCll(IReadOnlyDictionary<string, string> fields)
    {
        if (fields.TryGetValue(MaxCllCombinedKey, out var combinedRawValue))
        {
            var combinedMatch = MaxCllCombinedRegex().Match(combinedRawValue);
            if (combinedMatch.Success)
            {
                var maxCllCombined = ParseFlexibleInteger(combinedMatch.Groups["maxcll"].Value);
                var maxFallCombined = ParseFlexibleInteger(combinedMatch.Groups["maxfall"].Value);
                return maxCllCombined.HasValue && maxFallCombined.HasValue
                    ? $"{maxCllCombined.Value.ToString(CultureInfo.InvariantCulture)},{maxFallCombined.Value.ToString(CultureInfo.InvariantCulture)}"
                    : null;
            }
        }

        var maxCll = ParseFlexibleInteger(GetFirstFieldValue(fields, [MaximumContentLightLevelKey, MaxCllKey]));
        var maxFall = ParseFlexibleInteger(GetFirstFieldValue(fields, [MaximumFrameAverageLightLevelKey, MaxFallKey]));
        return maxCll.HasValue && maxFall.HasValue
            ? $"{maxCll.Value.ToString(CultureInfo.InvariantCulture)},{maxFall.Value.ToString(CultureInfo.InvariantCulture)}"
            : null;
    }

    private static string? ParseMasteringDisplay(IReadOnlyDictionary<string, string> fields)
    {
        var primariesRawValue = GetFirstFieldValue(fields, [MasteringDisplayColorPrimariesKey]);
        var luminanceRawValue = GetFirstFieldValue(fields, [MasteringDisplayLuminanceKey]);
        if (string.IsNullOrWhiteSpace(primariesRawValue) || string.IsNullOrWhiteSpace(luminanceRawValue))
        {
            return null;
        }

        var preset = NormalizeToken(primariesRawValue);
        var coordinates = preset switch
        {
            "display p3" => "G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)",
            "p3" => "G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)",
            "dcip3" => "G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)",
            "dci p3" => "G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)",
            "bt.2020" => "G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)",
            "bt2020" => "G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)",
            "rec.2020" => "G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)",
            "rec2020" => "G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(coordinates))
        {
            return null;
        }

        var luminanceMatch = MasteringDisplayLuminanceRegex().Match(luminanceRawValue);
        if (!luminanceMatch.Success
            || !TryParseLuminance(luminanceMatch.Groups["max"].Value, out var maxLuminance)
            || !TryParseLuminance(luminanceMatch.Groups["min"].Value, out var minLuminance))
        {
            return null;
        }

        return $"{coordinates}L({maxLuminance.ToString(CultureInfo.InvariantCulture)},{minLuminance.ToString(CultureInfo.InvariantCulture)})";
    }

    private static string? GetFirstFieldValue(IReadOnlyDictionary<string, string> fields, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static long? ParseFlexibleInteger(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var numberMatch = FlexibleIntegerRegex().Match(rawValue);
        if (!numberMatch.Success)
        {
            return null;
        }

        var digitsOnly = Regex.Replace(numberMatch.Value, @"[^\d]", string.Empty);
        return long.TryParse(digitsOnly, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool TryParseLuminance(string rawValue, out long scaledValue)
    {
        scaledValue = 0;
        var normalized = rawValue.Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        scaledValue = (long)Math.Round(value * 10000d, MidpointRounding.AwayFromZero);
        return true;
    }

    private static string NormalizeToken(string value)
    {
        return Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
    }

    [GeneratedRegex(@"(?<maxcll>[0-9][0-9\s,]*)\s*/\s*(?<maxfall>[0-9][0-9\s,]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MaxCllCombinedRegex();

    [GeneratedRegex(@"\d[\d\s,]*", RegexOptions.Compiled)]
    private static partial Regex FlexibleIntegerRegex();

    [GeneratedRegex(@"min\s*:\s*(?<min>[0-9][0-9.,]*)\s*cd\/m2,\s*max\s*:\s*(?<max>[0-9][0-9.,]*)\s*cd\/m2", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MasteringDisplayLuminanceRegex();
}

internal sealed record HdrTextImportParseResult(bool Success, string Arguments)
{
    public static HdrTextImportParseResult Failure()
    {
        return new HdrTextImportParseResult(false, string.Empty);
    }
}
