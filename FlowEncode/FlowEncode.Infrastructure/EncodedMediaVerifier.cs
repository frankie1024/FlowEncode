using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlowEncode.Infrastructure;

internal static class EncodedMediaVerifier
{
    internal static void VerifyVideo(string ffprobePath, string path, VideoOutputContract contract, CancellationToken token)
    {
        var json = Probe(ffprobePath, path, "v:0",
            "stream=codec_name,pix_fmt,nb_read_frames", contract.Frames.HasValue, token);
        ValidateVideo(json, contract);
    }

    internal static void ValidateVideo(string json, VideoOutputContract contract)
    {
        using var document = JsonDocument.Parse(json);
        var stream = FirstStream(document.RootElement);
        var expectedCodec = contract.Kind switch
        {
            Domain.EncoderKind.X264 => "h264", Domain.EncoderKind.X265 => "hevc", _ => "av1"
        };
        var codec = stream.GetProperty("codec_name").GetString();
        var pixelFormat = stream.GetProperty("pix_fmt").GetString() ?? string.Empty;
        var match = Regex.Match(pixelFormat, @"p(?<depth>\d+)(?:le|be)$");
        var depth = match.Success ? int.Parse(match.Groups["depth"].Value, CultureInfo.InvariantCulture) : 8;
        if (codec != expectedCodec || depth != contract.BitDepth
            || contract.Chroma is not null && VideoOutputContract.ChromaOf(pixelFormat) != contract.Chroma)
            throw new InvalidOperationException($"Encoded video differs from the plan: expected {expectedCodec} {contract.BitDepth}-bit {contract.Chroma}, received {codec} {pixelFormat}. / 编码产物的格式、位深或色度与计划不符。");
        if (contract.Frames.HasValue
            && (!stream.TryGetProperty("nb_read_frames", out var value)
                || !long.TryParse(value.GetString(), out var actualFrames) || actualFrames != contract.Frames.Value))
            throw new InvalidOperationException($"Encoded frame count differs from the plan ({contract.Frames}). / 编码产物帧数与计划不符。");
    }

    internal static void VerifyOpus(string ffprobePath, string path, int? channels, CancellationToken token)
    {
        using var document = JsonDocument.Parse(Probe(ffprobePath, path, "a:0", "stream=codec_name,channels,sample_rate", false, token));
        var stream = FirstStream(document.RootElement);
        if (stream.GetProperty("codec_name").GetString() != "opus"
            || channels.HasValue && stream.GetProperty("channels").GetInt32() != channels.Value
            || stream.GetProperty("sample_rate").GetString() != "48000")
            throw new InvalidOperationException("Opus output codec/channel count/sample rate differs from the plan. / Opus 产物的编码、声道数或采样率与计划不符。");
    }

    private static JsonElement FirstStream(JsonElement root)
    {
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            throw new InvalidOperationException("The output contains no readable media stream. / 产物没有可读取的媒体流。");
        return streams[0];
    }

    private static string Probe(string executable, string path, string streams, string entries, bool countFrames, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException("The encoder produced no media. / 编码器未生成有效媒体文件。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-v", "error", "-select_streams", streams, "-show_entries", entries, "-of", "json" }) start.ArgumentList.Add(argument);
        if (countFrames) start.ArgumentList.Add("-count_frames");
        start.ArgumentList.Add(path);
        var result = ProcessProbeRunner.Run(start, countFrames ? TimeSpan.FromHours(6) : TimeSpan.FromSeconds(30), "Media verification timed out. / 媒体校验超时。", token);
        if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StandardError))
            throw new InvalidOperationException($"Media verification failed. / 媒体校验失败：{result.StandardError}");
        return result.StandardOutput;
    }
}
