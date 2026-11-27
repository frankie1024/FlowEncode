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

    [TestMethod]
    public void ToggleBeforeAfterCompare_WrapsInPlaceAndDerivedFiltersAndRestoresCleanly()
    {
        var script = """
            import vapoursynth as vs
            core = vs.core
            src16 = core.lsmas.LWLibavSource("bd.mkv")
            src16 = core.placebo.Deband(src16, threshold=4.0)
            src16 = core.dfttest.DFTTest(src16, sigma=2.0)
            src16.set_output()
            """;

        var enabled = VapourSynthSmartScriptEditor.ToggleBeforeAfterCompare(script, cursorLineNumber: 4);
        Assert.IsFalse(enabled.UpdatedInPlace);
        StringAssert.Contains(enabled.UpdatedScript, "_fe_before = src16");
        StringAssert.Contains(enabled.UpdatedScript, "_fe_after = src16");
        Assert.IsTrue(enabled.UpdatedScript.IndexOf("_fe_after = src16") < enabled.UpdatedScript.IndexOf("DFTTest"));
        StringAssert.Contains(enabled.UpdatedScript, "_fe_before.set_output(0)");
        StringAssert.Contains(enabled.UpdatedScript, "_fe_after.set_output(1)");
        StringAssert.Contains(enabled.UpdatedScript, "# [FlowEncode A/B saved] src16.set_output()");

        var restored = VapourSynthSmartScriptEditor.ToggleBeforeAfterCompare(enabled.UpdatedScript, cursorLineNumber: 4);
        Assert.IsTrue(restored.UpdatedInPlace);
        Assert.AreEqual(script, restored.UpdatedScript);
    }

    [TestMethod]
    public void ApplyMultiLineFilterTemplate_InsertsStatementsBeforeSetOutput()
    {
        var script = """
            import vapoursynth as vs
            core = vs.core
            src = core.lsmas.LWLibavSource("dvd.mkv")
            src.set_output()
            """;

        var res = VapourSynthSmartScriptEditor.ApplyMultiLineFilterTemplate(
            script,
            outputIndex: 0,
            ["{var} = core.vivtc.VFM({var}, order=1)", "{var} = core.vivtc.VDecimate({var})"]);

        StringAssert.Contains(res.UpdatedScript, "src = core.vivtc.VFM(src, order=1)");
        StringAssert.Contains(res.UpdatedScript, "src = core.vivtc.VDecimate(src)");
        Assert.IsTrue(res.UpdatedScript.IndexOf("VDecimate") < res.UpdatedScript.IndexOf("src.set_output()"));
    }

    [TestMethod]
    public void StubGenerator_EmitsValidPyiWithCoreAndBoundVideoNodePlugins()
    {
        var snapshot = new VapourSynthLanguageFeaturesSnapshot(
            IsRuntimeReady: true,
            RuntimeSummary: "VapourSynth R79 / 2 plugins",
            CoreMembers: [new VapourSynthCoreMemberDescriptor("resize", "namespace", "zimg", "Resize")],
            Namespaces:
            [
                new VapourSynthPluginNamespaceDescriptor(
                    "resize",
                    "com.vapoursynth.resize",
                    "Resize",
                    [
                        new VapourSynthPluginFunctionDescriptor(
                            "Spline36",
                            "core.resize.Spline36",
                            "Spline36(clip, width=None, height=None, format=None)",
                            "VideoNode",
                            [
                                new VapourSynthFunctionParameterDescriptor("clip", "clip", ""),
                                new VapourSynthFunctionParameterDescriptor("width", "width=None", ""),
                                new VapourSynthFunctionParameterDescriptor("height", "height=None", ""),
                                new VapourSynthFunctionParameterDescriptor("format", "format=None", "")
                            ],
                            "Zimg Spline36")
                    ])
            ],
            VsConstants: [new VapourSynthCoreMemberDescriptor("YUV420P10", "enumMember", "PresetVideoFormat = 3000012", "10-bit 4:2:0")]);

        var stub = VapourSynthStubGenerator.GenerateStub(snapshot);
        StringAssert.Contains(stub, "YUV420P10: int");
        StringAssert.Contains(stub, "class _Plugin_resize:");
        StringAssert.Contains(stub, "def Spline36(self, clip: Any = ..., width: Any = ..., height: Any = ..., format: Any = ..., **kwargs: Any) -> 'VideoNode':");
        StringAssert.Contains(stub, "class _BoundPlugin_resize:");
        StringAssert.Contains(stub, "def Spline36(self, width: Any = ..., height: Any = ..., format: Any = ..., **kwargs: Any) -> 'VideoNode':");
        StringAssert.Contains(stub, "    resize: _BoundPlugin_resize");
        StringAssert.Contains(stub, "    resize: _Plugin_resize");
    }

    [TestMethod]
    public void ApplyReplaceRangesAndMaskedMerge_AutoDefineFltWhenMissingAndReuseWhenPresent()
    {
        var scriptNoFlt = """
            import vapoursynth as vs
            core = vs.core
            sample10 = core.lsmas.LWLibavSource("a.mkv")
            sample10.set_output(0)
            """;

        var r1 = VapourSynthSmartScriptEditor.ApplyReplaceRanges(
            scriptNoFlt,
            outputIndex: 0,
            [new PreviewSceneRange(733, 1979)]);
        StringAssert.Contains(r1.UpdatedScript, "from vstools import replace_ranges");
        StringAssert.Contains(r1.UpdatedScript, "flt = sample10");
        StringAssert.Contains(r1.UpdatedScript, "sample10 = replace_ranges(sample10, flt, [(733, 1979)])");
        Assert.IsTrue(r1.UpdatedScript.IndexOf("flt = sample10") < r1.UpdatedScript.IndexOf("replace_ranges(sample10"));

        var m1 = VapourSynthSmartScriptEditor.ApplyMaskedMerge(scriptNoFlt, outputIndex: 0);
        StringAssert.Contains(m1.UpdatedScript, "flt = sample10");
        StringAssert.Contains(m1.UpdatedScript, "core.std.MaskedMerge(flt, sample10, mask)");

        var scriptWithFlt = """
            import vapoursynth as vs
            core = vs.core
            sample10 = core.lsmas.LWLibavSource("a.mkv")
            flt = core.placebo.Deband(sample10)
            sample10.set_output(0)
            """;

        var r2 = VapourSynthSmartScriptEditor.ApplyReplaceRanges(
            scriptWithFlt,
            outputIndex: 0,
            [new PreviewSceneRange(10, 20)]);
        Assert.IsFalse(r2.UpdatedScript.Contains("flt = sample10"));
        StringAssert.Contains(r2.UpdatedScript, "sample10 = replace_ranges(sample10, flt, [(10, 20)])");
    }

    [TestMethod]
    public void SmartInsert_EnsuresRuntimeImportsBaseClipAndReusesCustomAliases()
    {
        // 1. Blank script -> auto-adds vs, core, BlankClip, filter, and set_output()
        var fromEmpty = VapourSynthSmartScriptEditor.ApplyFilterTemplate(
            string.Empty,
            outputIndex: 0,
            expressionTemplateWithPlaceholder: "core.placebo.Deband({var}, threshold=4.0)");
        Assert.IsTrue(fromEmpty.AddedImport);
        StringAssert.Contains(fromEmpty.UpdatedScript, "import vapoursynth as vs");
        StringAssert.Contains(fromEmpty.UpdatedScript, "core = vs.core");
        StringAssert.Contains(fromEmpty.UpdatedScript, "clip = core.std.BlankClip(");
        StringAssert.Contains(fromEmpty.UpdatedScript, "clip = core.placebo.Deband(clip, threshold=4.0)");
        StringAssert.Contains(fromEmpty.UpdatedScript, "clip.set_output()");

        // 2. Custom vs alias and missing core -> reuses 'v' and inserts 'core = v.core'
        var customVs = """
            import vapoursynth as v
            src = v.core.lsmas.LWLibavSource("a.mkv")
            src.set_output()
            """;
        var hdr = VapourSynthSmartScriptEditor.ApplyFilterTemplate(
            customVs,
            outputIndex: 0,
            expressionTemplateWithPlaceholder: "core.placebo.Tonemap({var}.resize.Spline36(format=vs.YUV444P16))");
        Assert.IsTrue(hdr.AddedImport);
        StringAssert.Contains(hdr.UpdatedScript, "core = v.core");
        StringAssert.Contains(hdr.UpdatedScript, "format=v.YUV444P16");
        Assert.IsFalse(hdr.UpdatedScript.Contains("import vapoursynth as vs"));

        // 3. Aliased from-import (replace_ranges as rfs) -> reuses alias without duplicating import
        var aliasedRfs = """
            import vapoursynth as vs
            from vstools import replace_ranges as rfs
            core = vs.core
            src = core.lsmas.LWLibavSource("a.mkv")
            src.set_output()
            """;
        var rfsResult = VapourSynthSmartScriptEditor.ApplyReplaceRanges(
            aliasedRfs,
            outputIndex: 0,
            [new PreviewSceneRange(10, 20)]);
        Assert.IsFalse(rfsResult.AddedImport);
        StringAssert.Contains(rfsResult.UpdatedScript, "flt = src");
        StringAssert.Contains(rfsResult.UpdatedScript, "src = rfs(src, flt, [(10, 20)])");

        // 4. Script with only set_output() and no clip assignment -> scaffolds BlankClip and inserts filter BEFORE set_output()
        var onlyOutput = """
            import vapoursynth as vs
            core = vs.core

            clip.set_output()
            """;
        var withClip = VapourSynthSmartScriptEditor.ApplyFilterTemplate(
            onlyOutput,
            outputIndex: 0,
            expressionTemplateWithPlaceholder: "core.placebo.Deband({var}, threshold=4.0)");
        StringAssert.Contains(withClip.UpdatedScript, "clip = core.std.BlankClip(");
        StringAssert.Contains(withClip.UpdatedScript, "clip = core.placebo.Deband(clip, threshold=4.0)");
        Assert.IsTrue(withClip.UpdatedScript.IndexOf("BlankClip") < withClip.UpdatedScript.IndexOf("Deband"));
        Assert.IsTrue(withClip.UpdatedScript.IndexOf("Deband") < withClip.UpdatedScript.IndexOf("clip.set_output()"));
    }

    [TestMethod]
    public void ApplyCrop_ReplacesMultiLineCropWithoutOrphanArguments()
    {
        var script = """
            import vapoursynth as vs
            core = vs.core
            src = core.lsmas.LWLibavSource("bd.mkv")
            src = core.std.CropRel(
                src,
                left=0,
                right=0,
                top=140,
                bottom=140
            )
            src.set_output()
            """;

        var updated = VapourSynthSmartScriptEditor.ApplyCrop(script, 0, isAbsolute: false, left: 2, top: 138, widthOrRight: 2, heightOrBottom: 138);
        Assert.IsTrue(updated.UpdatedInPlace);
        StringAssert.Contains(updated.UpdatedScript, "src = core.std.CropRel(src, left=2, right=2, top=138, bottom=138)");
        Assert.IsFalse(updated.UpdatedScript.Contains("top=140"));
        Assert.IsFalse(updated.UpdatedScript.Contains("bottom=140"));
    }

    [TestMethod]
    public void EnsureImports_DoesNotSplitMultiLineFromImportOrIndentedImport()
    {
        var script = """
            import vapoursynth as vs
            from vstools import (
                core,
                replace_ranges,
            )

            def helper():
                import math
                return math.pi

            src = core.lsmas.LWLibavSource("bd.mkv")
            src.set_output()
            """;

        var res = VapourSynthSmartScriptEditor.ApplyCropAndBbmod(script, 0, 0, 140, 0, 140);
        Assert.IsTrue(res.AddedImport);
        var importIdx = res.UpdatedScript.IndexOf("import awsmfunc as awf", StringComparison.Ordinal);
        var closeParenIdx = res.UpdatedScript.IndexOf(")", StringComparison.Ordinal);
        var defIdx = res.UpdatedScript.IndexOf("def helper():", StringComparison.Ordinal);
        Assert.IsTrue(importIdx > closeParenIdx && importIdx < defIdx);

        var rfs = VapourSynthSmartScriptEditor.ApplyReplaceRanges(script, 0, [new PreviewSceneRange(10, 20)]);
        Assert.IsFalse(rfs.AddedImport);
    }

    [TestMethod]
    public void ToggleBeforeAfterCompare_NoOpsOnFirstAssignmentWithoutPriorClip()
    {
        var script = """
            import vapoursynth as vs
            core = vs.core
            src = core.lsmas.LWLibavSource("bd.mkv")
            src.set_output()
            """;

        var res = VapourSynthSmartScriptEditor.ToggleBeforeAfterCompare(script, cursorLineNumber: 3);
        Assert.AreEqual(script, res.UpdatedScript);
    }

    [TestMethod]
    public void StubGenerator_AvoidsDuplicateSelfKwargsAndDanglingBareStar()
    {
        var snapshot = new VapourSynthLanguageFeaturesSnapshot(
            IsRuntimeReady: true,
            RuntimeSummary: "test",
            CoreMembers: [],
            Namespaces:
            [
                new VapourSynthPluginNamespaceDescriptor(
                    "edge",
                    "com.test.edge",
                    "Edge",
                    [
                        new VapourSynthPluginFunctionDescriptor(
                            "Weird",
                            "core.edge.Weird",
                            "Weird(self, args, kwargs, *, from)",
                            "VideoNode",
                            [
                                new VapourSynthFunctionParameterDescriptor("self", "self", ""),
                                new VapourSynthFunctionParameterDescriptor("args", "args", ""),
                                new VapourSynthFunctionParameterDescriptor("kwargs", "kwargs", ""),
                                new VapourSynthFunctionParameterDescriptor("", "*", ""),
                                new VapourSynthFunctionParameterDescriptor("from", "from", "")
                            ],
                            "Edge case params")
                    ])
            ],
            VsConstants: []);

        var stub = VapourSynthStubGenerator.GenerateStub(snapshot);
        StringAssert.Contains(stub, "def Weird(self, args: Any = ..., kwargs: Any = ..., **_vs_kwargs: Any) -> 'VideoNode':");
        Assert.IsFalse(stub.Contains("self, self:"));
        Assert.IsFalse(stub.Contains("*, **"));
    }
}
