using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LocalEncodingJobRunnerProgressParsingTests
{
    [TestMethod]
    public void ParseSnapshot_WithOfficialSvtAnsiTicker_ParsesProgressMetrics()
    {
        const string line = "Encoding: \u001b[33m 114/5400 Frames\u001b[0m @ \u001b[32m170.28\u001b[0m fps | \u001b[35m1108.11 kb/s\u001b[0m | Size: \u001b[31m1.19 MB\u001b[0m \u001b[38;5;248m[56.37 MB]\u001b[0m | Time: \u001b[36m0:00:01\u001b[0m \u001b[38;5;248m[-0:00:31]\u001b[0m";

        var parsed = EncodingProgressParser.ParseSnapshot(
            EncoderKind.SvtAv1,
            totalFrames: 5400,
            sourceFramesPerSecond: 24000d / 1001d,
            line);

        Assert.IsNotNull(parsed);
        Assert.IsNotNull(parsed.Snapshot);
        var snapshot = parsed.Snapshot!;
        Assert.AreEqual(114L, snapshot.CurrentFrame);
        Assert.AreEqual(5400L, snapshot.TotalFrames);
        Assert.AreEqual(170.28, snapshot.FramesPerSecond!.Value, 0.001);
        Assert.AreEqual(1108.11, snapshot.BitrateKbps!.Value, 0.001);
        Assert.AreEqual(TimeSpan.FromSeconds(31), snapshot.Eta);
        Assert.IsTrue(snapshot.EstimatedFileSizeBytes > 0);
        Assert.AreEqual(114d / 5400d, parsed.ProgressFraction!.Value, 0.000001);
    }

    [TestMethod]
    public void ParseSnapshot_WithBracketedX265TickerWithoutLeadingInnerSpace_ParsesProgressMetrics()
    {
        const string line = "[45.2%] 2440/5400 Frames @ 46.49 FPS | 13974.00 kb/s | 0:00:31 | 220.4 MB [220.4 MB]";

        var parsed = EncodingProgressParser.ParseSnapshot(
            EncoderKind.X265,
            totalFrames: 5400,
            sourceFramesPerSecond: 24000d / 1001d,
            line);

        Assert.IsNotNull(parsed);
        Assert.IsNotNull(parsed.Snapshot);
        var snapshot = parsed.Snapshot!;
        Assert.AreEqual(2440L, snapshot.CurrentFrame);
        Assert.AreEqual(5400L, snapshot.TotalFrames);
        Assert.AreEqual(46.49, snapshot.FramesPerSecond!.Value, 0.001);
        Assert.AreEqual(13974.00, snapshot.BitrateKbps!.Value, 0.001);
        Assert.AreEqual(TimeSpan.FromSeconds(31), snapshot.Eta);
        Assert.IsTrue(snapshot.EstimatedFileSizeBytes > 0);
        Assert.AreEqual(0.452, parsed.ProgressFraction!.Value, 0.000001);
    }

    [TestMethod]
    public void ParseSourcePreparationProgressPercent_WithLwiIndexLine_ParsesPercent()
    {
        const string line = "Creating lwi index file 42%";

        var parsed = EncodingProgressParser.ParseSourcePreparationProgressPercent(line);

        Assert.AreEqual(42, parsed);
    }

    [TestMethod]
    public void ParseSourcePreparationProgressPercent_WithBestSourceIndexLine_ParsesPercent()
    {
        const string line = "Information: VideoSource track #0 index progress 54%";

        var parsed = EncodingProgressParser.ParseSourcePreparationProgressPercent(line);

        Assert.AreEqual(54, parsed);
    }

    [TestMethod]
    public void ParseSourcePreparationProgressPercent_WithUnrelatedLine_ReturnsNull()
    {
        const string line = "Script evaluation finished.";

        var parsed = EncodingProgressParser.ParseSourcePreparationProgressPercent(line);

        Assert.IsNull(parsed);
    }

    [TestMethod]
    public void ResolveReportedProgressFractionForTesting_WithNullCurrentAndKnownLast_ReturnsLast()
    {
        var resolved = LocalEncodingJobRunner.ResolveReportedProgressFractionForTesting(
            currentProgressFraction: null,
            lastProgressFraction: 0.452);

        Assert.AreEqual(0.452, resolved);
    }

    [TestMethod]
    public void ResolveReportedProgressFractionForTesting_WithKnownCurrent_PrefersCurrent()
    {
        var resolved = LocalEncodingJobRunner.ResolveReportedProgressFractionForTesting(
            currentProgressFraction: 0.31,
            lastProgressFraction: 0.452);

        Assert.AreEqual(0.31, resolved);
    }

    [TestMethod]
    public void ShouldSurfaceLineDuringSourcePreparationForTesting_WithNeutralLine_ReturnsFalse()
    {
        const string line = "x265 [info]: HEVC encoder version 4.1";

        var result = LocalEncodingJobRunner.ShouldSurfaceLineDuringSourcePreparationForTesting(line);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ShouldSurfaceLineDuringSourcePreparationForTesting_WithFailureLine_ReturnsTrue()
    {
        const string line = "x265 [error]: failed to open output file";

        var result = LocalEncodingJobRunner.ShouldSurfaceLineDuringSourcePreparationForTesting(line);

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void ShouldSurfaceLineDuringSourcePreparationForTesting_WithTracebackLine_ReturnsTrue()
    {
        const string line = "Traceback (most recent call last):";

        var result = LocalEncodingJobRunner.ShouldSurfaceLineDuringSourcePreparationForTesting(line);

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void ShouldAppendSourcePreparationVisibleLogLineForTesting_WithLwiProgressTick_ReturnsFalse()
    {
        const string line = "Creating lwi index file 42%";

        var result = LocalEncodingJobRunner.ShouldAppendSourcePreparationVisibleLogLineForTesting(line);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ShouldAppendSourcePreparationVisibleLogLineForTesting_WithBestSourceProgressTick_ReturnsFalse()
    {
        const string line = "Information: VideoSource track #0 index progress 54%";

        var result = LocalEncodingJobRunner.ShouldAppendSourcePreparationVisibleLogLineForTesting(line);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ShouldAppendSourcePreparationVisibleLogLineForTesting_WithFailureLine_ReturnsTrue()
    {
        const string line = "Script evaluation failed: source plugin error";

        var result = LocalEncodingJobRunner.ShouldAppendSourcePreparationVisibleLogLineForTesting(line);

        Assert.IsTrue(result);
    }

}
