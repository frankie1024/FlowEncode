using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class SvtAv1ProfileConstraintsTests
{
    [TestMethod]
    public void HasTwoPassOverlayConflict_WhenQuotedSvtParamsEnableOverlay_ReturnsTrue()
    {
        var profile = CreateSvtTwoPassProfile("--svtav1-params \"enable-overlays=1:lookahead=120\"");

        Assert.IsTrue(SvtAv1ProfileConstraints.HasTwoPassOverlayConflict(profile));
    }

    [TestMethod]
    public void HasTwoPassOverlayConflict_WhenLaterQuotedSvtParamsDisableOverlay_ReturnsFalse()
    {
        var profile = CreateSvtTwoPassProfile("--enable-overlays 1 --svtav1-params 'enable-overlays=0'");

        Assert.IsFalse(SvtAv1ProfileConstraints.HasTwoPassOverlayConflict(profile));
    }

    private static EncodingProfile CreateSvtTwoPassProfile(string additionalArguments)
    {
        return new EncodingProfile(
            EncoderKind.SvtAv1,
            "SVT-AV1 Test",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            RateControlMode.TwoPass,
            28,
            null,
            ".ivf",
            additionalArguments,
            string.Empty);
    }
}
