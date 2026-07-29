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
    [DataRow(@"x64\7za.exe", true)]
    [DataRow("/x64/7ZA.EXE", true)]
    [DataRow(@"arm64\7za.exe", false)]
    [DataRow("7za.exe", false)]
    public void IsPortable7ZipExecutableEntry_AcceptsOnlyX64Executable(string entryName, bool expected)
    {
        Assert.AreEqual(expected, SetupBootstrapService.IsPortable7ZipExecutableEntry(entryName));
    }

    [TestMethod]
    [DataRow(@"C:\Python\Lib\site-packages\vsrepo\7z.exe", true)]
    [DataRow(@"C:\Python\Lib\site-packages\vsrepo\7z.dll", true)]
    [DataRow(@"C:\Python\Lib\site-packages\other\7z.exe", false)]
    [DataRow(@"C:\Python\Lib\site-packages\vsrepo\python.exe", false)]
    public void IsSafeVsrepoExtractorPath_RestrictsDeletionToVsrepoExtractorFiles(
        string path,
        bool expected)
    {
        Assert.AreEqual(expected, SetupBootstrapService.IsSafeVsrepoExtractorPath(path));
    }

    [TestMethod]
    [DataRow("3.12.10", true, false, true)]
    [DataRow("3.13.9", true, false, true)]
    [DataRow("3.14.0", true, false, true)]
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
    [DataRow(ReadinessState.Ready, ReadinessState.Ready, ReadinessState.Ready)]
    [DataRow(ReadinessState.Ready, ReadinessState.Missing, ReadinessState.Partial)]
    [DataRow(ReadinessState.Ready, ReadinessState.Misconfigured, ReadinessState.Misconfigured)]
    [DataRow(ReadinessState.Missing, ReadinessState.Missing, ReadinessState.Missing)]
    public void ResolveModuleAndCliReadiness_RequiresBothSurfaces(
        ReadinessState moduleState,
        ReadinessState cliState,
        ReadinessState expected)
    {
        Assert.AreEqual(
            expected,
            SetupBootstrapService.ResolveModuleAndCliReadiness(moduleState, cliState));
    }

    [TestMethod]
    public void BuildVsPluginBundlePackagePlan_AllPackagesInstalled_OnlyUpgradesBundle()
    {
        var installedPackages = new[] { "ffms2", "fpng", "libp2p", "lsmas", "placebo", "mvsfunc", "havsfunc" };

        var plan = SetupBootstrapService.BuildVsPluginBundlePackagePlan(installedPackages);

        Assert.AreEqual(0, plan.InstallPackages.Count);
        CollectionAssert.AreEqual(
            installedPackages,
            plan.UpgradePackages.ToArray());
    }

    [TestMethod]
    public void BuildVsPluginBundlePackagePlan_MissingPackages_InstallsMissingThenUpgradesBundle()
    {
        var plan = SetupBootstrapService.BuildVsPluginBundlePackagePlan(["ffms2", "lsmas"]);

        CollectionAssert.AreEqual(
            new[] { "fpng", "libp2p", "placebo", "mvsfunc", "havsfunc" },
            plan.InstallPackages.ToArray());
        CollectionAssert.AreEqual(
            new[] { "ffms2", "fpng", "libp2p", "lsmas", "placebo", "mvsfunc", "havsfunc" },
            plan.UpgradePackages.ToArray());
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
