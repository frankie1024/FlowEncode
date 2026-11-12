using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LegacyAv1anCliFallbackRunnerTests
{
    [TestMethod]
    [DataRow("natural-cubic-polynomial", "natural-cubicpolynomial")]
    [DataRow("fritschcarlson-pchip", "fritschcarlson-pchip")]
    public void InterpolationSelection_UsesUpstreamRoundPairSyntax(string selection, string expected)
    {
        var request = new AutoCompressionRequest(Guid.NewGuid(), @"D:\input.vpy", @"D:\output.mkv",
            EncoderKind.X264, AutoCompressionMetric.Vmaf, 95, 4, "", "", 2,
            new AutoCompressionSearchProfile(selection, null, null, 1, false));
        var arguments = LegacyAv1anCliFallbackRunner.BuildArgumentParts(request, @"D:\temp", AppLanguage.English, true).ToList();
        Assert.AreEqual(expected, arguments[arguments.IndexOf("--interp-method") + 1]);
    }

    [TestMethod]
    public void GetWorkingDirectory_WhenSourceHasDirectory_UsesSourceDirectory()
    {
        var request = new AutoCompressionRequest(
            Guid.NewGuid(),
            @"D:\YP\mercy\CRFtest.vpy",
            @"D:\YP\mercy\CRFtest.av1.vmaf95.mkv",
            EncoderKind.SvtAv1,
            AutoCompressionMetric.Vmaf,
            95,
            4,
            string.Empty,
            string.Empty,
            null);

        var workingDirectory = LegacyAv1anCliFallbackRunner.GetWorkingDirectory(request, @"E:\cmct_encode\tools\av1an.exe");

        Assert.AreEqual(@"D:\YP\mercy", workingDirectory);
    }
}
