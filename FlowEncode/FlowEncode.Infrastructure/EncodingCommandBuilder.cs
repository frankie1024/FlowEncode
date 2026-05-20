using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal sealed class EncodingCommandBuilder
{
    private readonly ExternalToolLocator _toolLocator;

    public EncodingCommandBuilder(ExternalToolLocator toolLocator)
    {
        _toolLocator = toolLocator;
    }

    internal EncodingExecutionPlan BuildPlan(
        EncodingJobRequest request,
        string encoderPath,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        string? statsPath,
        string? outputPathOverride = null)
    {
        var profile = request.Profile;
        var preset = EncoderArgumentValueNormalizer.NormalizePresetForCli(profile.Kind, profile.Preset);
        var tune = EncoderArgumentValueNormalizer.NormalizeTuneForCli(profile.Kind, profile.Tune);
        var profileValue = EncoderArgumentValueNormalizer.NormalizeProfileForCli(profile.Kind, profile.Profile);
        var sourceCommand = BuildSourceCommand(request, pipelineKind);
        var includeX265UhdParameters = profile.Kind == EncoderKind.X265
            && !string.IsNullOrWhiteSpace(profile.UhdParameters);

        var steps = BuildExecutionSteps(
            request,
            encoderPath,
            sourceCommand,
            pipelineKind,
            sourceInfo,
            includeX265UhdParameters,
            preset,
            tune,
            profileValue,
            statsPath,
            outputPathOverride ?? request.OutputPath);

        var displayCommand = JoinStepDisplayCommands(steps);

        return new EncodingExecutionPlan(
            steps,
            displayCommand,
            profile.Kind,
            sourceInfo?.TotalFrames,
            sourceInfo?.FramesPerSecond,
            statsPath is null ? [] : [statsPath]);
    }

    internal static string BuildEncoderColorMetadataArguments(
        EncoderKind kind,
        SourceVideoInfo? sourceInfo,
        string? additionalArguments,
        string? x265UhdParameters)
    {
        if (sourceInfo is null)
        {
            return string.Empty;
        }

        return kind switch
        {
            EncoderKind.X264 => BuildX264ColorMetadataArguments(sourceInfo, [additionalArguments ?? string.Empty]),
            EncoderKind.X265 => BuildX265ColorMetadataArguments(sourceInfo, [additionalArguments ?? string.Empty, x265UhdParameters ?? string.Empty]),
            EncoderKind.SvtAv1 => BuildSvtColorMetadataArguments(sourceInfo, additionalArguments),
            _ => string.Empty
        };
    }

    internal static IReadOnlyList<string> TokenizeCommandLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var argv = CommandLineToArgvW(NormalizeLegacySingleQuotedArguments(value), out var argc);
        if (argv == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to tokenize command line arguments. Win32Error={Marshal.GetLastWin32Error()}");
        }

        try
        {
            var result = new List<string>(argc);
            for (var index = 0; index < argc; index++)
            {
                var item = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                var token = Marshal.PtrToStringUni(item);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    result.Add(token);
                }
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static IReadOnlyList<EncodingExecutionStep> BuildExecutionSteps(
        EncodingJobRequest request,
        string encoderPath,
        ProcessCommand? sourceCommand,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        bool includeX265UhdParameters,
        string preset,
        string tune,
        string profileValue,
        string? statsPath,
        string outputPath)
    {
        return request.Profile.Kind switch
        {
            EncoderKind.X264 or EncoderKind.X265 when request.Profile.RateControl == RateControlMode.TwoPass
                => BuildX26xTwoPassSteps(request, encoderPath, sourceCommand, pipelineKind, sourceInfo, includeX265UhdParameters, preset, tune, profileValue, statsPath!, outputPath),
            EncoderKind.SvtAv1 when request.Profile.RateControl == RateControlMode.TwoPass
                => BuildSvtTwoPassSteps(request, encoderPath, sourceCommand, pipelineKind, sourceInfo, preset, tune, profileValue, statsPath!, outputPath),
            _ => BuildSinglePassSteps(request, encoderPath, sourceCommand, pipelineKind, sourceInfo, includeX265UhdParameters, preset, tune, profileValue, statsPath, outputPath)
        };
    }

    private static IReadOnlyList<EncodingExecutionStep> BuildSinglePassSteps(
        EncodingJobRequest request,
        string encoderPath,
        ProcessCommand? sourceCommand,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        bool includeX265UhdParameters,
        string preset,
        string tune,
        string profileValue,
        string? statsPath,
        string outputPath)
    {
        var encoderCommand = BuildEncoderCommand(
            request,
            encoderPath,
            pipelineKind,
            sourceInfo,
            includeX265UhdParameters,
            preset,
            tune,
            profileValue,
            outputPath,
            BuildRateControlArguments(request.Profile.Kind, request.Profile, statsPath));

        return [CreateExecutionStep(sourceCommand, pipelineKind, encoderCommand, 1, 1)];
    }

    private static IReadOnlyList<EncodingExecutionStep> BuildX26xTwoPassSteps(
        EncodingJobRequest request,
        string encoderPath,
        ProcessCommand? sourceCommand,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        bool includeX265UhdParameters,
        string preset,
        string tune,
        string profileValue,
        string statsPath,
        string outputPath)
    {
        return BuildTwoPassSteps(
            request,
            encoderPath,
            sourceCommand,
            pipelineKind,
            sourceInfo,
            includeX265UhdParameters,
            preset,
            tune,
            profileValue,
            statsPath,
            requireSourceInfo: false,
            outputPath);
    }

    private static IReadOnlyList<EncodingExecutionStep> BuildSvtTwoPassSteps(
        EncodingJobRequest request,
        string encoderPath,
        ProcessCommand? sourceCommand,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        string preset,
        string tune,
        string profileValue,
        string statsPath,
        string outputPath)
    {
        return BuildTwoPassSteps(
            request,
            encoderPath,
            sourceCommand,
            pipelineKind,
            sourceInfo,
            includeX265UhdParameters: false,
            preset,
            tune,
            profileValue,
            statsPath,
            requireSourceInfo: true,
            outputPath);
    }

    private static IReadOnlyList<EncodingExecutionStep> BuildTwoPassSteps(
        EncodingJobRequest request,
        string encoderPath,
        ProcessCommand? sourceCommand,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        bool includeX265UhdParameters,
        string preset,
        string tune,
        string profileValue,
        string statsPath,
        bool requireSourceInfo,
        string outputPath)
    {
        var resolvedSourceInfo = requireSourceInfo
            ? sourceInfo ?? throw new InvalidOperationException("SVT-AV1 two-pass encoding requires detectable source metadata.")
            : sourceInfo;

        var pass1Command = BuildEncoderCommand(
            request,
            encoderPath,
            pipelineKind,
            resolvedSourceInfo,
            includeX265UhdParameters,
            preset,
            tune,
            profileValue,
            "NUL",
            BuildRateControlArguments(request.Profile.Kind, request.Profile, statsPath, passIndex: 1, passCount: 2));

        var pass2Command = BuildEncoderCommand(
            request,
            encoderPath,
            pipelineKind,
            resolvedSourceInfo,
            includeX265UhdParameters,
            preset,
            tune,
            profileValue,
            outputPath,
            BuildRateControlArguments(request.Profile.Kind, request.Profile, statsPath, passIndex: 2, passCount: 2));

        return
        [
            CreateExecutionStep(sourceCommand, pipelineKind, pass1Command, 1, 2),
            CreateExecutionStep(sourceCommand, pipelineKind, pass2Command, 2, 2)
        ];
    }

    private static EncodingExecutionStep CreateExecutionStep(
        ProcessCommand? sourceCommand,
        InputPipelineKind pipelineKind,
        ProcessCommand encoderCommand,
        int stageIndex,
        int stageCount)
    {
        var pipelineCommand = sourceCommand is null
            ? encoderCommand.DisplayCommand
            : $"{sourceCommand.DisplayCommand} | {encoderCommand.DisplayCommand}";
        return new EncodingExecutionStep(
            encoderCommand,
            pipelineKind is InputPipelineKind.Y4mFile or InputPipelineKind.RawYuvFile ? null : sourceCommand,
            pipelineCommand,
            stageIndex,
            stageCount);
    }

    private static ProcessCommand BuildEncoderCommand(
        EncodingJobRequest request,
        string encoderPath,
        InputPipelineKind pipelineKind,
        SourceVideoInfo? sourceInfo,
        bool includeX265UhdParameters,
        string preset,
        string tune,
        string profileValue,
        string outputPath,
        string rateControl)
    {
        var profile = request.Profile;
        var outputArg = profile.Kind switch
        {
            EncoderKind.X264 => $"-o {Quote(outputPath)}",
            EncoderKind.X265 => $"-o {Quote(outputPath)}",
            EncoderKind.SvtAv1 => $"-b {Quote(outputPath)}",
            _ => throw new ArgumentOutOfRangeException()
        };

        var directInputArgs = profile.Kind switch
        {
            EncoderKind.X264 => BuildX264DirectInputArguments(request.SourcePath, pipelineKind),
            EncoderKind.X265 => BuildX265DirectInputArguments(request.SourcePath, pipelineKind),
            EncoderKind.SvtAv1 => BuildSvtDirectInputArguments(request.SourcePath, pipelineKind),
            _ => throw new ArgumentOutOfRangeException()
        };
        var sourceMetadataArgs = BuildEncoderColorMetadataArguments(
            profile.Kind,
            sourceInfo,
            profile.AdditionalArguments,
            includeX265UhdParameters ? profile.UhdParameters : string.Empty);

        var arguments = profile.Kind switch
        {
            EncoderKind.X264 => BuildArgumentParts(
                $"--preset {preset}",
                rateControl,
                Optional("--tune", tune),
                Optional("--profile", profileValue),
                directInputArgs,
                TokenizeCommandLine(sourceMetadataArgs),
                TokenizeCommandLine(profile.AdditionalArguments),
                outputArg),
            EncoderKind.X265 => BuildArgumentParts(
                $"--preset {preset}",
                rateControl,
                Optional("--tune", tune),
                Optional("--profile", profileValue),
                directInputArgs,
                TokenizeCommandLine(sourceMetadataArgs),
                TokenizeCommandLine(profile.AdditionalArguments),
                TokenizeCommandLine(includeX265UhdParameters ? profile.UhdParameters : string.Empty),
                outputArg),
            EncoderKind.SvtAv1 => BuildArgumentParts(
                $"--preset {preset}",
                rateControl,
                Optional("--tune", tune),
                Optional("--profile", profileValue),
                "--progress 2",
                sourceInfo is null ? string.Empty : BuildSvtSourceArguments(sourceInfo),
                directInputArgs,
                TokenizeCommandLine(sourceMetadataArgs),
                TokenizeCommandLine(profile.AdditionalArguments),
                outputArg),
            _ => throw new ArgumentOutOfRangeException()
        };

        return new ProcessCommand(
            encoderPath,
            arguments,
            $"{Quote(encoderPath)} {string.Join(' ', arguments.Select(DisplayToken))}");
    }

    private static string JoinStepDisplayCommands(IReadOnlyList<EncodingExecutionStep> steps)
    {
        if (steps.Count == 1)
        {
            return steps[0].DisplayCommand;
        }

        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            steps.Select(step => $"[Pass {step.StageIndex}/{step.StageCount}]{Environment.NewLine}{step.DisplayCommand}"));
    }

    private static string BuildRateControlArguments(
        EncoderKind kind,
        EncodingProfile profile,
        string? statsPath = null,
        int? passIndex = null,
        int? passCount = null)
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
            RateControlMode.TwoPass => BuildTwoPassRateControlArguments(kind, profile, statsPath, passIndex, passCount),
            _ => string.Empty
        };
    }

    private static string BuildTwoPassRateControlArguments(
        EncoderKind kind,
        EncodingProfile profile,
        string? statsPath,
        int? passIndex,
        int? passCount)
    {
        return kind switch
        {
            EncoderKind.X264 or EncoderKind.X265 => JoinArguments(
                $"--bitrate {profile.Bitrate ?? 3500}",
                passIndex.HasValue ? $"--pass {passIndex.Value}" : "--pass 1",
                Optional("--stats", QuoteIfPresent(statsPath))),
            EncoderKind.SvtAv1 => JoinArguments(
                $"--rc 1 --tbr {profile.Bitrate ?? 3500}",
                passIndex.HasValue ? $"--pass {passIndex.Value}" : "--pass 1",
                Optional("--stats", QuoteIfPresent(statsPath))),
            _ => string.Empty
        };
    }

    private static string BuildX264ColorMetadataArguments(SourceVideoInfo sourceInfo, IReadOnlyList<string> manualArguments)
    {
        var parts = new List<string>();
        AddStringMetadataOption(parts, "--range", MapX264Range(sourceInfo.ColorRange), manualArguments);
        AddStringMetadataOption(parts, "--colorprim", NormalizeX26xColorValue(sourceInfo.ColorPrimaries, X26xColorPrimaries), manualArguments);
        AddStringMetadataOption(parts, "--transfer", NormalizeX26xColorValue(sourceInfo.ColorTransfer, X26xColorTransfers), manualArguments);
        AddStringMetadataOption(parts, "--colormatrix", NormalizeX26xColorValue(sourceInfo.ColorMatrix, X26xColorMatrices), manualArguments);
        AddStringMetadataOption(parts, "--mastering-display", sourceInfo.MasteringDisplay, manualArguments);
        return JoinArguments([.. parts]);
    }

    private static string BuildX265ColorMetadataArguments(SourceVideoInfo sourceInfo, IReadOnlyList<string> manualArguments)
    {
        var parts = new List<string>();
        var hasVideoSignalPreset = ArgumentsContainAnyOption(manualArguments, "--video-signal-type-preset");
        if (hasVideoSignalPreset)
        {
            return string.Empty;
        }

        AddStringMetadataOption(parts, "--range", MapX265Range(sourceInfo.ColorRange), manualArguments);
        AddStringMetadataOption(parts, "--colorprim", NormalizeX26xColorValue(sourceInfo.ColorPrimaries, X26xColorPrimaries), manualArguments);
        AddStringMetadataOption(parts, "--transfer", NormalizeX26xColorValue(sourceInfo.ColorTransfer, X26xColorTransfers), manualArguments);
        AddStringMetadataOption(parts, "--colormatrix", NormalizeX26xColorValue(sourceInfo.ColorMatrix, X26xColorMatrices), manualArguments);
        AddStringMetadataOption(parts, "--master-display", sourceInfo.MasteringDisplay, manualArguments);
        AddStringMetadataOption(parts, "--max-cll", sourceInfo.ContentLightLevel, manualArguments);
        return JoinArguments([.. parts]);
    }

    private static string BuildSvtColorMetadataArguments(SourceVideoInfo sourceInfo, string? manualArguments)
    {
        var parts = new List<string>();
        var manualArgumentList = new[] { manualArguments ?? string.Empty };

        AddStringMetadataOption(parts, "--color-range", MapSvtRange(sourceInfo.ColorRange), manualArgumentList);
        AddStringMetadataOption(parts, "--color-primaries", MapSvtColorPrimaries(sourceInfo.ColorPrimaries), manualArgumentList);
        AddStringMetadataOption(parts, "--transfer-characteristics", MapSvtColorTransfer(sourceInfo.ColorTransfer), manualArgumentList);
        AddStringMetadataOption(parts, "--matrix-coefficients", MapSvtColorMatrix(sourceInfo.ColorMatrix), manualArgumentList);
        AddStringMetadataOption(parts, "--chroma-sample-position", MapSvtChromaLocation(sourceInfo.ChromaLocation), manualArgumentList);
        AddStringMetadataOption(parts, "--mastering-display", sourceInfo.MasteringDisplay, manualArgumentList);
        AddStringMetadataOption(parts, "--content-light", sourceInfo.ContentLightLevel, manualArgumentList);
        return JoinArguments([.. parts]);
    }

    private static void AddStringMetadataOption(ICollection<string> parts, string optionName, string? value, IReadOnlyList<string> manualArguments)
    {
        if (string.IsNullOrWhiteSpace(value) || ArgumentsContainAnyOption(manualArguments, optionName))
        {
            return;
        }

        parts.Add($"{optionName} {value}");
    }

    private static string? NormalizeX26xColorValue(string? value, IReadOnlySet<string> supportedValues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return supportedValues.Contains(normalized) ? normalized : null;
    }

    private static string? MapX264Range(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "tv" or "limited" => "tv",
            "pc" or "full" => "pc",
            _ => null
        };
    }

    private static string? MapX265Range(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "tv" or "limited" => "limited",
            "pc" or "full" => "full",
            _ => null
        };
    }

    private static string? MapSvtRange(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "tv" or "limited" => "0",
            "pc" or "full" => "1",
            _ => null
        };
    }

    private static string? MapSvtColorPrimaries(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "bt709" => "1",
            "bt470m" => "4",
            "bt470bg" => "5",
            "smpte170m" => "6",
            "smpte240m" => "7",
            "film" => "8",
            "bt2020" => "9",
            "smpte428" => "10",
            "smpte431" => "11",
            "smpte432" => "12",
            "ebu3213" or "jedec-p22" => "22",
            _ => null
        };
    }

    private static string? MapSvtColorTransfer(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "bt709" => "1",
            "bt470m" => "4",
            "bt470bg" => "5",
            "smpte170m" => "6",
            "smpte240m" => "7",
            "linear" => "8",
            "log100" => "9",
            "log316" => "10",
            "iec61966-2-4" => "11",
            "bt1361e" => "12",
            "iec61966-2-1" => "13",
            "bt2020-10" => "14",
            "bt2020-12" => "15",
            "smpte2084" => "16",
            "smpte428" => "17",
            "arib-std-b67" => "18",
            _ => null
        };
    }

    private static string? MapSvtColorMatrix(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "gbr" or "rgb" => "0",
            "bt709" => "1",
            "fcc" => "4",
            "bt470bg" => "5",
            "smpte170m" => "6",
            "smpte240m" => "7",
            "ycgco" => "8",
            "bt2020nc" => "9",
            "bt2020c" => "10",
            "smpte2085" => "11",
            "chroma-derived-nc" => "12",
            "chroma-derived-c" => "13",
            "ictcp" => "14",
            _ => null
        };
    }

    private static string? MapSvtChromaLocation(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "left" or "vertical" => "left",
            "topleft" or "colocated" or "top-left" => "topleft",
            _ => null
        };
    }

    private static bool ArgumentsContainAnyOption(IReadOnlyList<string> arguments, params string[] optionNames)
    {
        foreach (var argument in arguments)
        {
            if (ArgumentsContainAnyOption(argument, optionNames))
            {
                return true;
            }
        }

        return false;
    }

    private ProcessCommand? BuildSourceCommand(EncodingJobRequest request, InputPipelineKind pipelineKind)
    {
        return pipelineKind switch
        {
            InputPipelineKind.VapourSynth => CreateProcessCommand(
                _toolLocator.ResolveVspipe(),
                request.SourcePath,
                "-",
                "--container",
                "y4m"),
            InputPipelineKind.AviSynth => CreateProcessCommand(
                _toolLocator.ResolveAvs2PipeMod(),
                "-y4mp",
                request.SourcePath),
            InputPipelineKind.FfmpegPipe => CreateProcessCommand(
                _toolLocator.ResolveFfmpeg(),
                "-hide_banner",
                "-loglevel",
                "error",
                "-i",
                request.SourcePath,
                "-map",
                "0:v:0",
                "-an",
                "-sn",
                "-dn",
                "-strict",
                "-1",
                "-f",
                "yuv4mpegpipe",
                "-"),
            InputPipelineKind.RawYuvFile or InputPipelineKind.Y4mFile => null,
            _ => null
        };
    }

    private static ProcessCommand CreateProcessCommand(string executablePath, params string[] arguments)
    {
        return new ProcessCommand(
            executablePath,
            arguments,
            $"{Quote(executablePath)} {string.Join(' ', arguments.Select(DisplayToken))}");
    }

    private static IReadOnlyList<string> BuildArgumentParts(params object?[] parts)
    {
        var result = new List<string>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case null:
                    break;
                case string text when !string.IsNullOrWhiteSpace(text):
                    result.AddRange(TokenizeCommandLine(text));
                    break;
                case IEnumerable<string> values:
                    result.AddRange(values.Where(static value => !string.IsNullOrWhiteSpace(value)));
                    break;
            }
        }

        return result;
    }

    private static string DisplayToken(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.IndexOfAny([' ', '\t', '"']) >= 0
            ? Quote(value.Replace("\"", "\\\""))
            : value;
    }

    private static bool ArgumentsContainAnyOption(string? arguments, params string[] optionNames)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        var options = new HashSet<string>(optionNames, StringComparer.OrdinalIgnoreCase);
        foreach (var token in TokenizeCommandLine(arguments))
        {
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var optionName = token.Split('=', 2)[0];
            if (options.Contains(optionName))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeLegacySingleQuotedArguments(string value)
    {
        if (value.IndexOf('\'') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var inDoubleQuotes = false;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"')
            {
                inDoubleQuotes = !inDoubleQuotes;
                builder.Append(character);
                continue;
            }

            if (character == '\''
                && !inDoubleQuotes
                && (index == 0 || char.IsWhiteSpace(value[index - 1])))
            {
                var closingIndex = value.IndexOf('\'', index + 1);
                if (closingIndex > index)
                {
                    AppendDoubleQuotedArgument(builder, value.AsSpan(index + 1, closingIndex - index - 1));
                    index = closingIndex;
                    continue;
                }
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static void AppendDoubleQuotedArgument(StringBuilder builder, ReadOnlySpan<char> value)
    {
        builder.Append('"');

        var backslashCount = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount);
                backslashCount = 0;
            }

            builder.Append(character);
        }

        if (backslashCount > 0)
        {
            builder.Append('\\', backslashCount * 2);
        }

        builder.Append('"');
    }

    private static string X264InputSwitch(InputPipelineKind pipelineKind)
    {
        return pipelineKind switch
        {
            InputPipelineKind.Y4mFile => "--demuxer y4m",
            InputPipelineKind.RawYuvFile => "--demuxer raw",
            _ => "--demuxer y4m --stdin y4m"
        };
    }

    private static string BuildX264DirectInputArguments(string sourcePath, InputPipelineKind pipelineKind)
    {
        return pipelineKind switch
        {
            InputPipelineKind.Y4mFile => $"{X264InputSwitch(pipelineKind)} {Quote(sourcePath)}",
            InputPipelineKind.RawYuvFile => $"{X264InputSwitch(pipelineKind)} {Quote(sourcePath)}",
            _ => $"{X264InputSwitch(pipelineKind)} -"
        };
    }

    private static string BuildX265DirectInputArguments(string sourcePath, InputPipelineKind pipelineKind)
    {
        return pipelineKind switch
        {
            InputPipelineKind.Y4mFile => $"--y4m --input {Quote(sourcePath)}",
            InputPipelineKind.RawYuvFile => $"--input {Quote(sourcePath)}",
            _ => "--y4m --input -"
        };
    }

    private static string BuildSvtDirectInputArguments(string sourcePath, InputPipelineKind pipelineKind)
    {
        return pipelineKind switch
        {
            InputPipelineKind.Y4mFile => $"--input {Quote(sourcePath)}",
            InputPipelineKind.RawYuvFile => $"--input {Quote(sourcePath)}",
            _ => "--input -"
        };
    }

    private static string BuildSvtSourceArguments(SourceVideoInfo sourceInfo)
    {
        return JoinArguments(
            $"--width {sourceInfo.Width}",
            $"--height {sourceInfo.Height}",
            sourceInfo.TotalFrames is > 0 ? $"--frames {sourceInfo.TotalFrames.Value}" : string.Empty,
            sourceInfo.BitDepth > 0 ? $"--input-depth {sourceInfo.BitDepth}" : string.Empty);
    }

    private static string Optional(string name, string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : $"{name} {value}";
    }

    private static string QuoteIfPresent(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : Quote(value);
    }

    private static string Quote(string value)
    {
        return $"\"{value}\"";
    }

    private static string JoinArguments(params string[] parts)
    {
        return string.Join(" ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.0##", CultureInfo.InvariantCulture);
    }

    private static readonly HashSet<string> X26xColorPrimaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "bt709",
        "bt470m",
        "bt470bg",
        "smpte170m",
        "smpte240m",
        "film",
        "bt2020",
        "smpte428",
        "smpte431",
        "smpte432"
    };

    private static readonly HashSet<string> X26xColorTransfers = new(StringComparer.OrdinalIgnoreCase)
    {
        "bt709",
        "bt470m",
        "bt470bg",
        "smpte170m",
        "smpte240m",
        "linear",
        "log100",
        "log316",
        "iec61966-2-4",
        "bt1361e",
        "iec61966-2-1",
        "bt2020-10",
        "bt2020-12",
        "smpte2084",
        "smpte428",
        "arib-std-b67"
    };

    private static readonly HashSet<string> X26xColorMatrices = new(StringComparer.OrdinalIgnoreCase)
    {
        "bt709",
        "fcc",
        "bt470bg",
        "smpte170m",
        "smpte240m",
        "gbr",
        "ycgco",
        "bt2020nc",
        "bt2020c",
        "smpte2085",
        "chroma-derived-nc",
        "chroma-derived-c",
        "ictcp"
    };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
