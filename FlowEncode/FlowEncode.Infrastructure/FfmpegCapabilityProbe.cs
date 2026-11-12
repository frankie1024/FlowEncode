using System.Collections.Concurrent;
using System.Diagnostics;

namespace FlowEncode.Infrastructure;

internal sealed record FfmpegCapabilities(bool Y4m, bool LibOpus, bool ChannelMap, bool PcmWav);

internal static class FfmpegCapabilityProbe
{
    private static readonly ConcurrentDictionary<string, FfmpegCapabilities> Cache = new();
    private static readonly ConcurrentDictionary<string, string> VersionCache = new();

    private static string Identity(string executable)
    {
        var file = new FileInfo(executable);
        var files = Directory.EnumerateFiles(file.DirectoryName!, "*.dll").Append(file.FullName)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(static path => new FileInfo(path));
        return string.Join('|', files.Select(static item => $"{item.FullName}:{item.Length}:{item.LastWriteTimeUtc.Ticks}"));
    }

    internal static FfmpegCapabilities Probe(string executable, CancellationToken token = default)
    {
        var key = Identity(executable);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var muxers = Run(executable, ["-hide_banner", "-muxers"], token);
        var encoders = Run(executable, ["-hide_banner", "-encoders"], token);
        var filters = Run(executable, ["-hide_banner", "-filters"], token);
        var result = new FfmpegCapabilities(ContainsWord(muxers, "yuv4mpegpipe"), ContainsWord(encoders, "libopus"),
            ContainsWord(filters, "channelmap"), ContainsWord(encoders, "pcm_s16le") && ContainsWord(muxers, "wav"));
        Cache[key] = result;
        return result;
    }

    internal static void RequireVideo(string executable, CancellationToken token = default)
    {
        if (!Probe(executable, token).Y4m)
            throw new InvalidOperationException("FFmpeg lacks the yuv4mpegpipe muxer required for video input. / 当前 FFmpeg 缺少视频输入所需的 Y4M muxer。");
    }

    internal static void RequireOpus(string executable, bool direct, CancellationToken token)
    {
        var capability = Probe(executable, token);
        if (direct ? !capability.LibOpus || !capability.ChannelMap : !capability.PcmWav)
            throw new InvalidOperationException(direct
                ? "FFmpeg requires libopus and channelmap for this multichannel workflow. / 当前多声道流程需要含 libopus 和 channelmap 的 FFmpeg。"
                : "FFmpeg lacks PCM/WAV support required by opusenc. / 当前 FFmpeg 缺少 opusenc 管线所需的 PCM/WAV 支持。");
    }

    internal static void ValidatePair(string ffmpeg, string ffprobe, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var left = VersionCache.GetOrAdd(Identity(ffmpeg), _ => Run(ffmpeg, ["-version"], token).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0]);
        var right = VersionCache.GetOrAdd(Identity(ffprobe), _ => Run(ffprobe, ["-version"], token).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0]);
        var ffmpegVersion = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2);
        var ffprobeVersion = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2);
        if (string.IsNullOrWhiteSpace(ffmpegVersion) || ffmpegVersion != ffprobeVersion)
            throw new InvalidOperationException($"FFmpeg/FFprobe build mismatch. Import both from the same package. / FFmpeg 与 FFprobe 构建不一致，请从同一工具包导入。{Environment.NewLine}{ffmpeg}: {left}{Environment.NewLine}{ffprobe}: {right}");
    }

    private static bool ContainsWord(string value, string word) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(word, StringComparer.Ordinal);

    private static string Run(string executable, IReadOnlyList<string> args, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        var result = ProcessProbeRunner.Run(start, TimeSpan.FromSeconds(15), "FFmpeg capability probe timed out.", token);
        if (result.ExitCode != 0) throw new InvalidOperationException($"FFmpeg capability probe failed: {result.StandardError}");
        return result.StandardOutput + Environment.NewLine + result.StandardError;
    }
}
