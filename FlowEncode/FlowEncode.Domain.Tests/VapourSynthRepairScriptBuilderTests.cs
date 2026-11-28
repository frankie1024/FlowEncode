using FlowEncode.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class VapourSynthRepairScriptBuilderTests
{
    private const string Source = "import vapoursynth as vs\r\nsource.set_output(2, alpha=alpha)\r\nother.set_output(4)\r\n";
    private static readonly PreviewRepairSuggestion Crop = new("crop", [0],
        ["_fe_repair_clip = _fe_repair_clip.std.Crop(top=20, bottom=20)",
         "_fe_repair_clip = _fe_repair_clip.fb.FillBorders(top=1, bottom=1, mode='fillmargins')"],
        [0, 21, 0, 21], Remainder: [0, 1, 0, 1],
        AlphaStatements: ["_fe_repair_alpha = _fe_repair_alpha.std.Crop(top=21, bottom=21)"]);

    [TestMethod]
    public void Draft_PreservesOtherOutputsAndTheRepairedOutputsAlpha()
    {
        var result = VapourSynthRepairScriptBuilder.Build(Source, 2, [Crop]);

        StringAssert.StartsWith(result.UpdatedScript, Source);
        StringAssert.Contains(result.UpdatedScript, "if _fe_repair_alpha is not None:\r\n    _fe_repair_alpha = _fe_repair_alpha.std.Crop(top=21, bottom=21)");
        StringAssert.Contains(result.UpdatedScript, "_fe_repaired.set_output(2, alpha=_fe_repair_alpha, alt_output=_fe_repair_output.alt_output)");
        Assert.IsFalse(result.UpdatedScript.Contains("clear_outputs", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DraftComparison_UsesOriginalAndProcessedClipsAsItsOnlyOutputs()
    {
        var result = VapourSynthRepairScriptBuilder.Build(Source, 2, [Crop], compare: true);

        StringAssert.Contains(result.UpdatedScript, "_fe_repair_vs.get_output(2)");
        StringAssert.Contains(result.UpdatedScript, "_fe_repair_vs.clear_outputs()\r\n_fe_repair_output.clip.set_output(0)\r\n_fe_repaired.set_output(1)");
        Assert.IsFalse(result.UpdatedScript.Contains("_fe_repair_alpha", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Draft_LimitsSceneFiltersToConfirmedRanges()
    {
        var deband = new PreviewRepairSuggestion("deband", [15], ["_fe_repair_clip = deband(_fe_repair_clip)"], [],
            Ranges: [new(15, 20), new(8, 12), new(12, 15), new(35, 35)], Scope: "widespread");

        var result = VapourSynthRepairScriptBuilder.Build(Source, 2, [deband]);

        StringAssert.Contains(result.UpdatedScript, "parts.append(original[cursor:start])");
        StringAssert.Contains(result.UpdatedScript, "parts.append(processed[start:end + 1])");
        StringAssert.Contains(result.UpdatedScript, "_fe_repair_ranges(_fe_repair_original, _fe_repair_clip, [(8, 20), (35, 35)])");
    }
}
