using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class FfprobeAudioSourceInfoService : IAudioSourceInfoService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    private readonly IToolProbeService _toolProbeService;

    public FfprobeAudioSourceInfoService(IToolProbeService toolProbeService)
    {
        _toolProbeService = toolProbeService;
    }

    public async Task<AudioSourceInfo?> ProbeAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        var probe = await _toolProbeService.ProbeAsync(RegisteredToolKind.Ffprobe, cancellationToken);
        if (!probe.IsReady || string.IsNullOrWhiteSpace(probe.ExecutablePath))
        {
            return null;
        }

        var output = await RunFfprobeAsync(
            probe.ExecutablePath,
            $"-v error -select_streams a:0 -show_entries format=duration,format_name:stream=codec_name,profile,channels,channel_layout,sample_rate,bits_per_sample,bits_per_raw_sample,sample_fmt,duration -of json {Quote(sourcePath)}",
            "ffprobe failed to inspect the selected audio source.",
            cancellationToken);

        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var formatDuration = document.RootElement.TryGetProperty("format", out var formatElement)
            ? ParseNullableDouble(GetJsonString(formatElement, "duration"))
            : null;
        var formatName = document.RootElement.TryGetProperty("format", out formatElement)
            ? GetJsonString(formatElement, "format_name")
            : string.Empty;

        foreach (var stream in streams.EnumerateArray())
        {
            var codecName = GetJsonString(stream, "codec_name");
            var durationSeconds = ParseNullableDouble(GetJsonString(stream, "duration")) ?? formatDuration;
            if ((!durationSeconds.HasValue || durationSeconds.Value <= 0)
                && ShouldEstimatePacketDuration(codecName, formatName))
            {
                try
                {
                    durationSeconds = await EstimateDurationFromPacketsAsync(probe.ExecutablePath, sourcePath, cancellationToken)
                        ?? durationSeconds;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to estimate audio duration from packets for '{sourcePath}'. {ex}");
                }
            }

            return new AudioSourceInfo(
                codecName,
                GetJsonString(stream, "profile"),
                ParseInt(GetJsonString(stream, "channels")),
                GetJsonString(stream, "channel_layout"),
                ParseNullableInt(GetJsonString(stream, "sample_rate")),
                ResolveBitDepth(stream),
                durationSeconds);
        }

        return null;
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static async Task<string> RunFfprobeAsync(
        string ffprobePath,
        string arguments,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var result = await ProcessProbeRunner.RunAsync(
            new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            ProbeTimeout,
            "ffprobe audio source probe timed out.",
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
                ? failureMessage
                : result.StandardError.Trim());
        }

        return result.StandardOutput;
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.ToString()
            : string.Empty;
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int? ParseNullableInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ParseNullableDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? ResolveBitDepth(JsonElement stream)
    {
        var bitsPerSample = ParseNullableInt(GetJsonString(stream, "bits_per_sample"));
        if (bitsPerSample is > 0)
        {
            return bitsPerSample;
        }

        var bitsPerRawSample = ParseNullableInt(GetJsonString(stream, "bits_per_raw_sample"));
        if (bitsPerRawSample is > 0)
        {
            return bitsPerRawSample;
        }

        return InferBitDepthFromSampleFormat(GetJsonString(stream, "sample_fmt"));
    }

    private static int? InferBitDepthFromSampleFormat(string sampleFormat)
    {
        return sampleFormat.ToLowerInvariant() switch
        {
            "u8" or "u8p" => 8,
            "s16" or "s16p" => 16,
            "s24" or "s24p" => 24,
            "s32" or "s32p" or "flt" or "fltp" => 32,
            "dbl" or "dblp" => 64,
            _ => null
        };
    }

    private static bool ShouldEstimatePacketDuration(string codecName, string formatName)
    {
        return codecName.Equals("truehd", StringComparison.OrdinalIgnoreCase)
            || codecName.Equals("mlp", StringComparison.OrdinalIgnoreCase)
            || formatName.Contains("truehd", StringComparison.OrdinalIgnoreCase)
            || formatName.Contains("mlp", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<double?> EstimateDurationFromPacketsAsync(
        string ffprobePath,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var packetDurationSeconds = await ReadFirstPacketDurationSecondsAsync(ffprobePath, sourcePath, cancellationToken);
        var packetCount = await ReadPacketCountAsync(ffprobePath, sourcePath, cancellationToken);
        if (!packetDurationSeconds.HasValue
            || packetDurationSeconds.Value <= 0
            || !packetCount.HasValue
            || packetCount.Value <= 0)
        {
            return null;
        }

        return packetDurationSeconds.Value * packetCount.Value;
    }

    private static async Task<double?> ReadFirstPacketDurationSecondsAsync(
        string ffprobePath,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var output = await RunFfprobeAsync(
            ffprobePath,
            $"-v error -select_streams a:0 -show_packets -show_entries packet=duration_time -read_intervals 0%+#1 -of json {Quote(sourcePath)}",
            "ffprobe failed to inspect the selected audio source.",
            cancellationToken);

        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("packets", out var packets)
            || packets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var packet in packets.EnumerateArray())
        {
            return ParseNullableDouble(GetJsonString(packet, "duration_time"));
        }

        return null;
    }

    private static async Task<long?> ReadPacketCountAsync(
        string ffprobePath,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var output = await RunFfprobeAsync(
            ffprobePath,
            $"-v error -select_streams a:0 -count_packets -show_entries stream=nb_read_packets -of json {Quote(sourcePath)}",
            "ffprobe failed to inspect the selected audio source.",
            cancellationToken);

        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var stream in streams.EnumerateArray())
        {
            var rawPacketCount = GetJsonString(stream, "nb_read_packets");
            if (long.TryParse(rawPacketCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var packetCount))
            {
                return packetCount;
            }
        }

        return null;
    }
}
