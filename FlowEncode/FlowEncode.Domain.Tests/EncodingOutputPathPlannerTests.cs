using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class EncodingOutputPathPlannerTests
{
    [TestMethod]
    public void BuildDefaultOutputPath_WhenCrfProfile_AppendsEncoderAndQuality()
    {
        var profile = CreateProfile(EncoderKind.X265, RateControlMode.Crf, quality: 18.5, outputContainer: ".hevc");

        var outputPath = EncodingOutputPathPlanner.BuildDefaultOutputPath(
            @"D:\source\movie.mkv",
            @"D:\out",
            profile);

        Assert.AreEqual(@"D:\out\movie-x265-crf18.5.hevc", outputPath);
    }

    [TestMethod]
    public void BuildDefaultOutputPath_WhenRateControlIsBitrate_UsesSourceNameOnly()
    {
        var profile = CreateProfile(EncoderKind.X264, RateControlMode.Abr, bitrate: 4500, outputContainer: "264");

        var outputPath = EncodingOutputPathPlanner.BuildDefaultOutputPath(
            @"D:\source\movie.mkv",
            @"D:\out",
            profile);

        Assert.AreEqual(@"D:\out\movie.264", outputPath);
    }

    [TestMethod]
    public void BuildDefaultOutputPath_WhenProfileIsNull_UsesFallbackExtension()
    {
        var outputPath = EncodingOutputPathPlanner.BuildDefaultOutputPath(
            @"D:\source\movie.mkv",
            @"D:\out",
            null);

        Assert.AreEqual(@"D:\out\movie.264", outputPath);
    }

    [TestMethod]
    public void BuildDefaultOutputBaseFileName_WhenSourceHasNoName_UsesFallbackName()
    {
        var fileName = EncodingOutputPathPlanner.BuildDefaultOutputBaseFileName(
            @"D:\",
            CreateProfile(EncoderKind.SvtAv1, RateControlMode.Crf, quality: 20));

        Assert.AreEqual("encode-svtav1-crf20", fileName);
    }

    [TestMethod]
    public void NormalizeOutputExtension_WhenValueIsBlank_UsesFallbackExtension()
    {
        Assert.AreEqual("264", EncodingOutputPathPlanner.NormalizeOutputExtension("   "));
    }

    private static EncodingProfile CreateProfile(
        EncoderKind kind,
        RateControlMode rateControl,
        double quality = 18,
        int? bitrate = null,
        string outputContainer = "mkv")
    {
        return new EncodingProfile(
            kind,
            "Test",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            rateControl,
            quality,
            bitrate,
            outputContainer,
            string.Empty,
            string.Empty);
    }
}
