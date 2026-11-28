namespace FlowEncode.Infrastructure;

internal static class VapourSynthToolchainPolicy
{
    internal const string VapourSynthVersion = "82";
    internal const string VsjetpackPackageSpec = "vsjetpack[full]";
    internal const string VsWheelsIndex = "https://jaded-encoding-thaumaturgy.github.io/vs-wheels/simple";
    internal const string AwsmfuncVersion = "1.3.5";
    internal const string ReleaseUrl = "https://github.com/vapoursynth/vapoursynth/releases/tag/R82";
    internal const string Constraints = "vapoursynth==" + VapourSynthVersion;

    internal static string PackageSpec(string distribution) => distribution.ToLowerInvariant() switch
    {
        "vapoursynth" => "vapoursynth==" + VapourSynthVersion,
        "awsmfunc" => "awsmfunc==" + AwsmfuncVersion,
        _ => distribution
    };

    internal static IReadOnlyList<string> InstallArguments(string package, string constraintPath) =>
        ["-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "--progress-bar", "off",
            "--only-binary=:all:", "--constraint", constraintPath, PackageSpec(package)];

    internal static IReadOnlyList<string> VsjetpackInstallArguments() =>
        ["-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "--progress-bar", "off",
            VsjetpackPackageSpec, "--extra-index-url", VsWheelsIndex];
}
