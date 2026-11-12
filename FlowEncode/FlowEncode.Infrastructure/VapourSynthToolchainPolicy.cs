using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class VapourSynthToolchainPolicy
{
    // R80 removes API3. Keep the existing plugin ecosystem usable until its
    // replacement API4 stack has passed script/render regression tests.
    internal const string VapourSynthVersion = "79";
    internal const string VsjetpackVersion = "2.2.2";
    internal const string VsrepoVersion = "2.0.0";
    internal const string AwsmfuncVersion = "1.3.5";
    internal const string ReleaseUrl = "https://github.com/vapoursynth/vapoursynth/releases/tag/R79";
    internal const string Constraints = """
        vapoursynth==79
        vsjetpack==2.2.2
        vsrepo==2.0.0
        awsmfunc==1.3.5
        vapoursynth-akarin==1.4.1
        vapoursynth-vszip==22.0.0
        vsstubs==2.2.0
        annotated-doc==0.0.6
        annotated-types==0.8.0
        build==1.6.1
        colorama==0.4.6
        jetpytools==3.2.0
        markdown-it-py==4.3.0
        mdurl==0.1.2
        numpy==2.5.4
        packaging==26.3
        psutil==7.2.2
        pydantic==2.14.3
        pydantic_core==2.50.4
        Pygments==2.21.0
        pyproject_hooks==1.3.3
        rich==15.0.0
        shellingham==1.5.4
        tqdm==4.70.1
        typer==0.29.1
        typing_extensions==4.16.0
        typing-inspection==0.4.5
        uv-build==0.13.7
        vsutil==0.8.0
        vs-rekt==1.0.0
        """;

    internal static string PackageSpec(string distribution) => distribution.ToLowerInvariant() switch
    {
        "vapoursynth" => "vapoursynth==" + VapourSynthVersion,
        "vsjetpack" => "vsjetpack==" + VsjetpackVersion,
        "vsrepo" => "vsrepo==" + VsrepoVersion,
        "awsmfunc" => "awsmfunc==" + AwsmfuncVersion,
        _ => distribution
    };

    internal static string RecommendedVersion(string distribution, string upstream) => distribution.ToLowerInvariant() switch
    {
        "vapoursynth" => VapourSynthVersion,
        "vsjetpack" => VsjetpackVersion,
        "vsrepo" => VsrepoVersion,
        "awsmfunc" => AwsmfuncVersion,
        _ => upstream
    };

    internal static IReadOnlyList<string> InstallArguments(string package, string constraintPath) =>
        ["-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "--progress-bar", "off",
            "--only-binary=:all:", "--constraint", constraintPath, PackageSpec(package)];
}
