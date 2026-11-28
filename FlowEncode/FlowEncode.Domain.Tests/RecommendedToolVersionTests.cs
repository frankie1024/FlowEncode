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
    [DataRow(SetupDependencyKind.VapourSynth, "R82", true)]
    [DataRow(SetupDependencyKind.VapourSynth, "82", true)]
    [DataRow(SetupDependencyKind.VapourSynth, "VapourSynth R82", true)]
    [DataRow(SetupDependencyKind.VapourSynth, "R81", false)]
    [DataRow(SetupDependencyKind.VapourSynth, "R820", false)]
    [DataRow(SetupDependencyKind.Python312, "3.14.10 x64", true)]
    [DataRow(SetupDependencyKind.Python312, "3.12.10 x64", false)]
    [DataRow(SetupDependencyKind.FfmpegBundle, "ffmpeg version 9.0.2-full_build-www.gyan.dev", true)]
    [DataRow(SetupDependencyKind.FfmpegBundle, "ffmpeg version 9.0.2-rc1", false)]
    [DataRow(SetupDependencyKind.FfmpegBundle, "ffmpeg version 9.0.2-123-gabcdef", false)]
    [DataRow(SetupDependencyKind.FfmpegBundle, "ffmpeg version 10.0", false)]
    [DataRow(SetupDependencyKind.X264, "x264 0.165.3222 b35605a", true)]
    [DataRow(SetupDependencyKind.X265, "HEVC encoder version 4.3 (8bit+10bit)", true)]
    [DataRow(SetupDependencyKind.X265, "HEVC encoder version 4.3 (8bit+10bit+12bit)", true)]
    [DataRow(SetupDependencyKind.X265, "HEVC encoder version 4.3 (10bit+8bit)", true)]
    [DataRow(SetupDependencyKind.X265, "HEVC encoder version 4.3 (8bit)", false)]
    [DataRow(SetupDependencyKind.X265, "HEVC encoder version 4.3 (10bit)", false)]
    [DataRow(SetupDependencyKind.X265, "HEVC encoder version 4.3", false)]
    [DataRow(SetupDependencyKind.X265, "HEVC encoder version 4.3+10 (8bit)", false)]
    [DataRow(SetupDependencyKind.X265, "HEVC encoder version 4.30 (8bit+10bit)", false)]
    [DataRow(SetupDependencyKind.Av1an, "av1an 0.6.0-flowencode.1 (rev abc1234) (Release)", true)]
    [DataRow(SetupDependencyKind.Av1an, "av1an 0.6.0", false)]
    [DataRow(SetupDependencyKind.Av1an, "av1an 0.5.2-flowencode.2", false)]
    [DataRow(SetupDependencyKind.Av1an, "av1an 0.6.0-flowencode.10", false)]
    [DataRow(SetupDependencyKind.Av1an, "av1an 0.6.0-flowencode.1-unstable", false)]
    [DataRow(SetupDependencyKind.Awsmfunc, "1.3.5", true)]
    [DataRow(SetupDependencyKind.Vsjetpack, "2.2.2", false)]
    [DataRow(SetupDependencyKind.Vsjetpack, "3.0.0", false)]
    [DataRow(SetupDependencyKind.SvtAv1, "SVT-AV1 Encoder Lib v4.3.0", true)]
    [DataRow(SetupDependencyKind.X265, "version string unavailable", false)]
    public void DetectedVersion_RequiresExactRecommendedRelease(SetupDependencyKind kind, string actual, bool expected)
        => Assert.AreEqual(expected, ToolchainBaseline.IsRecommendedVersion(kind, actual));

    [TestMethod]
    public void MatchingVersion_IsStillRepairableWhenReadinessFails()
    {
        var status = new SetupDependencyStatus(SetupDependencyKind.VapourSynth, ReadinessState.Ready,
            "R82", "R82", false, "vspipe.exe", "", true, true, "");
        Assert.IsTrue(ToolchainBaseline.IsRecommendedInstallation(status));
        Assert.IsFalse(ToolchainBaseline.IsRecommendedInstallation(status with { State = ReadinessState.Misconfigured }));
        Assert.IsFalse(ToolchainBaseline.IsRecommendedInstallation(status with { State = ReadinessState.Partial }));
        Assert.IsFalse(ToolchainBaseline.IsRecommendedInstallation(status with { InstalledVersion = "R81" }));
    }

    [TestMethod]
    [DataRow(
        "x265 [info]: HEVC encoder version 4.3\r\nx265 [info]: build info [Windows][MSVC 1944][64 bit] 8bit+10bit",
        "HEVC encoder version 4.3 (8bit+10bit)")]
    [DataRow(
        "x265 [info]: HEVC encoder version 4.3\r\nx265 [info]: build info [Windows][MSVC 1944][64 bit] 8bit",
        "HEVC encoder version 4.3 (8bit)")]
    [DataRow(
        "x265 [info]: HEVC encoder version 4.3\r\nx265 [info]: build info [Windows][GCC 14.2.0][64 bit][noasm] 8bit+10bit+12bit",
        "HEVC encoder version 4.3 (8bit+10bit+12bit)")]
    [DataRow(
        "x265 [info]: HEVC encoder version 4.3\r\nx265 [info]: build info [Windows][MSVC 1944][64 bit] 10bit",
        "HEVC encoder version 4.3 (10bit)")]
    [DataRow(
        "x265 [info]: HEVC encoder version 4.3",
        "HEVC encoder version 4.3")]
    public void EncoderBinaryProbe_ParseVersion_PreservesX265BitDepth(string output, string expected)
        => Assert.AreEqual(expected, EncoderBinaryProbe.ParseVersion(output, EncoderKind.X265));

}
