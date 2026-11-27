using FlowEncode.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class PreviewCompareStateTests
{
    private static VapourSynthPreviewOutputInfo CreateOutput(
        int index,
        int width = 1920,
        int height = 1080,
        int totalFrames = 1000,
        int fpsNum = 24000,
        int fpsDen = 1001) =>
        new(index, $"Output {index}", width, height, totalFrames, fpsNum, fpsDen, "YUV420P8", 8);

    [TestMethod]
    public void NormalizeMode_WhenFewerThanTwoOutputs_ReturnsSingle()
    {
        Assert.AreEqual(PreviewCompareMode.Single, PreviewCompareCoordinator.NormalizeMode(PreviewCompareMode.Wipe, 0));
        Assert.AreEqual(PreviewCompareMode.Single, PreviewCompareCoordinator.NormalizeMode(PreviewCompareMode.SideBySide, 1));
        Assert.AreEqual(PreviewCompareMode.Difference, PreviewCompareCoordinator.NormalizeMode(PreviewCompareMode.Difference, 2));
    }

    [TestMethod]
    public void CycleMode_CyclesForwardAndBackwardWhenMultipleOutputsExist()
    {
        Assert.AreEqual(PreviewCompareMode.Wipe, PreviewCompareCoordinator.CycleMode(PreviewCompareMode.Single, 2));
        Assert.AreEqual(PreviewCompareMode.SideBySide, PreviewCompareCoordinator.CycleMode(PreviewCompareMode.Wipe, 2));
        Assert.AreEqual(PreviewCompareMode.Difference, PreviewCompareCoordinator.CycleMode(PreviewCompareMode.SideBySide, 2));
        Assert.AreEqual(PreviewCompareMode.Flicker, PreviewCompareCoordinator.CycleMode(PreviewCompareMode.Difference, 2));
        Assert.AreEqual(PreviewCompareMode.Single, PreviewCompareCoordinator.CycleMode(PreviewCompareMode.Flicker, 2));
        Assert.AreEqual(PreviewCompareMode.Flicker, PreviewCompareCoordinator.CycleMode(PreviewCompareMode.Single, 2, reverse: true));
        Assert.AreEqual(PreviewCompareMode.Single, PreviewCompareCoordinator.CycleMode(PreviewCompareMode.Single, 1));
    }

    [TestMethod]
    public void ResolveSecondaryOutputIndex_PrefersDistinctExistingSecondaryOrFallsBackToFirstDistinct()
    {
        var outputs = new[]
        {
            CreateOutput(0),
            CreateOutput(1),
            CreateOutput(2)
        };

        Assert.AreEqual(2, PreviewCompareCoordinator.ResolveSecondaryOutputIndex(outputs, primaryOutputIndex: 0, preferredSecondaryOutputIndex: 2));
        Assert.AreEqual(1, PreviewCompareCoordinator.ResolveSecondaryOutputIndex(outputs, primaryOutputIndex: 0, preferredSecondaryOutputIndex: 0));
        Assert.AreEqual(0, PreviewCompareCoordinator.ResolveSecondaryOutputIndex(outputs, primaryOutputIndex: 1, preferredSecondaryOutputIndex: 9));
        Assert.IsNull(PreviewCompareCoordinator.ResolveSecondaryOutputIndex([CreateOutput(0)], primaryOutputIndex: 0, preferredSecondaryOutputIndex: 1));
    }

    [TestMethod]
    public void SwapOutputs_SwapsDistinctIndices()
    {
        var swapped = PreviewCompareCoordinator.SwapOutputs(0, 2);
        Assert.AreEqual((2, (int?)0), swapped);

        var unchanged = PreviewCompareCoordinator.SwapOutputs(0, null);
        Assert.AreEqual((0, (int?)null), unchanged);
    }

    [TestMethod]
    public void ResolveSecondaryFrameNumber_SyncsByFrameOrTimestampAndClamps()
    {
        var output24 = CreateOutput(0, totalFrames: 240, fpsNum: 24, fpsDen: 1);
        var output60 = CreateOutput(1, totalFrames: 600, fpsNum: 60, fpsDen: 1);
        var shortOutput = CreateOutput(2, totalFrames: 50, fpsNum: 24, fpsDen: 1);

        Assert.AreEqual(
            24,
            PreviewCompareCoordinator.ResolveSecondaryFrameNumber(output24, output60, 24, "frame", isTimelineTimeMode: false));

        Assert.AreEqual(
            60,
            PreviewCompareCoordinator.ResolveSecondaryFrameNumber(output24, output60, 24, "time", isTimelineTimeMode: false));

        Assert.AreEqual(
            60,
            PreviewCompareCoordinator.ResolveSecondaryFrameNumber(output24, output60, 24, "timeline", isTimelineTimeMode: true));

        Assert.AreEqual(
            24,
            PreviewCompareCoordinator.ResolveSecondaryFrameNumber(output24, output60, 24, "timeline", isTimelineTimeMode: false));

        Assert.AreEqual(
            49,
            PreviewCompareCoordinator.ResolveSecondaryFrameNumber(output24, shortOutput, 120, "frame", isTimelineTimeMode: false));
    }

    [TestMethod]
    public void WipeCoordinateMath_ConvertsBetweenViewportAndNormalizedSplitRatio()
    {
        // Image is centered at left=100 in viewport, width=800, not scrolled.
        var ratio = PreviewCompareCoordinator.ComputeSplitRatioFromViewportX(
            pointerViewportX: 300,
            imageLeftInViewport: 100,
            horizontalOffset: 0,
            imageWidth: 800);
        Assert.AreEqual(0.25, ratio, 0.0001);

        // Zoomed image (width=2000, left=0), scrolled right by 400px, pointer at viewport X=500 -> image X=900 -> 0.45
        var zoomedRatio = PreviewCompareCoordinator.ComputeSplitRatioFromViewportX(
            pointerViewportX: 500,
            imageLeftInViewport: 0,
            horizontalOffset: 400,
            imageWidth: 2000);
        Assert.AreEqual(0.45, zoomedRatio, 0.0001);

        var dividerX = PreviewCompareCoordinator.ComputeViewportDividerX(
            splitRatio: zoomedRatio,
            imageLeftInViewport: 0,
            horizontalOffset: 400,
            imageWidth: 2000);
        Assert.AreEqual(500, dividerX, 0.0001);

        // Centering in 1000px viewport while scrolled 400px -> image X = 900 -> 0.45
        var centered = PreviewCompareCoordinator.ComputeCenteredViewportSplitRatio(
            viewportWidth: 1000,
            imageLeftInViewport: 0,
            horizontalOffset: 400,
            imageWidth: 2000);
        Assert.AreEqual(0.45, centered, 0.0001);
    }

    [TestMethod]
    public void ComputeEffectiveViewportWidth_HalvesAvailableWidthForSideBySide()
    {
        Assert.AreEqual(1200, PreviewCompareCoordinator.ComputeEffectiveViewportWidth(1200, PreviewCompareMode.Single), 0.0001);
        Assert.AreEqual(1200, PreviewCompareCoordinator.ComputeEffectiveViewportWidth(1200, PreviewCompareMode.Wipe), 0.0001);
        Assert.AreEqual(596, PreviewCompareCoordinator.ComputeEffectiveViewportWidth(1200, PreviewCompareMode.SideBySide, splitterGapWidth: 8), 0.0001);
    }

    [TestMethod]
    public void RecommendSizeAlignment_DistinguishesCropFromRescale()
    {
        // Symmetric vertical crop (e.g. top=42, bottom=42)
        Assert.AreEqual(PreviewCompareSizeAlignment.Center1To1, PreviewCompareCoordinator.RecommendSizeAlignment(1920, 1080, 1920, 996));
        // Small crop on all edges
        Assert.AreEqual(PreviewCompareSizeAlignment.Center1To1, PreviewCompareCoordinator.RecommendSizeAlignment(1920, 1080, 1916, 996));
        // Horizontal pillarbox crop
        Assert.AreEqual(PreviewCompareSizeAlignment.Center1To1, PreviewCompareCoordinator.RecommendSizeAlignment(1920, 1080, 1440, 1080));
        // 1080p vs 720p downscale
        Assert.AreEqual(PreviewCompareSizeAlignment.UniformFit, PreviewCompareCoordinator.RecommendSizeAlignment(1920, 1080, 1280, 720));
        // 1080p letterboxed vs cropped 720p
        Assert.AreEqual(PreviewCompareSizeAlignment.UniformFit, PreviewCompareCoordinator.RecommendSizeAlignment(1920, 1080, 1280, 536));
    }

    [TestMethod]
    public void ComputeAlignedCanvasLayout_CentersCroppedClipWithoutStretching()
    {
        var layout = PreviewCompareCoordinator.ComputeAlignedCanvasLayout(
            1920,
            1080,
            1920,
            996,
            PreviewCompareSizeAlignment.Center1To1);

        Assert.AreEqual(1920, layout.CanvasWidth);
        Assert.AreEqual(1080, layout.CanvasHeight);
        Assert.AreEqual(new PreviewCompareAlignedRect(0, 0, 1920, 1080), layout.RectA);
        Assert.AreEqual(new PreviewCompareAlignedRect(0, 42, 1920, 996), layout.RectB);

        // Swapped A/B keeps identical canvas and pixel positions
        var swapped = PreviewCompareCoordinator.ComputeAlignedCanvasLayout(
            1920,
            996,
            1920,
            1080,
            PreviewCompareSizeAlignment.Center1To1);

        Assert.AreEqual(1920, swapped.CanvasWidth);
        Assert.AreEqual(1080, swapped.CanvasHeight);
        Assert.AreEqual(new PreviewCompareAlignedRect(0, 42, 1920, 996), swapped.RectA);
        Assert.AreEqual(new PreviewCompareAlignedRect(0, 0, 1920, 1080), swapped.RectB);
    }

    [TestMethod]
    public void ComputeAlignedCanvasLayout_UniformFitPreservesAspectRatio()
    {
        var layout = PreviewCompareCoordinator.ComputeAlignedCanvasLayout(
            1920,
            1080,
            1280,
            536,
            PreviewCompareSizeAlignment.UniformFit);

        Assert.AreEqual(1920, layout.CanvasWidth);
        Assert.AreEqual(1080, layout.CanvasHeight);
        Assert.AreEqual(0, layout.RectB.X, 0.001);
        Assert.AreEqual(138, layout.RectB.Y, 0.001);
        Assert.AreEqual(1920, layout.RectB.Width, 0.001);
        Assert.AreEqual(804, layout.RectB.Height, 0.001);
    }

    [TestMethod]
    public void ResolveSecondaryFrameNumber_AppliesFrameOffsetAndClamps()
    {
        var outputA = CreateOutput(0, totalFrames: 240, fpsNum: 24, fpsDen: 1);
        var outputB = CreateOutput(1, totalFrames: 240, fpsNum: 24, fpsDen: 1);

        Assert.AreEqual(
            36,
            PreviewCompareCoordinator.ResolveSecondaryFrameNumber(outputA, outputB, 24, "frame", isTimelineTimeMode: false, secondaryFrameOffset: 12));
        Assert.AreEqual(
            0,
            PreviewCompareCoordinator.ResolveSecondaryFrameNumber(outputA, outputB, 5, "frame", isTimelineTimeMode: false, secondaryFrameOffset: -20));
        Assert.AreEqual(
            239,
            PreviewCompareCoordinator.ResolveSecondaryFrameNumber(outputA, outputB, 230, "frame", isTimelineTimeMode: false, secondaryFrameOffset: 50));
    }

    [TestMethod]
    public void FormatAlignmentScriptSnippet_GeneratesTrimPadAndSubpixelLines()
    {
        Assert.AreEqual(
            "clip_b = clip_b[12:]",
            PreviewCompareCoordinator.FormatAlignmentScriptSnippet(12));
        Assert.AreEqual(
            "clip_b = clip_b[0] * 3 + clip_b",
            PreviewCompareCoordinator.FormatAlignmentScriptSnippet(-3));
        var combined = PreviewCompareCoordinator.FormatAlignmentScriptSnippet(5, subpixelOffsetX: 0.25, subpixelOffsetY: -0.5);
        StringAssert.Contains(combined, "clip_b = clip_b[5:]");
        StringAssert.Contains(combined, "src_left=0.25, src_top=-0.5");
    }

    [TestMethod]
    public void PreviewRenderOptions_DetectsHdrTransferProperties()
    {
        Assert.IsTrue(PreviewRenderOptions.IsHdrTransferProperties([new VapourSynthPreviewFrameProperty("_Transfer", "16")]));
        Assert.IsTrue(PreviewRenderOptions.IsHdrTransferProperties([new VapourSynthPreviewFrameProperty("_Transfer", "18")]));
        Assert.IsFalse(PreviewRenderOptions.IsHdrTransferProperties([new VapourSynthPreviewFrameProperty("_Transfer", "1")]));
        Assert.IsFalse(PreviewRenderOptions.IsHdrTransferProperties([]));
    }

    [TestMethod]
    public void PreviewCompareScriptBuilder_BuildsTwoSourceCompareScript()
    {
        var script = PreviewCompareScriptBuilder.BuildTwoSourceCompareScript(
            @"C:\videos\source.mkv",
            @"C:\videos\encode.mkv",
            "source.mkv",
            "encode.mkv");

        StringAssert.Contains(script, "clip_a.set_output(0)");
        StringAssert.Contains(script, "clip_b.set_output(1)");
        StringAssert.Contains(script, "LWLibavSource");
        StringAssert.Contains(script, "ffms2");
        StringAssert.Contains(script, @"C:\\videos\\source.mkv");
        StringAssert.Contains(script, @"C:\\videos\\encode.mkv");
    }
}
