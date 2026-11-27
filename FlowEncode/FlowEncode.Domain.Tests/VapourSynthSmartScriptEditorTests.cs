using FlowEncode.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class VapourSynthSmartScriptEditorTests
{
    [TestMethod]
    public void ResolveOutputTarget_FindsRealVariableNameForMultipleOutputs()
    {
        var script = """
            import vapoursynth as vs
            core = vs.core
            src16 = core.lsmas.LWLibavSource("bd.mkv")
            enc = core.lsmas.LWLibavSource("enc.mkv")
            src16.set_output(0)
            enc.set_output(1)
            """;

        var out0 = VapourSynthSmartScriptEditor.ResolveOutputTarget(script, 0);
        var out1 = VapourSynthSmartScriptEditor.ResolveOutputTarget(script, 1);

        Assert.AreEqual("src16", out0.VariableName);
        Assert.IsTrue(out0.FoundSetOutput);
        Assert.AreEqual("enc", out1.VariableName);
        Assert.IsTrue(out1.FoundSetOutput);
    }

    [TestMethod]
    public void ApplyCrop_UsesRealVariableAndUpdatesExistingCropInPlace()
    {
        var script = """
            import vapoursynth as vs
            core = vs.core
            src16 = core.lsmas.LWLibavSource("bd.mkv")
            src16.set_output()
            """;

        var first = VapourSynthSmartScriptEditor.ApplyCrop(script, 0, isAbsolute: false, left: 0, top: 140, widthOrRight: 0, heightOrBottom: 140);
        Assert.AreEqual("src16", first.ResolvedVariableName);
        Assert.IsFalse(first.UpdatedInPlace);
        StringAssert.Contains(first.UpdatedScript, "src16 = core.std.CropRel(src16, left=0, right=0, top=140, bottom=140)");
        Assert.IsTrue(first.UpdatedScript.IndexOf("CropRel") < first.UpdatedScript.IndexOf("src16.set_output()"));

        var second = VapourSynthSmartScriptEditor.ApplyCrop(first.UpdatedScript, 0, isAbsolute: false, left: 2, top: 138, widthOrRight: 2, heightOrBottom: 138);
        Assert.IsTrue(second.UpdatedInPlace);
        StringAssert.Contains(second.UpdatedScript, "src16 = core.std.CropRel(src16, left=2, right=2, top=138, bottom=138)");
        Assert.IsFalse(second.UpdatedScript.Contains("top=140"));
    }

    [TestMethod]
    public void ApplyCropAndBbmod_AddsMissingImportOrReusesExistingAlias()
    {
        var scriptNoImport = """
            import vapoursynth as vs
            core = vs.core
            src16 = core.lsmas.LWLibavSource("bd.mkv")
            src16.set_output()
            """;

        var res1 = VapourSynthSmartScriptEditor.ApplyCropAndBbmod(scriptNoImport, 0, 0, 140, 0, 140);
        Assert.IsTrue(res1.AddedImport);
        StringAssert.Contains(res1.UpdatedScript, "import awsmfunc as awf");
        StringAssert.Contains(res1.UpdatedScript, "src16 = awf.bbmod(src16");

        var scriptWithAlias = """
            import vapoursynth as vs
            import awsmfunc as myawf
            core = vs.core
            src16 = core.lsmas.LWLibavSource("bd.mkv")
            src16.set_output()
            """;

        var res2 = VapourSynthSmartScriptEditor.ApplyCropAndBbmod(scriptWithAlias, 0, 0, 140, 0, 140);
        Assert.IsFalse(res2.AddedImport);
        StringAssert.Contains(res2.UpdatedScript, "src16 = myawf.bbmod(src16");
    }

    [TestMethod]
    public void ApplyAlignment_TargetsSecondaryOutputVariableBeforeSetOutput()
    {
        var script = """
            import vapoursynth as vs
            core = vs.core
            bd = core.lsmas.LWLibavSource("bd.mkv")
            web = core.lsmas.LWLibavSource("web.mkv")
            bd.set_output(0)
            web.set_output(1)
            """;

        var res = VapourSynthSmartScriptEditor.ApplyAlignment(script, outputIndex: 1, frameOffset: 12, subpixelOffsetX: 0.25, subpixelOffsetY: -0.5);
        Assert.AreEqual("web", res.ResolvedVariableName);
        StringAssert.Contains(res.UpdatedScript, "web = web[12:]");
        StringAssert.Contains(res.UpdatedScript, "web = web.resize.Spline36(src_left=0.25, src_top=-0.5)");
        Assert.IsTrue(res.UpdatedScript.IndexOf("Spline36") < res.UpdatedScript.IndexOf("web.set_output(1)"));
    }

    [TestMethod]
    public void ApplyDerivedVariableAndRewireOutput_UpdatesSetOutputFromSrc8ToSrc16()
    {
        var script = """
            import vapoursynth as vs
            core = vs.core
            src8 = core.lsmas.LWLibavSource("bd.mkv")
            src8.set_output()
            """;

        var res = VapourSynthSmartScriptEditor.ApplyDerivedVariableAndRewireOutput(
            script,
            outputIndex: 0,
            newVariableName: "src16",
            expressionTemplateWithPlaceholder: "core.fmtc.bitdepth({var}, bits=16)");

        Assert.AreEqual("src16", res.ResolvedVariableName);
        StringAssert.Contains(res.UpdatedScript, "src16 = core.fmtc.bitdepth(src8, bits=16)");
        StringAssert.Contains(res.UpdatedScript, "src16.set_output()");
        Assert.IsFalse(res.UpdatedScript.Contains("src8.set_output()"));
    }

    [TestMethod]
    public void ApplyFilterTemplate_PromotesBitdepthAndRewiresOutputAndAutoImports()
    {
        var script = """
            import vapoursynth as vs
            core = vs.core
            src8 = core.lsmas.LWLibavSource("bd.mkv")
            src8.set_output()
            """;

        var promoted = VapourSynthSmartScriptEditor.ApplyFilterTemplate(
            script,
            outputIndex: 0,
            expressionTemplateWithPlaceholder: "core.fmtc.bitdepth({var}, bits=16)",
            derivedVariableName: "src16");

        Assert.AreEqual("src16", promoted.ResolvedVariableName);
        StringAssert.Contains(promoted.UpdatedScript, "src16 = core.fmtc.bitdepth(src8, bits=16)");
        StringAssert.Contains(promoted.UpdatedScript, "src16.set_output()");
        Assert.IsFalse(promoted.UpdatedScript.Contains("src8.set_output()"));

        var withFrameInfo = VapourSynthSmartScriptEditor.ApplyFilterTemplate(
            promoted.UpdatedScript,
            outputIndex: 0,
            expressionTemplateWithPlaceholder: "{alias}.FrameInfo({var}, title=\"Clip\")",
            importModule: "awsmfunc",
            importAlias: "awf");

        Assert.IsTrue(withFrameInfo.AddedImport);
        Assert.AreEqual("src16", withFrameInfo.ResolvedVariableName);
        StringAssert.Contains(withFrameInfo.UpdatedScript, "import awsmfunc as awf");
        StringAssert.Contains(withFrameInfo.UpdatedScript, "src16 = awf.FrameInfo(src16, title=\"Clip\")");
        Assert.IsTrue(withFrameInfo.UpdatedScript.IndexOf("FrameInfo") < withFrameInfo.UpdatedScript.IndexOf("src16.set_output()"));
    }

    [TestMethod]
    public void ApplyVideoSourceLoader_BuildsFreshScriptAndAvoidsVariableCollisions()
    {
        var fresh = VapourSynthSmartScriptEditor.ApplyVideoSourceLoader(string.Empty, @"D:\Videos\movie.mkv");
        Assert.AreEqual("src", fresh.ResolvedVariableName);
        Assert.IsTrue(fresh.AddedImport);
        StringAssert.Contains(fresh.UpdatedScript, "import vapoursynth as vs");
        StringAssert.Contains(fresh.UpdatedScript, "core = vs.core");
        StringAssert.Contains(fresh.UpdatedScript, @"src = core.lsmas.LWLibavSource(r""D:\Videos\movie.mkv"")");
        StringAssert.Contains(fresh.UpdatedScript, "src.set_output()");

        var second = VapourSynthSmartScriptEditor.ApplyVideoSourceLoader(fresh.UpdatedScript, @"D:\Videos\encode.mkv");
        Assert.AreEqual("src2", second.ResolvedVariableName);
        StringAssert.Contains(second.UpdatedScript, @"src2 = core.lsmas.LWLibavSource(r""D:\Videos\encode.mkv"")");
        StringAssert.Contains(second.UpdatedScript, "src.set_output()");
    }
}
