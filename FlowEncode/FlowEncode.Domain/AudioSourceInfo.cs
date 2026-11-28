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
    double? DurationSeconds,
    string? Language = null,
    string? Title = null)
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

    public static bool IsDeewChannelCountSupported(int channels) => channels is 1 or 2 or 5 or 6 or 7 or 8;

    public bool IsDts => CodecName.Equals("dts", StringComparison.OrdinalIgnoreCase);

    public bool IsDtsX => IsDts && ProfileName.Contains("DTS:X", StringComparison.OrdinalIgnoreCase);

    public string? Auro3DLayout { get; init; }

    public int DeewChannelCount
    {
        get
        {
            if (!IsDts) return Channels;
            var layout = ChannelLayout.Split('.');
            if (layout.Length == 3 && Array.TrueForAll(layout, static part => part.Length > 0 && part.All(char.IsAsciiDigit))
                && int.TryParse(layout[0], out var bed))
                return bed >= 7 ? 8 : 6;
            return Math.Min(Channels, 8);
        }
    }

    public bool IsTrueHdAtmos => CodecName.Equals("truehd", StringComparison.OrdinalIgnoreCase)
        && ProfileName.Contains("Atmos", StringComparison.OrdinalIgnoreCase);
}
