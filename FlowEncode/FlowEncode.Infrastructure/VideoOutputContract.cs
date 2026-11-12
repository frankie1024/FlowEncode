using System.Globalization;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal sealed record VideoOutputContract(EncoderKind Kind, int BitDepth, string? Chroma, long? Frames)
{
    internal static VideoOutputContract Resolve(EncodingProfile profile, SourceVideoInfo? source)
    {
        var options = EncoderCliParameters.Read(profile.AdditionalArguments,
            profile.Kind == EncoderKind.X265 ? profile.UhdParameters : null);
        var name = options.GetValueOrDefault("--profile", profile.Profile).ToLowerInvariant();
        var depth = EncoderCliParameters.ResolveOutputDepth(profile, source?.BitDepth is > 0 ? source.BitDepth : null) ?? 8;
        var chroma = ChromaOf(source?.PixelFormat);
        if (chroma == "rgb")
            throw new ArgumentException("Convert RGB input to a supported YUV format before using the Y4M pipeline. / 请先将 RGB 输入转换为所需 YUV 格式。");
        if (profile.Kind == EncoderKind.X264 && options.TryGetValue("--output-csp", out var csp))
            chroma = csp.ToLowerInvariant() switch { "i420" => "420", "i422" => "422", "i444" => "444", _ => chroma };

        if (profile.Kind == EncoderKind.SvtAv1)
        {
            if (depth is not (8 or 10) || chroma is not (null or "420"))
                throw new ArgumentException("SVT-AV1 requires 8/10-bit YUV420 input. Convert the script/source first. / SVT-AV1 需要 8/10 位 YUV420 输入，请先转换脚本或源文件。");
            var svtProfile = EncoderArgumentValueNormalizer.NormalizeProfileForCli(profile.Kind, name);
            if (svtProfile is "1" or "2")
                throw new ArgumentException("This SVT-AV1 pipeline supports Main profile with YUV420 input. / 当前 SVT-AV1 管线支持 YUV420 Main profile，所选 high/professional 组合不可用。");
            if (options.TryGetValue("--input-depth", out var inputDepth) && inputDepth != depth.ToString(CultureInfo.InvariantCulture))
                throw new ArgumentException("SVT input depth does not match the probed source. / SVT 附加输入位深与源信息不一致。");
        }

        if (profile.Kind == EncoderKind.X264 && name is "baseline" or "main" or "high" or "high10"
            && chroma is not (null or "420"))
            throw new ArgumentException("The selected H.264 profile requires YUV420. / 所选 H.264 profile 需要 YUV420。");
        if (profile.Kind == EncoderKind.X265 && name is "main" or "main10" or "mainstillpicture"
            && chroma is not (null or "420"))
            throw new ArgumentException("The selected HEVC profile requires YUV420. / 所选 HEVC profile 需要 YUV420。");
        var frames = source?.IsFrameCountEstimated == true ? null : source?.TotalFrames;
        if (options.TryGetValue("--seek", out var seekValue) || options.TryGetValue("--skip", out seekValue))
            if (long.TryParse(seekValue, out var skip) && frames.HasValue) frames = Math.Max(0, frames.Value - skip);
        if (options.TryGetValue("--frames", out var frameValue) || options.TryGetValue("-n", out frameValue))
            if (long.TryParse(frameValue, out var limit) && limit > 0) frames = frames.HasValue ? Math.Min(frames.Value, limit) : limit;
        return new VideoOutputContract(profile.Kind, depth, chroma, frames);
    }

    internal static string? ChromaOf(string? pixelFormat)
    {
        var value = pixelFormat?.ToLowerInvariant() ?? string.Empty;
        if (value is "yv12" or "i420") return "420";
        if (value is "yv16" or "yuy2") return "422";
        if (value == "yv24") return "444";
        foreach (var chroma in new[] { "420", "422", "444" })
            if (value.Contains(chroma, StringComparison.Ordinal)) return chroma;
        return value.StartsWith("gray", StringComparison.Ordinal) ? "400" : value.Length == 0 || value == "unknown" ? null : "rgb";
    }
}
