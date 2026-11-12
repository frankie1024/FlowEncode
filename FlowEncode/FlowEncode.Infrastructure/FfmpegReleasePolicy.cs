using System.Text.Json;
using System.Text.RegularExpressions;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class FfmpegReleasePolicy
{
    // Advance deliberately after testing a tagged release. Even BtbN's release-branch
    // archives are rebuilt daily, so they do not satisfy the stable package policy.
    internal const string Version = "9.0.2";
    internal const string ReleaseUrl = "https://github.com/GyanD/codexffmpeg/releases/tag/" + Version;
    internal const string ApiUrl = "https://api.github.com/repos/GyanD/codexffmpeg/releases/tags/" + Version;
    internal const string AssetName = "ffmpeg-" + Version + "-full_build-shared.zip";
    internal const string Sha256 = "8d31e162f1616e37aab3fa2db991b97e1b8dbeb1c8465fd81a81be16ff91b328";

    internal static string? ReadStableVersion(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(?:(?:ffmpeg|ffprobe) version )?n?(?<version>\d+\.\d+(?:\.\d+)?)(?<suffix>[^\s]*)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var suffix = match.Groups["suffix"].Value;
        if (suffix.Length > 0 && !suffix.StartsWith("-full_build", StringComparison.OrdinalIgnoreCase)
            && !suffix.StartsWith("-essentials_build", StringComparison.OrdinalIgnoreCase)) return null;
        return match.Groups["version"].Value;
    }

    internal static ExternalToolUpdatePackage? SelectPackage(string json, bool useBaseline = true)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        var version = release.GetProperty("tag_name").GetString() ?? string.Empty;
        var assetName = $"ffmpeg-{version}-full_build-shared.zip";
        var releaseUrl = $"https://github.com/GyanD/codexffmpeg/releases/tag/{version}";
        if (!Regex.IsMatch(version, @"^\d+\.\d+(?:\.\d+)?$")
            || useBaseline && version != Version
            || release.GetProperty("draft").GetBoolean()
            || release.GetProperty("prerelease").GetBoolean())
        {
            return null;
        }

        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != assetName)
            {
                continue;
            }

            var digest = asset.TryGetProperty("digest", out var value)
                ? PackageIntegrityVerifier.NormalizeSha256Digest(value.GetString())
                : null;
            if (useBaseline && digest != Sha256) digest = null;
            return new ExternalToolUpdatePackage(
                ExternalToolKind.Ffmpeg, version, assetName, releaseUrl,
                // Use the tagged URL rather than accepting a floating latest URL.
                $"https://github.com/GyanD/codexffmpeg/releases/download/{version}/{assetName}",
                release.GetProperty("published_at").GetDateTimeOffset(),
                $"FFmpeg {version} 稳定正式发布包（Gyan Full Shared，包含 ffmpeg / ffprobe）；不跟随每日构建。"
                    + (digest is null ? " 资源缺少 SHA256，已禁用自动安装。" : string.Empty),
                digest, digest is not null);
        }

        return null;
    }
}
