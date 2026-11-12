using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

// This catalog belongs to the 2.0 maintenance line. Advance only with a new app release.
internal static class LegacyToolchainBaseline
{
    internal sealed record Package(string Repository, string Tag, string Asset, string Sha256)
    {
        internal string ReleaseUrl => $"https://github.com/{Repository}/releases/tag/{Tag}";
        internal string DownloadUrl => $"https://github.com/{Repository}/releases/download/{Tag}/{Asset}";
    }

    internal static Package Encoder(EncoderKind kind) => kind switch
    {
        EncoderKind.X264 => new("frankie1024/x264-windows-builds", "stable-20250608-b35605ace3dd",
            "x264-stable-20250608-b35605ace3dd-x64.zip", "3f60ff87f577307c1d452ff4a221b833d0978627b8ada96819b8d1f2d970cbbb"),
        EncoderKind.X265 => new("frankie1024/x265-windows-builds", "4.2",
            "x265-4.2-x64.zip", "90d3795f31091eeabd05b79e4285b18dd3e823d1b6aec49e97f1cb043b399240"),
        EncoderKind.SvtAv1 => new("frankie1024/svt-av1-windows-builds", "v4.1.0",
            "svt-av1-4.1.0-x64.zip", "825a5fbf4398eece65e3dfdb09df222f1e918547454abb7a3019efea2d3f9206"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static Package Tool(ExternalToolKind kind) => kind switch
    {
        ExternalToolKind.Ffmpeg => new("GyanD/codexffmpeg", "8.1",
            "ffmpeg-8.1-full_build-shared.zip", "e184e012ca9a3527ed45d61f8e250c50b07b8661d90849820ea754239049efb4"),
        ExternalToolKind.Av1an => new("frankie1024/Av1an", "v0.5.2-flowencode.2",
            "av1an.exe", "e6366b7ba3345f3a0fd8d4773a162ce0593c4c191c10ea5c058e4b97cb73eacf"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal const string Notice = "FlowEncode 2.0 兼容基线 / compatibility baseline. Upgrade the app before adopting a newer toolchain.";
    private static readonly DateTimeOffset SnapshotDate = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    internal static IReadOnlyList<EncoderUpdatePackage> EncoderPackages() => Enum.GetValues<EncoderKind>().Select(kind =>
    {
        var pin = Encoder(kind);
        return new EncoderUpdatePackage(kind, EncoderArchitecture.X64, pin.Tag.TrimStart('v'), pin.Asset,
            pin.ReleaseUrl, pin.DownloadUrl, SnapshotDate, Notice, pin.Sha256, true);
    }).ToArray();

    internal static IReadOnlyList<ExternalToolUpdatePackage> ToolPackages() => Enum.GetValues<ExternalToolKind>().Select(kind =>
    {
        var pin = Tool(kind);
        return new ExternalToolUpdatePackage(kind, pin.Tag.TrimStart('v'), pin.Asset,
            pin.ReleaseUrl, pin.DownloadUrl, SnapshotDate, Notice, pin.Sha256, true);
    }).ToArray();

    internal static bool Matches(Package pin, string asset, string url, string? digest) =>
        pin.Asset == asset && pin.DownloadUrl == url && pin.Sha256 == digest;
}
