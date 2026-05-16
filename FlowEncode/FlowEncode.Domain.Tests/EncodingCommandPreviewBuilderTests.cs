using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class EncodingCommandPreviewBuilderTests
{
    [TestMethod]
    public void Build_WhenX264TwoPass_IncludesBothPassesAndStatsFile()
    {
        var profile = CreateProfile(
            EncoderKind.X264,
            RateControlMode.TwoPass,
            bitrate: 4500,
            outputContainer: "264");

        var preview = EncodingCommandPreviewBuilder.Build(profile);

        StringAssert.Contains(preview.Title, "x264");
        StringAssert.Contains(preview.CommandLine, "[Pass 1/2]");
        StringAssert.Contains(preview.CommandLine, "--pass 1");
        StringAssert.Contains(preview.CommandLine, "--stats \"{output}.x264_2pass.log\"");
        StringAssert.Contains(preview.CommandLine, "[Pass 2/2]");
        StringAssert.Contains(preview.CommandLine, "--pass 2");
        StringAssert.Contains(preview.CommandLine, "-o \"{output}.264\" -");
    }

    [TestMethod]
    public void Build_WhenX265HasUhdParameters_AppendsUhdParameters()
    {
        var profile = CreateProfile(
            EncoderKind.X265,
            RateControlMode.Crf,
            quality: 18.5,
            outputContainer: "hevc",
            uhdParameters: "--uhd-bd --repeat-headers");

        var preview = EncodingCommandPreviewBuilder.Build(profile);

        StringAssert.Contains(preview.CommandLine, "x265");
        StringAssert.Contains(preview.CommandLine, "--crf 18.5");
        StringAssert.Contains(preview.CommandLine, "--uhd-bd --repeat-headers");
        StringAssert.Contains(preview.CommandLine, "-o \"{output}.hevc\"");
    }

    [TestMethod]
    public void Build_WhenSvtAv1Crf_UsesSvtRateControl()
    {
        var profile = CreateProfile(
            EncoderKind.SvtAv1,
            RateControlMode.Crf,
            quality: 20.25,
            outputContainer: "ivf");

        var preview = EncodingCommandPreviewBuilder.Build(profile);

        StringAssert.Contains(preview.Title, "SVT-AV1");
        StringAssert.Contains(preview.CommandLine, "SvtAv1EncApp");
        StringAssert.Contains(preview.CommandLine, "--rc 0 --crf 20.25");
        StringAssert.Contains(preview.CommandLine, "-b \"{output}.ivf\"");
    }

    [TestMethod]
    public void Build_WhenEncoderKindIsUnknown_ReturnsEmptyCommand()
    {
        var profile = CreateProfile((EncoderKind)999, RateControlMode.Crf);

        var preview = EncodingCommandPreviewBuilder.Build(profile);

        Assert.AreEqual(profile.Name, preview.Title);
        Assert.AreEqual(string.Empty, preview.CommandLine);
        Assert.AreEqual(string.Empty, preview.Notes);
    }

    private static EncodingProfile CreateProfile(
        EncoderKind encoderKind,
        RateControlMode rateControl,
        double quality = 18,
        int? bitrate = null,
        string outputContainer = "mkv",
        string uhdParameters = "")
    {
        return new EncodingProfile(
            encoderKind,
            "Test Profile",
            string.Empty,
            "veryslow",
            string.Empty,
            string.Empty,
            rateControl,
            quality,
            bitrate,
            outputContainer,
            "--custom-flag",
            uhdParameters);
    }
}
