namespace FlowEncode.Domain;

public static class SvtAv1ProfileConstraints
{
    public static bool HasTwoPassOverlayConflict(EncodingProfile profile)
    {
        return profile.Kind == EncoderKind.SvtAv1
            && profile.RateControl == RateControlMode.TwoPass
            && IsOverlayEnabled(profile.AdditionalArguments);
    }

    private static bool IsOverlayEnabled(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        var enabled = false;
        var tokens = CommandArgumentTokenizer.Tokenize(arguments);

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (CommandArgumentTokenizer.TryParseInlineOption(token, "--enable-overlays", out var overlayValue))
            {
                enabled = IsTruthyValue(overlayValue);
                continue;
            }

            if (token.Equals("--enable-overlays", StringComparison.OrdinalIgnoreCase))
            {
                if (CommandArgumentTokenizer.TryReadValue(tokens, index, out var explicitValue))
                {
                    enabled = IsTruthyValue(explicitValue);
                    index++;
                }
                else
                {
                    enabled = true;
                }

                continue;
            }

            if (CommandArgumentTokenizer.TryParseInlineOption(token, "--svtav1-params", out var inlineParams))
            {
                ApplySvtParamOverrides(inlineParams, ref enabled);
                continue;
            }

            if (token.Equals("--svtav1-params", StringComparison.OrdinalIgnoreCase)
                && CommandArgumentTokenizer.TryReadValue(tokens, index, out var parameterValue))
            {
                ApplySvtParamOverrides(parameterValue, ref enabled);
                index++;
            }
        }

        return enabled;
    }

    private static void ApplySvtParamOverrides(string parameterValue, ref bool enabled)
    {
        foreach (var entry in parameterValue.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = entry.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = entry[..separatorIndex].Trim();
            var value = entry[(separatorIndex + 1)..].Trim();

            if (key.Equals("enable-overlays", StringComparison.OrdinalIgnoreCase))
            {
                enabled = IsTruthyValue(value);
            }
        }
    }

    private static bool IsTruthyValue(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "off" or "no" => false,
            _ => true
        };
    }

}
