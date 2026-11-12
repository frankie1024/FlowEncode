using System.Text.RegularExpressions;

namespace FlowEncode.Domain;

public static class ToolchainBaseline
{
    public static string VersionFor(SetupDependencyKind kind) => kind switch
    {
        SetupDependencyKind.Python312 => "3.14.10",
        SetupDependencyKind.VapourSynth => "R79",
        SetupDependencyKind.Vsrepo => "2.0.0",
        SetupDependencyKind.VsPluginBundle => "2026-11-12",
        SetupDependencyKind.Awsmfunc => "1.3.5",
        SetupDependencyKind.Vsjetpack => "2.2.2",
        SetupDependencyKind.FfmpegBundle => "9.0.2",
        SetupDependencyKind.X264 => "0.165.3222 (stable b35605a)",
        SetupDependencyKind.X265 => "4.3 (8+10-bit multilib)",
        SetupDependencyKind.SvtAv1 => "4.3.0",
        SetupDependencyKind.Av1an => "0.6.0-flowencode.1",
        _ => string.Empty
    };

    public static bool IsRecommendedInstallation(SetupDependencyStatus status)
        => status.State == ReadinessState.Ready && IsRecommendedVersion(status.Kind, status.InstalledVersion);

    // Compare the detected version, not a minimum-supported version. Unknown versions stay repairable.
    public static bool IsRecommendedVersion(SetupDependencyKind kind, string installedVersion)
    {
        if (!HasBaseline(kind) || string.IsNullOrWhiteSpace(installedVersion)) return false;
        if (kind == SetupDependencyKind.VsPluginBundle)
            return string.Equals(installedVersion, VersionFor(kind), StringComparison.OrdinalIgnoreCase);
        if (kind == SetupDependencyKind.Av1an)
            return Regex.IsMatch(installedVersion, $@"(?<![\w.]){Regex.Escape(VersionFor(kind))}(?![\w.+-])", RegexOptions.IgnoreCase);
        if (kind == SetupDependencyKind.VapourSynth)
            return Regex.IsMatch(installedVersion.Trim(), @"^(?:VapourSynth\s+)?R?79(?:\.0)?(?:\s|$)", RegexOptions.IgnoreCase);
        var expected = Regex.Match(VersionFor(kind), @"\d+(?:\.\d+)+").Value;
        var actual = Regex.Match(installedVersion, @"(?<![\d.])\d+(?:\.\d+)+(?![\d.])");
        return actual.Success && actual.Value == expected
            && !Regex.IsMatch(installedVersion[(actual.Index + actual.Length)..], @"^(?:[-~]?(?:dev|rc|alpha|beta|git)|-\d+-g)", RegexOptions.IgnoreCase);
    }

    public static bool HasBaseline(SetupDependencyKind kind) => VersionFor(kind).Length > 0;
}
