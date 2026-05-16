namespace FlowEncode.Domain;

public static class EncodingCommandPreviewBuilder
{
    public static CommandPreview Build(EncodingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Kind switch
        {
            EncoderKind.X264 => new CommandPreview(
                $"{profile.Name} · x264 管线预览",
                BuildX264Preview(profile),
                "命令预览使用 VapourSynth 管线占位符。后续接入作业队列时，可以直接替换输入脚本与输出路径。"),
            EncoderKind.X265 => new CommandPreview(
                $"{profile.Name} · x265 管线预览",
                BuildX265Preview(profile),
                "x265 使用 y4m 管道输入模型，便于复用旧项目对 Avisynth / VapourSynth 的统一抽象。"),
            EncoderKind.SvtAv1 => new CommandPreview(
                $"{profile.Name} · SVT-AV1 管线预览",
                BuildSvtAv1Preview(profile),
                "当前默认输出 IVF。后续可以在 mux 阶段再接 MKV/MP4 封装。"),
            _ => new CommandPreview(profile.Name, string.Empty, string.Empty)
        };
    }

    private static string BuildX264Preview(EncodingProfile profile)
    {
        var preset = EncoderArgumentValueNormalizer.NormalizePresetForCli(profile.Kind, profile.Preset);
        var tune = EncoderArgumentValueNormalizer.NormalizeTuneForCli(profile.Kind, profile.Tune);
        var profileValue = EncoderArgumentValueNormalizer.NormalizeProfileForCli(profile.Kind, profile.Profile);
        var statsFile = "\"{output}.x264_2pass.log\"";

        if (profile.RateControl == RateControlMode.TwoPass)
        {
            var pass1 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "x264",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 1),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--demuxer y4m --stdin y4m",
                BuildOptionalSegment(profile.AdditionalArguments),
                "-o \"NUL\" -");

