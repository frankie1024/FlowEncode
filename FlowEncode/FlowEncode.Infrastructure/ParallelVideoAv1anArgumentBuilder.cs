using System.Globalization;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class ParallelVideoAv1anCommand
{
    public ParallelVideoAv1anCommand(IReadOnlyList<string> arguments, string displayCommand)
    {
        Arguments = arguments;
        DisplayCommand = displayCommand;
    }

    public IReadOnlyList<string> Arguments { get; }

    public string DisplayCommand { get; }
}

public static class ParallelVideoAv1anArgumentBuilder
{
    private static readonly HashSet<string> ForbiddenOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "-i",
        "--input",
        "--stdin",
        "--y4m",
        "--demuxer",
        "-o",
        "--output",
        "-b",
        "--pass",
        "--stats",
        "--slow-firstpass"
    };

    internal static ParallelVideoAv1anCommand BuildCommand(
        ParallelVideoEncodingRequest request,
        string av1anPath,
        string tempDirectory,
        string outputPath,
        SourceVideoInfo? sourceInfo = null,
        string? logFilePath = null,
        bool useStructuredProgress = true)
    {
        RequestValidation.ValidateParallelVideoEncodingRequest(request);

        var videoParameters = BuildVideoParameters(request, sourceInfo);
        var arguments = new List<string>
        {
            "-i",
            request.SourcePath,
            "-o",
            outputPath,
            "-y",
            "--keep",
            "--temp",
            tempDirectory,
            "--verbose",
            "--encoder",
            MapEncoder(request.EncoderKind),
            "--video-params",
            videoParameters
        };

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            arguments.Add("--log-file");
            arguments.Add(logFilePath);
            arguments.Add("--log-level");
            arguments.Add("debug");
        }

        if (useStructuredProgress)
        {
            arguments.Add("--progress-format");
            arguments.Add("jsonl");
        }

        if (request.Workers is > 0)
        {
            arguments.Add("--workers");
            arguments.Add(request.Workers.Value.ToString(CultureInfo.InvariantCulture));
        }

        return new ParallelVideoAv1anCommand(
            arguments,
            $"{Quote(av1anPath)} {CommandLineDisplay.JoinArguments(arguments)}");
    }

    internal static string BuildVideoParameters(ParallelVideoEncodingRequest request, SourceVideoInfo? sourceInfo = null)
    {
        RequestValidation.ValidateParallelVideoEncodingRequest(request);

        var preset = EncoderArgumentValueNormalizer.NormalizePresetForCli(request.EncoderKind, request.Preset);
        var tune = EncoderArgumentValueNormalizer.NormalizeTuneForCli(request.EncoderKind, request.Tune);
        var profile = EncoderArgumentValueNormalizer.NormalizeProfileForCli(request.EncoderKind, request.Profile);
        var additionalArguments = TokenizeAndValidateUserArguments(request.VideoParameters);
        var includeX265UhdParameters = request.EncoderKind == EncoderKind.X265
            && !string.IsNullOrWhiteSpace(request.UhdParameters);
        var uhdParameters = includeX265UhdParameters
            ? TokenizeAndValidateUserArguments(request.UhdParameters)
            : [];
        var colorMetadataArguments = EncodingCommandBuilder.BuildEncoderColorMetadataArguments(
            request.EncoderKind,
            sourceInfo,
            request.VideoParameters,
            includeX265UhdParameters ? request.UhdParameters : string.Empty);

        var parts = request.EncoderKind switch
        {
            EncoderKind.X264 => BuildArgumentParts(
                $"--crf {FormatNumber(request.Crf)}",
                $"--preset {preset}",
                Optional("--tune", tune),
                Optional("--profile", profile),
                ContainsOption(additionalArguments, "--log-level")
                    ? Array.Empty<string>()
                    : ["--log-level", "info"],
                EncodingCommandBuilder.TokenizeCommandLine(colorMetadataArguments),
                additionalArguments),
            EncoderKind.X265 => BuildArgumentParts(
                $"--crf {FormatNumber(request.Crf)}",
                $"--preset {preset}",
                Optional("--tune", tune),
                Optional("--profile", profile),
                EncodingCommandBuilder.TokenizeCommandLine(colorMetadataArguments),
                additionalArguments,
                uhdParameters),
            EncoderKind.SvtAv1 => BuildArgumentParts(
                "--rc 0",
                $"--crf {FormatNumber(request.Crf)}",
                $"--preset {preset}",
                Optional("--tune", tune),
                Optional("--profile", profile),
                sourceInfo is null ? string.Empty : BuildSvtSourceArguments(sourceInfo),
                EncodingCommandBuilder.TokenizeCommandLine(colorMetadataArguments),
                additionalArguments),
            _ => throw new ArgumentOutOfRangeException(nameof(request.EncoderKind), request.EncoderKind, null)
        };

        return CommandLineDisplay.JoinArguments(parts);
    }

    public static string? FindForbiddenUserArgument(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        foreach (var token in EncodingCommandBuilder.TokenizeCommandLine(arguments))
        {
            if (!token.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            var optionName = token.Split('=', 2)[0];
            if (ForbiddenOptions.Contains(optionName))
            {
                return optionName;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> TokenizeAndValidateUserArguments(string? arguments)
    {
        var forbidden = FindForbiddenUserArgument(arguments);
        if (forbidden is not null)
        {
            throw new InvalidOperationException($"Av1an parallel video encoding does not support encoder argument '{forbidden}'. Let Av1an manage chunk input/output and use CRF-only settings.");
        }

        return EncodingCommandBuilder.TokenizeCommandLine(arguments ?? string.Empty);
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
                    result.AddRange(EncodingCommandBuilder.TokenizeCommandLine(text));
                    break;
                case IEnumerable<string> values:
                    result.AddRange(values.Where(static value => !string.IsNullOrWhiteSpace(value)));
                    break;
            }
        }

        return result;
    }

    private static string Optional(string name, string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : $"{name} {value}";
    }

    private static bool ContainsOption(IReadOnlyList<string> arguments, string option)
    {
        return arguments.Any(token =>
            string.Equals(token.Split('=', 2)[0], option, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSvtSourceArguments(SourceVideoInfo sourceInfo)
    {
        return string.Join(
            " ",
            new[]
            {
                $"--width {sourceInfo.Width}",
                $"--height {sourceInfo.Height}",
                sourceInfo.TotalFrames is > 0 ? $"--frames {sourceInfo.TotalFrames.Value}" : string.Empty,
                sourceInfo.BitDepth > 0 ? $"--input-depth {sourceInfo.BitDepth}" : string.Empty
            }.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string MapEncoder(EncoderKind kind)
    {
        return kind switch
        {
            EncoderKind.X264 => "x264",
            EncoderKind.X265 => "x265",
            EncoderKind.SvtAv1 => "svt-av1",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.0##", CultureInfo.InvariantCulture);
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }
}
