using FlowEncode.Application;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class VapourSynthSnippetLibraryTests
{
    [TestMethod]
    public void AllSnippetIds_AreUniqueAndResolvable()
    {
        var ids = VapourSynthSnippetLibrary.All.Select(snippet => snippet.Id).ToArray();

        CollectionAssert.AllItemsAreUnique(ids);
        foreach (var id in ids)
        {
            Assert.IsNotNull(VapourSynthSnippetLibrary.FindById(id));
        }
    }

    [TestMethod]
    public void Snippets_DoNotEmbedLocalReferencePaths()
    {
        foreach (var snippet in VapourSynthSnippetLibrary.All)
        {
            Assert.IsFalse(snippet.InsertText.Contains(@"D:\YP", StringComparison.OrdinalIgnoreCase), snippet.Id);
            Assert.IsFalse(snippet.InsertText.Contains(@"D:\codex", StringComparison.OrdinalIgnoreCase), snippet.Id);
        }
    }

    [TestMethod]
    public void Snippets_CoverCommonWorkspacePatterns()
    {
        var combinedText = string.Join("\n", VapourSynthSnippetLibrary.All.Select(snippet => snippet.InsertText));

        StringAssert.Contains(combinedText, "core.lsmas.LWLibavSource");
        StringAssert.Contains(combinedText, "core.std.Crop");
        StringAssert.Contains(combinedText, "core.resize.Spline36");
        StringAssert.Contains(combinedText, "set_output(0)");
        StringAssert.Contains(combinedText, "core.vivtc.VFM");
    }
}
