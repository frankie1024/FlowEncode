using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class TemplateEditingBaselineTests
{
    [TestMethod]
    public void Matches_WhenNotesOnlyDifferByWinUiLineEndings_ReturnsTrue()
    {
        var profile = CreateProfile(description: "第一行\r\n第二行");
        var baseline = TemplateEditingBaseline.Capture("x264", "第一行\r\n第二行", profile);

        Assert.IsTrue(baseline.Matches("x264", "第一行\r第二行", profile with { Description = "第一行\r第二行" }));
        Assert.IsTrue(baseline.Matches("  x264  ", "  第一行\n第二行  ", profile with { Description = "第一行\n第二行" }));
    }

    [TestMethod]
    public void Matches_WhenArgumentsOnlyDifferByLineEndings_ReturnsTrue()
    {
        var profile = CreateProfile(
            additionalArguments: "--level 4.1\r\n--ref 4",
            uhdParameters: "--hdr10\r\n--repeat-headers");
        var baseline = TemplateEditingBaseline.Capture("x265", string.Empty, profile);

        var winUiRoundTrippedProfile = profile with
        {
            AdditionalArguments = "--level 4.1\r--ref 4",
            UhdParameters = "--hdr10\r--repeat-headers"
        };

        Assert.IsTrue(baseline.Matches("x265", string.Empty, winUiRoundTrippedProfile));
    }

    [TestMethod]
    public void Matches_WhenNotesOrProfileContentChanges_ReturnsFalse()
    {
        var profile = CreateProfile();
        var baseline = TemplateEditingBaseline.Capture("x264", "第一行\r\n第二行", profile);

        Assert.IsFalse(baseline.Matches("x264-edited", "第一行\r第二行", profile));
        Assert.IsFalse(baseline.Matches("x264", "第一行\r已修改", profile));
        Assert.IsFalse(baseline.Matches("x264", "第一行\r第二行", profile with { Quality = 19.0 }));
        Assert.IsFalse(baseline.Matches("x264", "第一行\r第二行", null));
    }

    private static EncodingProfile CreateProfile(
        string description = "",
        string additionalArguments = "--level 4.1",
        string uhdParameters = "")
    {
        return new EncodingProfile(
            EncoderKind.X264,
            "x264",
            description,
            "veryslow",
            string.Empty,
            "High",
            RateControlMode.Crf,
            18.0,
            null,
            "264",
            additionalArguments,
            uhdParameters);
    }
}
