using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LegacyAv1anCliFallbackRunnerTests
{
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
