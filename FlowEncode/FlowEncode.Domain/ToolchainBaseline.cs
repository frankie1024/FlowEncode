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
        SetupDependencyKind.Av1an => "0.5.2-flowencode.2",
        _ => string.Empty
    };

    public static bool HasBaseline(SetupDependencyKind kind) => VersionFor(kind).Length > 0;
}
