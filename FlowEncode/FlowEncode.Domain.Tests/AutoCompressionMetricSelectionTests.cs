using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class AutoCompressionMetricSelectionTests
{
    [TestMethod]
    public void ResolvePreferredMetric_WhenCurrentMetricIsSupported_KeepsCurrentMetric()
    {
        var resolved = AutoCompressionMetricSelection.ResolvePreferredMetric(
            AutoCompressionMetric.Xpsnr,
            [AutoCompressionMetric.Vmaf, AutoCompressionMetric.Xpsnr]);

        Assert.AreEqual(AutoCompressionMetric.Xpsnr, resolved);
    }

    [TestMethod]
    public void ResolvePreferredMetric_WhenCurrentMetricIsUnsupported_FallsBackToFirstSupportedMetric()
    {
        var resolved = AutoCompressionMetricSelection.ResolvePreferredMetric(
            AutoCompressionMetric.Vmaf,
            [AutoCompressionMetric.ButteraugliInf, AutoCompressionMetric.Xpsnr]);

        Assert.AreEqual(AutoCompressionMetric.ButteraugliInf, resolved);
    }

    [TestMethod]
    public void ResolvePreferredMetric_WhenSupportedMetricListIsEmpty_KeepsCurrentMetric()
    {
        var resolved = AutoCompressionMetricSelection.ResolvePreferredMetric(
            AutoCompressionMetric.Ssimulacra2,
            []);

        Assert.AreEqual(AutoCompressionMetric.Ssimulacra2, resolved);
    }

    [TestMethod]
    public void BuildOutputPath_WithResolvedFallbackMetric_UsesFallbackMetricToken()
    {
        var resolved = AutoCompressionMetricSelection.ResolvePreferredMetric(
            AutoCompressionMetric.Vmaf,
            [AutoCompressionMetric.XpsnrWeighted]);
        var outputPath = AutoCompressionOutputPathPlanner.BuildOutputPath(
            @"D:\input\movie.mkv",
            @"D:\output",
            EncoderKind.X265,
            resolved,
            72.5);

        Assert.AreEqual(@"D:\output\movie.x265.xpsnrw72p5.mkv", outputPath);
    }

    [TestMethod]
    public void BuildArgumentParts_WithResolvedFallbackMetric_UsesFallbackMetricInCommandArguments()
    {
        var resolved = AutoCompressionMetricSelection.ResolvePreferredMetric(
            AutoCompressionMetric.Vmaf,
            [AutoCompressionMetric.ButteraugliInf]);
        var request = new AutoCompressionRequest(
            Guid.NewGuid(),
            @"D:\input\movie.mkv",
            @"D:\output\movie.mkv",
            EncoderKind.X265,
            resolved,
            3.5,
            4,
            string.Empty,
            string.Empty,
            null);

        var arguments = LegacyAv1anCliFallbackRunner.BuildArgumentParts(
            request,
            @"D:\temp",
            AppLanguage.English,
            useStructuredProgress: true);
        var targetMetricIndex = Array.IndexOf(arguments.ToArray(), "--target-metric");

        Assert.IsTrue(targetMetricIndex >= 0);
        Assert.AreEqual("butteraugli-inf", arguments[targetMetricIndex + 1]);
    }
}
