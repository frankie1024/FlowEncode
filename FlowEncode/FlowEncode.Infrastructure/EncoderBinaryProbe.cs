using System.Diagnostics;
using System.Text.RegularExpressions;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class EncoderBinaryProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private const string TimeoutMessage = "Version probe timed out.";

    public static string ProbeVersion(string executablePath, EncoderKind kind)
    {
        try
        {
            var result = ProcessProbeRunner.Run(
                new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                ProbeTimeout,
                TimeoutMessage);

            var output = string.Concat(
                result.StandardOutput,
                Environment.NewLine,
                result.StandardError).Trim();

            if (result.ExitCode != 0)
            {
                return $"Present (version probe failed with exit code {result.ExitCode})";
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return "Present (version string unavailable)";
            }

            var parsed = ParseVersion(output, kind);
            return string.IsNullOrWhiteSpace(parsed) ? FirstMeaningfulLine(output) : parsed;
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, TimeoutMessage, StringComparison.Ordinal))
        {
            return "Present (version probe timed out)";
        }
        catch
        {
            return "Present (version probe failed)";
        }
    }

    public static EncoderArchitecture DetectArchitecture(string executablePath)
    {
        try
        {
            using var stream = File.OpenRead(executablePath);
            using var reader = new BinaryReader(stream);

            stream.Seek(0x3C, SeekOrigin.Begin);
            var peHeaderOffset = reader.ReadInt32();
            stream.Seek(peHeaderOffset + 4, SeekOrigin.Begin);
            var machine = reader.ReadUInt16();

            return machine switch
            {
                0x014C => EncoderArchitecture.X86,
                0x8664 => EncoderArchitecture.X64,
                0xAA64 => EncoderArchitecture.X64,
                _ => Environment.Is64BitOperatingSystem ? EncoderArchitecture.X64 : EncoderArchitecture.X86
            };
        }
        catch
        {
            return Environment.Is64BitOperatingSystem ? EncoderArchitecture.X64 : EncoderArchitecture.X86;
        }
    }

    internal static string ParseVersion(string output, EncoderKind kind)
    {
        if (kind == EncoderKind.X265)
        {
            return ParseX265Version(output);
        }

        var match = kind switch
        {
            EncoderKind.X264 => Regex.Match(output, @"\bx264\s+\d+\.\d+\.\d+(?:\s+[A-Za-z0-9]+)?", RegexOptions.IgnoreCase),
            EncoderKind.SvtAv1 => Regex.Match(output, @"\bSVT-AV1\s+Encoder\s+Lib\s+v\d+\.\d+(?:\.\d+)?", RegexOptions.IgnoreCase),
            _ => Match.Empty
        };

        return match.Success ? match.Value : string.Empty;
    }

    private static string ParseX265Version(string output)
    {
        var versionMatch = Regex.Match(output, @"\bHEVC\s+encoder\s+version\s+\d+\.\d+(?:\+\d+)?", RegexOptions.IgnoreCase);
        if (!versionMatch.Success)
        {
            return string.Empty;
        }

        var bitDepthMatch = Regex.Match(
            output,
            @"\bbuild\s+info\b[^\r\n]*?\b(?<depths>(?:8|10|12)bit(?:\+(?:8|10|12)bit)*)\b",
            RegexOptions.IgnoreCase);

        return bitDepthMatch.Success
            ? $"{versionMatch.Value} ({bitDepthMatch.Groups["depths"].Value.ToLowerInvariant()})"
            : versionMatch.Value;
    }

    private static string FirstMeaningfulLine(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? "Present";
    }
}
