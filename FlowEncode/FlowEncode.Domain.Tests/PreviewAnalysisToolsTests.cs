using FlowEncode.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class PreviewAnalysisToolsTests
{
    [TestMethod]
    public void BuildSamplingPlan_SkipsTenMinutesOnLongMovie()
    {
        // 120 min @ 24fps = 172800 frames
        var plan = PreviewWorstFramePlanner.BuildSamplingPlan(172800, 24.0, requestedSkipMinutes: 10.0, sampleCount: 80);
        Assert.IsFalse(plan.AdjustedForShortVideo);
        Assert.AreEqual(10.0, plan.AppliedSkipMinutes, 0.01);
        Assert.AreEqual(14400, plan.StartFrame);
        Assert.AreEqual(172799 - 14400, plan.EndFrame);
        Assert.AreEqual(80, plan.SampleFrames.Count);
        Assert.IsTrue(plan.SampleFrames[0] >= 14400);
        Assert.IsTrue(plan.SampleFrames[^1] <= 172799 - 14400);
    }

    [TestMethod]
    public void BuildSamplingPlan_AutoAdjustsSkipForShortAnimeEpisode()
    {
        // 24 min @ 23.976fps ≈ 34525 frames; skipping 10+10 min would skip 83% -> auto-adjusts to 10% (2.4 min)
        var plan = PreviewWorstFramePlanner.BuildSamplingPlan(34525, 24000.0 / 1001.0, requestedSkipMinutes: 10.0, sampleCount: 60);
        Assert.IsTrue(plan.AdjustedForShortVideo);
        Assert.IsTrue(plan.AppliedSkipMinutes > 2.0 && plan.AppliedSkipMinutes < 3.0);
        Assert.AreEqual(60, plan.SampleFrames.Count);
        Assert.IsTrue(plan.StartFrame > 3000 && plan.StartFrame < 4000);
    }

    [TestMethod]
    public void SceningFormatter_GeneratesReplaceRangesAndZones()
    {
        var ranges = new[]
        {
            new PreviewSceneRange(2400, 3600, "Dark", 1.25),
            new PreviewSceneRange(100, 500, "OP", 1.1)
        };

        var replace = PreviewSceningFormatter.FormatReplaceRanges(ranges, "src16", "deband");
        Assert.AreEqual("src16 = replace_ranges(src16, deband, [(100, 500), (2400, 3600)])", replace);

        var zones = PreviewSceningFormatter.FormatEncoderZones(ranges);
        Assert.AreEqual("--zones 100,500,b=1.1/2400,3600,b=1.25", zones);

        var av1an = PreviewSceningFormatter.FormatAv1anZones(ranges);
        StringAssert.Contains(av1an, "100 500 -q 18");
        StringAssert.Contains(av1an, "2400 3600 -q 18");
    }

    [TestMethod]
    public void FormatCropAndBbmodSnippet_IncludesImportCropAndBbmod()
    {
        var snippet = PreviewSceningFormatter.FormatCropAndBbmodSnippet(0, 140, 0, 140, "src");
        StringAssert.Contains(snippet, "import awsmfunc as awf");
        StringAssert.Contains(snippet, "core.std.CropRel(src, left=0, right=0, top=140, bottom=140)");
        StringAssert.Contains(snippet, "awf.bbmod(src");
    }
}
