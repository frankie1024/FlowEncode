using System.Runtime.Versioning;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class SetupBootstrapServiceVersionUpdateTests
{
    [TestMethod]
    public void VsjetpackInstall_UsesFullExtraAndWheelIndexWithoutPinning()
    {
        var arguments = VapourSynthToolchainPolicy.VsjetpackInstallArguments();

        CollectionAssert.AreEqual(new[] { "-m", "pip", "install", "--upgrade", "--disable-pip-version-check",
            "--progress-bar", "off", "vsjetpack[full]", "--extra-index-url",
            "https://jaded-encoding-thaumaturgy.github.io/vs-wheels/simple" }, arguments.ToArray());
        Assert.IsFalse(ToolchainBaseline.HasBaseline(SetupDependencyKind.Vsjetpack));
    }

    [TestMethod]
    [DataRow("3.12.10", true, false, false)]
    [DataRow("3.13.9", true, false, false)]
    [DataRow("3.14.0", true, false, true)]
    [DataRow("3.15.2", true, false, false)]
    [DataRow("4.0.0", true, false, false)]
    [DataRow("3.13.9", true, true, false)]
    [DataRow("3.13.9", false, false, false)]
    public void IsPythonReleaseEligible_EnforcesSupportedStableRange(
        string versionText,
        bool isPublished,
        bool isPreRelease,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            SetupBootstrapService.IsPythonReleaseEligible(
                Version.Parse(versionText),
                isPublished,
                isPreRelease));
    }

    [TestMethod]
    public void IsPythonInstallerFileEligible_RequiresX64InstallerAndSha256()
    {
        const string sha256 = "67b5635e80ea51072b87941312d00ec8927c4db9ba18938f7ad2d27b328b95fb";

        Assert.IsTrue(SetupBootstrapService.IsPythonInstallerFileEligible(
            "Windows installer (64-bit)",
            "https://python.example/python-3.13.9-amd64.exe",
            sha256));
        Assert.IsFalse(SetupBootstrapService.IsPythonInstallerFileEligible(
            "Windows installer (64-bit)",
            "https://python.example/python-3.13.9-amd64.exe",
            null));
        Assert.IsFalse(SetupBootstrapService.IsPythonInstallerFileEligible(
            "Windows installer (32-bit)",
            "https://python.example/python-3.13.9.exe",
            sha256));
    }

    [TestMethod]
    public void HasSetupDependencyVersionUpdate_X264SameShortAndLongRevision_ReturnsFalse()
    {
        var result = SetupBootstrapService.HasSetupDependencyVersionUpdate(
            SetupDependencyKind.X264,
            "x264 0.165.3222 b35605a",
            "stable-20250608-b35605ace3dd");

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void HasSetupDependencyVersionUpdate_X264DifferentDatedRevision_ReturnsTrue()
    {
        var result = SetupBootstrapService.HasSetupDependencyVersionUpdate(
            SetupDependencyKind.X264,
            "stable-20250601-a1234567890",
            "stable-20250608-b35605ace3dd");

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasSetupDependencyVersionUpdate_X265SemanticVersionStillCompares()
    {
        var result = SetupBootstrapService.HasSetupDependencyVersionUpdate(
            SetupDependencyKind.X265,
            "HEVC encoder version 4.1+12",
            "4.2");

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasSetupDependencyVersionUpdate_X265RequiresMultilibBitDepthWhenExpected()
    {
        Assert.IsTrue(SetupBootstrapService.HasSetupDependencyVersionUpdate(
            SetupDependencyKind.X265,
            "HEVC encoder version 4.3 (8bit)",
            "4.3 (8+10-bit multilib)"));
        Assert.IsTrue(SetupBootstrapService.HasSetupDependencyVersionUpdate(
            SetupDependencyKind.X265,
            "HEVC encoder version 4.3",
            "4.3-multilib.1"));
        Assert.IsFalse(SetupBootstrapService.HasSetupDependencyVersionUpdate(
            SetupDependencyKind.X265,
            string.Empty,
            "4.3 (8+10-bit multilib)"));
        Assert.IsFalse(SetupBootstrapService.HasSetupDependencyVersionUpdate(
            SetupDependencyKind.X265,
            "HEVC encoder version 4.3 (8bit+10bit)",
            "4.3 (8+10-bit multilib)"));
    }

    [TestMethod]
    public void IsInstalledVersionVerified_EncoderRevisionAndSemanticVersions_RequireCurrentVersion()
    {
        Assert.IsTrue(SetupBootstrapService.IsInstalledVersionVerified(
            SetupDependencyKind.X264,
            "x264 0.165.3222 b35605a",
            "stable-20250608-b35605ace3dd"));
        Assert.IsFalse(SetupBootstrapService.IsInstalledVersionVerified(
            SetupDependencyKind.X264,
            "stable-20250601-a1234567890",
            "stable-20250608-b35605ace3dd"));
        Assert.IsTrue(SetupBootstrapService.IsInstalledVersionVerified(
            SetupDependencyKind.X265,
            "HEVC encoder version 4.2",
            "4.2"));
        Assert.IsFalse(SetupBootstrapService.IsInstalledVersionVerified(
            SetupDependencyKind.X265,
            "HEVC encoder version 4.3 (8bit)",
            "4.3 (8+10-bit multilib)"));
        Assert.IsFalse(SetupBootstrapService.IsInstalledVersionVerified(
            SetupDependencyKind.X265,
            "HEVC encoder version 4.3",
            "4.3 (8+10-bit multilib)"));
        Assert.IsTrue(SetupBootstrapService.IsInstalledVersionVerified(
            SetupDependencyKind.X265,
            "HEVC encoder version 4.3 (8bit+10bit)",
            "4.3 (8+10-bit multilib)"));
    }

    [TestMethod]
    public void IsInstalledVersionVerified_FfmpegBuildDate_RejectsOlderBuild()
    {
        Assert.IsTrue(SetupBootstrapService.IsInstalledVersionVerified(
            SetupDependencyKind.FfmpegBundle,
            "ffmpeg version N-125800-20260728",
            "Latest Auto-Build (2026-07-28 13:32)"));
        Assert.IsFalse(SetupBootstrapService.IsInstalledVersionVerified(
            SetupDependencyKind.FfmpegBundle,
            "ffmpeg version N-125781-20260727",
            "Latest Auto-Build (2026-07-28 13:32)"));
    }

    [TestMethod]
    public void IsInstalledVersionVerified_UnusableProbeOrOlderToolVersion_ReturnsFalse()
    {
        Assert.IsFalse(SetupBootstrapService.IsInstalledVersionVerified(
            SetupDependencyKind.SvtAv1,
            "Present (version probe failed)",
            "4.2.0"));
        Assert.IsFalse(SetupBootstrapService.IsInstalledVersionVerified(
            SetupDependencyKind.Av1an,
            "av1an 0.5.1",
            "0.5.2"));
        Assert.IsTrue(SetupBootstrapService.IsInstalledVersionVerified(
            SetupDependencyKind.Av1an,
            "av1an 0.5.2-unstable (rev 75685bc)",
            "0.5.2"));
    }
}
