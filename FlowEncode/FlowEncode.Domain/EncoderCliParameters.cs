using System.Globalization;

namespace FlowEncode.Domain;

public static class EncoderCliParameters
{
    public static int? ResolveOutputDepth(EncodingProfile profile, int? sourceDepth = null)
    {
        var options = Read(profile.AdditionalArguments, profile.Kind == EncoderKind.X265 ? profile.UhdParameters : null);
        var name = options.GetValueOrDefault("--profile", profile.Profile).ToLowerInvariant();
        int? profileDepth = profile.Kind switch
        {
            EncoderKind.X264 when name is "baseline" or "main" or "high" => 8,
            EncoderKind.X264 when name == "high10" => 10,
            EncoderKind.X265 when name is "main" or "mainstillpicture" => 8,
            EncoderKind.X265 when name.Contains("12", StringComparison.Ordinal) => 12,
            EncoderKind.X265 when name.Contains("10", StringComparison.Ordinal) => 10,
            _ => null
        };
        var depth = profileDepth ?? sourceDepth;
        if (depth is null && options.TryGetValue("--input-depth", out var inputDepth) && int.TryParse(inputDepth, out var input)) depth = input;
        if (options.TryGetValue("--output-depth", out var explicitDepth))
        {
            if (!int.TryParse(explicitDepth, out var parsed) || parsed is not (8 or 10 or 12))
                throw new ArgumentException("--output-depth must be 8, 10 or 12. / 输出位深须为 8、10 或 12。");
            if (profileDepth.HasValue && parsed > profileDepth.Value)
                throw new ArgumentException("Output bit depth conflicts with the selected profile. / 输出位深与所选 profile 不兼容。");
            depth = parsed;
        }
        if (profile.Kind == EncoderKind.X264 && depth > 10)
            throw new ArgumentException("x264 supports 8/10-bit output. / x264 仅支持 8/10 位输出。");
        return depth;
    }

    // Preserve the encoder's ordering, including packed SVT overrides in old templates.
    public static IReadOnlyDictionary<string, string> Read(params string?[] arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in arguments)
        {
            var tokens = CommandArgumentTokenizer.Tokenize(text ?? string.Empty);
            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (!token.StartsWith('-')) continue;
                var split = token.IndexOf('=');
                var key = split > 0 ? token[..split] : token;
                var value = split > 0 ? token[(split + 1)..] : "1";
                if (split < 0 && CommandArgumentTokenizer.TryReadValue(tokens, index, out var next))
                {
                    value = next;
                    index++;
                }
                if (key.Equals("--svtav1-params", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var entry in value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var separator = entry.IndexOf('=');
                        if (separator > 0) result["--" + entry[..separator]] = entry[(separator + 1)..];
                    }
                }
                else result[key] = value;
            }
        }
        return result;
    }

    public static string BuildSvtQpArguments(double quality)
    {
        ValidateSvtQuality(quality);
        // --qp is integer-only and AQ defaults to CRF. This path also supports older EncApp versions.
        return quality <= 63 && quality == Math.Truncate(quality)
            ? $"--rc 0 --aq-mode 0 --qp {quality.ToString("0", CultureInfo.InvariantCulture)}"
            : $"--cqp {quality.ToString("0.##", CultureInfo.InvariantCulture)}";
    }

    public static string BuildIntegerQpArgument(double quality)
    {
        if (!double.IsFinite(quality) || quality < 0 || quality != Math.Truncate(quality))
            throw new ArgumentOutOfRangeException(nameof(quality), "x264/x265 QP must be an integer. / x264/x265 QP 必须为整数。");
        return $"--qp {quality.ToString("0", CultureInfo.InvariantCulture)}";
    }

    public static void ValidateSvtQuality(double quality)
    {
        if (!double.IsFinite(quality) || quality < 1 || quality > 70 || quality * 4 != Math.Truncate(quality * 4))
            throw new ArgumentOutOfRangeException(nameof(quality), "SVT-AV1 quality must be from 1 to 70 in steps of 0.25. / SVT-AV1 质量值须在 1–70 之间，步进为 0.25。");
    }
}
