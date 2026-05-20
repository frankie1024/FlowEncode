using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class VideoEncodingOutputBridgeTests
{
    [TestMethod]
    public void ParseEncodingLine_WithAnsiRichSvtTicker_ProducesTransientLineAndSnapshot()
    {
        const string line = "Encoding: \u001b[33m 114/5400 Frames\u001b[0m @ \u001b[32m170.28\u001b[0m fps | \u001b[35m1108.11 kb/s\u001b[0m | Size: \u001b[31m1.19 MB\u001b[0m \u001b[38;5;248m[56.37 MB]\u001b[0m | Time: \u001b[36m0:00:01\u001b[0m \u001b[38;5;248m[-0:00:31]\u001b[0m";

        var parsed = VideoEncodingOutputBridge.ParseEncodingLine(
            EncoderKind.SvtAv1,
            totalFrames: 5400,
            sourceFramesPerSecond: 24000d / 1001d,
            line);

        Assert.IsNotNull(parsed);
        Assert.IsTrue(parsed.IsTransient);
        Assert.IsFalse(parsed.ShouldShowInLog);
        Assert.IsFalse(parsed.ShouldSurfaceDuringSourcePreparation);
        Assert.AreEqual("Encoding:  114/5400 Frames @ 170.28 fps | 1108.11 kb/s | Size: 1.19 MB [56.37 MB] | Time: 0:00:01 [-0:00:31]", parsed.NormalizedLine);
        Assert.IsNotNull(parsed.ParseResult);
        Assert.AreEqual(114L, parsed.ParseResult!.Snapshot!.CurrentFrame);
    }

    [TestMethod]
    public void ParseEncodingLine_WithUnparsedLine_ProducesMeaningfulVisibleOutput()
    {
        const string line = "x265 [info]: using cpu capabilities: MMX2 SSE2Fast LZCNT SSSE3 SSE4.2 AVX2";

        var parsed = VideoEncodingOutputBridge.ParseEncodingLine(
            EncoderKind.X265,
            totalFrames: 5400,
            sourceFramesPerSecond: 24000d / 1001d,
            line);

        Assert.IsNotNull(parsed);
        Assert.IsFalse(parsed.IsTransient);
        Assert.IsTrue(parsed.ShouldShowInLog);
        Assert.IsFalse(parsed.ShouldSurfaceDuringSourcePreparation);
        Assert.IsNull(parsed.ParseResult);
    }

    [TestMethod]
    public void ParseSourcePreparationLine_WithTraceback_EmitsVisibleSourceLine()
    {
        const string line = "Traceback (most recent call last):";

        var parsed = VideoEncodingOutputBridge.ParseSourcePreparationLine(line);

        Assert.IsNotNull(parsed);
        Assert.AreEqual("[source] Traceback (most recent call last):", parsed.DisplayLine);
        Assert.IsTrue(parsed.ShouldShowInLog);
        Assert.IsNull(parsed.ProgressPercent);
    }

    [TestMethod]
    public void ParseSourcePreparationLine_WithIndexProgressTick_EmitsHiddenProgressTick()
    {
        const string line = "Creating lwi index file 42%";

        var parsed = VideoEncodingOutputBridge.ParseSourcePreparationLine(line);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(42, parsed.ProgressPercent);
        Assert.IsFalse(parsed.ShouldShowInLog);
    }
}
