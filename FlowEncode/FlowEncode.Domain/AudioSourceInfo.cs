using System;
using System.Collections.Generic;

namespace FlowEncode.Domain;

public sealed record AudioSourceInfo(
    string CodecName,
    string ProfileName,
    int Channels,
    string ChannelLayout,
    int? SampleRate,
    int? BitDepth,
    double? DurationSeconds)
{
    private static readonly HashSet<string> LosslessCodecNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac",
        "alac",
        "wavpack",
        "ape",
        "tak",
        "tta",
        "truehd",
        "mlp"
    };

    public AudioChannelProfile? InferProfile()
    {
        return Channels switch
        {
            1 => AudioChannelProfile.Mono,
            2 => AudioChannelProfile.Stereo,
            6 => AudioChannelProfile.Surround51,
            8 => AudioChannelProfile.Surround71,
            _ => null
        };
    }

    public bool IsLossless()
    {
        if (string.IsNullOrWhiteSpace(CodecName))
        {
            return false;
        }

        if (LosslessCodecNames.Contains(CodecName))
        {
            return true;
        }

        if (CodecName.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)
            || CodecName.StartsWith("dsd", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(CodecName, "dts", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(ProfileName)
            && ProfileName.Contains("DTS-HD MA", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasStereoOrGreaterChannels() => Channels >= 2;

    public bool HasSurround51Or71Channels() => Channels is 6 or 8;
}
