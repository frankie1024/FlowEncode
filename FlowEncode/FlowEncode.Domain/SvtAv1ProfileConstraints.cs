namespace FlowEncode.Domain;

public static class SvtAv1ProfileConstraints
{
    public static string? GetParameterError(EncodingProfile profile)
    {
        if (profile.Kind != EncoderKind.SvtAv1) return null;
        var options = EncoderCliParameters.Read(profile.AdditionalArguments);
        var tune = options.GetValueOrDefault("--tune", profile.Tune);
        if (EncoderArgumentValueNormalizer.NormalizeTuneForCli(EncoderKind.SvtAv1, tune) == "3"
            && options.GetValueOrDefault("--avif") != "1"
            && options.GetValueOrDefault("--keyint") != "0")
            return "IQ tune requires all-intra / AVIF input. / IQ tune 不能用于普通视频；请使用全帧内编码或 AVIF 模式。";

        if (options.TryGetValue("--lp", out var lp)
            && (!int.TryParse(lp, out var level) || level < 0 || level > 6))
            return "SVT --lp is a parallelism level from 0 to 6, not a CPU thread count. / SVT --lp 是 0–6 的并行级别，旧线程数参数需迁移。";

        if (profile.RateControl is RateControlMode.Qp or RateControlMode.Cq)
        {
            if (options.TryGetValue("--aq-mode", out var aq) && aq != "0")
                return "QP requires --aq-mode 0. / QP 模式要求 --aq-mode 0。";
            if (options.ContainsKey("--crf") || options.TryGetValue("--rc", out var rc) && rc != "0")
                return "QP conflicts with the manually specified rate control. / QP 与附加参数中的码率控制模式冲突。";
            foreach (var option in new[] { "--qp", "--cqp" })
                if (options.TryGetValue(option, out var value)
                    && (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var quality)
                        || quality != profile.Quality
                        || option == "--qp" && (!int.TryParse(value, out _) || quality > 63)))
                    return "Manual QP must match the selected quality and the encoder's numeric format. / 附加 QP 必须与界面质量值一致，并符合编码器数值格式。";
        }
        else if (options.ContainsKey("--qp") || options.ContainsKey("--cqp"))
            return "Manual QP conflicts with the selected rate control. / 附加 QP 与所选码率控制模式冲突。";
        return null;
    }

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