            var pass2 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "x264",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 2),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--demuxer y4m --stdin y4m",
                BuildOptionalSegment(profile.AdditionalArguments),
                $"-o \"{{output}}.{profile.OutputContainer}\" -");

            return JoinStagePreview(pass1, pass2);
        }

        return JoinArguments(
            @"vspipe -c y4m ""{input}.vpy"" - |",
            "x264",
            $"--preset {preset}",
            BuildRateControlArguments(profile.Kind, profile),
            BuildOptionalArgument("--tune", tune),
            BuildOptionalArgument("--profile", profileValue),
            "--demuxer y4m --stdin y4m",
            BuildOptionalSegment(profile.AdditionalArguments),
            $"-o \"{{output}}.{profile.OutputContainer}\" -");
    }

    private static string BuildX265Preview(EncodingProfile profile)
    {
        var preset = EncoderArgumentValueNormalizer.NormalizePresetForCli(profile.Kind, profile.Preset);
        var tune = EncoderArgumentValueNormalizer.NormalizeTuneForCli(profile.Kind, profile.Tune);
        var profileValue = EncoderArgumentValueNormalizer.NormalizeProfileForCli(profile.Kind, profile.Profile);
        var statsFile = "\"{output}.x265_2pass.log\"";

        if (profile.RateControl == RateControlMode.TwoPass)
        {
            var pass1 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "x265",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 1),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--y4m --input -",
                BuildOptionalSegment(profile.AdditionalArguments),
                BuildOptionalSegment(profile.UhdParameters),
                "-o \"NUL\"");

            var pass2 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "x265",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 2),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--y4m --input -",
                BuildOptionalSegment(profile.AdditionalArguments),
                BuildOptionalSegment(profile.UhdParameters),
                $"-o \"{{output}}.{profile.OutputContainer}\"");

            return JoinStagePreview(pass1, pass2);
        }

        return JoinArguments(
            @"vspipe -c y4m ""{input}.vpy"" - |",
            "x265",
            $"--preset {preset}",
            BuildRateControlArguments(profile.Kind, profile),
            BuildOptionalArgument("--tune", tune),
            BuildOptionalArgument("--profile", profileValue),
            "--y4m --input -",
            BuildOptionalSegment(profile.AdditionalArguments),
            BuildOptionalSegment(profile.UhdParameters),
            $"-o \"{{output}}.{profile.OutputContainer}\"");
    }

    private static string BuildSvtAv1Preview(EncodingProfile profile)
    {
        var preset = EncoderArgumentValueNormalizer.NormalizePresetForCli(profile.Kind, profile.Preset);
        var tune = EncoderArgumentValueNormalizer.NormalizeTuneForCli(profile.Kind, profile.Tune);
        var profileValue = EncoderArgumentValueNormalizer.NormalizeProfileForCli(profile.Kind, profile.Profile);
        var statsFile = "\"{output}.svt-av1_2pass.log\"";

        if (profile.RateControl == RateControlMode.TwoPass)
        {
            var pass1 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "SvtAv1EncApp",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 1),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--progress 2",
                "--width {width}",
                "--height {height}",
                "--frames {frames}",
                "--input-depth 10",
                "--input -",
                BuildOptionalSegment(profile.AdditionalArguments),
                "-b \"NUL\"");

            var pass2 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "SvtAv1EncApp",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 2),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--progress 2",
                "--width {width}",
                "--height {height}",
                "--frames {frames}",
                "--input-depth 10",
                "--input -",
                BuildOptionalSegment(profile.AdditionalArguments),
                $"-b \"{{output}}.{profile.OutputContainer}\"");

            return JoinStagePreview(pass1, pass2);
        }

        return JoinArguments(
            @"vspipe -c y4m ""{input}.vpy"" - |",
            "SvtAv1EncApp",
            $"--preset {preset}",
            BuildRateControlArguments(profile.Kind, profile),
            BuildOptionalArgument("--tune", tune),
            BuildOptionalArgument("--profile", profileValue),
            "--progress 2",
            "--width {width}",
            "--height {height}",
            "--frames {frames}",
            "--input-depth 10",
            "--input -",
            BuildOptionalSegment(profile.AdditionalArguments),
            $"-b \"{{output}}.{profile.OutputContainer}\"");
    }

    private static string BuildRateControlArguments(EncoderKind kind, EncodingProfile profile)
    {
        return BuildRateControlArguments(kind, profile, null, null);
    }

    private static string BuildRateControlArguments(
        EncoderKind kind,
        EncodingProfile profile,
        string? statsFile,
        int? passIndex)
    {
        return profile.RateControl switch
        {
            RateControlMode.Crf => kind == EncoderKind.SvtAv1
                ? $"--rc 0 --crf {FormatNumber(profile.Quality)}"
                : $"--crf {FormatNumber(profile.Quality)}",
            RateControlMode.Cq or RateControlMode.Qp => kind == EncoderKind.SvtAv1
                ? $"--rc 0 --qp {FormatNumber(profile.Quality)}"
                : $"--qp {FormatNumber(profile.Quality)}",
            RateControlMode.Abr or RateControlMode.Vbr => kind == EncoderKind.SvtAv1
                ? $"--rc 1 --tbr {profile.Bitrate ?? 3500}"
                : $"--bitrate {profile.Bitrate ?? 3500}",
            RateControlMode.TwoPass => kind switch
            {
                EncoderKind.X264 or EncoderKind.X265 => JoinArguments(
                    $"--bitrate {profile.Bitrate ?? 3500}",
                    passIndex.HasValue ? $"--pass {passIndex.Value}" : "--pass 1",
                    BuildOptionalArgument("--stats", statsFile ?? string.Empty)),
                EncoderKind.SvtAv1 => JoinArguments(
                    $"--rc 1 --tbr {profile.Bitrate ?? 3500}",
                    passIndex.HasValue ? $"--pass {passIndex.Value}" : "--pass 1",
                    BuildOptionalArgument("--stats", statsFile ?? string.Empty)),
                _ => string.Empty
            },
            _ => string.Empty
        };
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BuildOptionalArgument(string option, string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : $"{option} {value}";
    }

    private static string BuildOptionalSegment(string segment)
    {
        return string.IsNullOrWhiteSpace(segment) ? string.Empty : segment.Trim();
    }

    private static string JoinArguments(params string[] arguments)
    {
        return string.Join(" ", arguments.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string JoinStagePreview(string pass1, string pass2)
    {
        return $"[Pass 1/2]{Environment.NewLine}{pass1}{Environment.NewLine}{Environment.NewLine}[Pass 2/2]{Environment.NewLine}{pass2}";
    }
}
