using System.Text.Json;
using System.Runtime.Versioning;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class RecommendedToolVersionTests
{
    [TestMethod]
    [DataRow(SetupDependencyKind.VapourSynth, "R79", true)]
    [DataRow(SetupDependencyKind.VapourSynth, "79", true)]
    [DataRow(SetupDependencyKind.VapourSynth, "VapourSynth R79", true)]
    [DataRow(SetupDependencyKind.VapourSynth, "R80", false)]
    [DataRow(SetupDependencyKind.VapourSynth, "R790", false)]
    [DataRow(SetupDependencyKind.Python312, "3.14.10 x64", true)]
    [DataRow(SetupDependencyKind.Python312, "3.12.10 x64", false)]
    [DataRow(SetupDependencyKind.FfmpegBundle, "ffmpeg version 9.0.2-full_build-www.gyan.dev", true)]
    [DataRow(SetupDependencyKind.FfmpegBundle, "ffmpeg version 9.0.2-rc1", false)]
    [DataRow(SetupDependencyKind.FfmpegBundle, "ffmpeg version 9.0.2-123-gabcdef", false)]
    [DataRow(SetupDependencyKind.FfmpegBundle, "ffmpeg version 10.0", false)]
    [DataRow(SetupDependencyKind.X264, "x264 0.165.3222 b35605a", true)]
    [DataRow(SetupDependencyKind.X265, "HEVC encoder version 4.3", true)]
    [DataRow(SetupDependencyKind.X265, "HEVC encoder version 4.30", false)]
    [DataRow(SetupDependencyKind.Av1an, "av1an 0.5.2-flowencode.2", true)]
    [DataRow(SetupDependencyKind.Av1an, "av1an 0.5.2", false)]
    [DataRow(SetupDependencyKind.Av1an, "av1an 0.5.2-flowencode.20", false)]
    [DataRow(SetupDependencyKind.VsPluginBundle, "2026-11-12", true)]
    [DataRow(SetupDependencyKind.VsPluginBundle, "9/9", false)]
    [DataRow(SetupDependencyKind.Awsmfunc, "1.3.5", true)]
    [DataRow(SetupDependencyKind.Vsrepo, "2.0.0", true)]
    [DataRow(SetupDependencyKind.Vsjetpack, "2.2.2", true)]
    [DataRow(SetupDependencyKind.SvtAv1, "SVT-AV1 Encoder Lib v4.3.0", true)]
    [DataRow(SetupDependencyKind.X265, "version string unavailable", false)]
    public void DetectedVersion_RequiresExactRecommendedRelease(SetupDependencyKind kind, string actual, bool expected)
        => Assert.AreEqual(expected, ToolchainBaseline.IsRecommendedVersion(kind, actual));

    [TestMethod]
    public void MatchingVersion_IsStillRepairableWhenReadinessFails()
    {
        var status = new SetupDependencyStatus(SetupDependencyKind.VapourSynth, ReadinessState.Ready,
            "R79", "R79", false, "vspipe.exe", "", true, true, "");
        Assert.IsTrue(ToolchainBaseline.IsRecommendedInstallation(status));
        Assert.IsFalse(ToolchainBaseline.IsRecommendedInstallation(status with { State = ReadinessState.Misconfigured }));
        Assert.IsFalse(ToolchainBaseline.IsRecommendedInstallation(status with { State = ReadinessState.Partial }));
        Assert.IsFalse(ToolchainBaseline.IsRecommendedInstallation(status with { InstalledVersion = "R80" }));
    }

    [TestMethod]
    public void PluginBundle_RequiresEveryPinnedPackageIncludingDependencies()
    {
        using var stream = typeof(SetupBootstrapService).Assembly.GetManifestResourceStream("FlowEncode.Infrastructure.Resources.VsrepoBaseline.json")!;
        using var document = JsonDocument.Parse(stream);
        var packages = document.RootElement.GetProperty("packages").EnumerateArray()
            .ToDictionary(p => p.GetProperty("identifier").GetString()!, p => p.GetProperty("releases")[0].GetProperty("version").GetString()!, StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(SetupBootstrapService.MatchesVsrepoBaselineVersions(packages));
        var key = packages.Keys.First();
        var expected = packages[key];
        packages[key] = "unknown";
        Assert.IsFalse(SetupBootstrapService.MatchesVsrepoBaselineVersions(packages));
        packages[key] = expected;
        packages.Remove(packages.Keys.Last());
        Assert.IsFalse(SetupBootstrapService.MatchesVsrepoBaselineVersions(packages));
    }

    [TestMethod]
    public void PluginBundle_ParsesFullVsrepoTableWithLongNamesAndVersions()
    {
        using var stream = typeof(SetupBootstrapService).Assembly.GetManifestResourceStream("FlowEncode.Infrastructure.Resources.VsrepoBaseline.json")!;
        using var document = JsonDocument.Parse(stream);
        var packages = document.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        var output = new List<string> { "Name                      Namespace       Installed   Latest      Identifier" };
        foreach (var package in packages)
        {
            var name = package.GetProperty("name").GetString()!;
            var packageNamespace = package.TryGetProperty("namespace", out var ns)
                ? ns.GetString()! : package.GetProperty("modulename").GetString()!;
            var version = package.GetProperty("releases")[0].GetProperty("version").GetString()!;
            output.Add($"{name,-25} {packageNamespace,-15} {version,-11} {version,-11} {package.GetProperty("identifier").GetString()}");
        }

        var parsed = SetupBootstrapService.ParseVsrepoInstalledPackages(string.Join(Environment.NewLine, output));
        Assert.AreEqual("r2", parsed["com.holywu.cas"].InstalledVersion);
        Assert.AreEqual("CAS - Contrast Adaptive Sharpening", parsed["cas"].Name);
        Assert.AreEqual("git:865c748", parsed["mvsfunc"].InstalledVersion);
        Assert.IsTrue(SetupBootstrapService.MatchesVsrepoBaselineVersions(
            parsed.ToDictionary(p => p.Key, p => p.Value.InstalledVersion, StringComparer.OrdinalIgnoreCase)));

        // A genuine dependency mismatch must still keep repair available.
        var mismatch = SetupBootstrapService.ParseVsrepoInstalledPackages(
            string.Join(Environment.NewLine, output).Replace("cas             r2", "cas             r1"));
        Assert.IsFalse(SetupBootstrapService.MatchesVsrepoBaselineVersions(
            mismatch.ToDictionary(p => p.Key, p => p.Value.InstalledVersion, StringComparer.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void VsrepoTable_PreservesUpgradeMarkersUnknownVersionsAndUnavailableReleases()
    {
        var output = string.Join(Environment.NewLine,
            $"{"*A plugin with a very long name",-25} {"plugin",-15} {"Unknown",-11} {"v123456789012345",-11} example.plugin",
            $"{"Unavailable plugin",-25} {"unavailable",-15} {"v123456789012345",-11} {"",-11} example.unavailable",
            "Not a package row");
        var parsed = SetupBootstrapService.ParseVsrepoInstalledPackages(output);
        Assert.AreEqual("A plugin with a very long name", parsed["example.plugin"].Name);
        Assert.AreEqual("Unknown", parsed["plugin"].InstalledVersion);
        Assert.AreEqual("v123456789012345", parsed["plugin"].LatestVersion);
        Assert.AreEqual("v123456789012345", parsed["unavailable"].InstalledVersion);
        Assert.AreEqual(string.Empty, parsed["unavailable"].LatestVersion);
        Assert.IsFalse(parsed.ContainsKey("row"));
    }
}
