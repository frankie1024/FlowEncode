using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class SavedTemplateFilePathPlannerTests
{
    [TestMethod]
    public void SanitizeFileName_ReplacesInvalidCharacters()
    {
        var sanitized = SavedTemplateFilePathPlanner.SanitizeFileName("bad:name?");

        Assert.AreEqual("bad_name_", sanitized);
    }

    [TestMethod]
    public void SanitizeFileName_WhenValueIsBlank_ReturnsFallbackName()
    {
        var sanitized = SavedTemplateFilePathPlanner.SanitizeFileName("   ");

        Assert.AreEqual(SavedTemplateFilePathPlanner.DefaultTemplateFileName, sanitized);
    }

    [TestMethod]
    public void BuildAvailableFilePath_WhenNameIsFree_UsesSanitizedName()
    {
        var rootPath = @"D:\templates";
        var occupiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var filePath = SavedTemplateFilePathPlanner.BuildAvailableFilePath(
            rootPath,
            "My:Template",
            occupiedPaths);

        Assert.AreEqual(@"D:\templates\My_Template.profile", filePath);
    }

    [TestMethod]
    public void BuildAvailableFilePath_WhenNameIsOccupied_UsesNextSuffix()
    {
        var rootPath = @"D:\templates";
        var occupiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\templates\My Template.profile",
            @"D:\templates\My Template-2.profile"
        };

        var filePath = SavedTemplateFilePathPlanner.BuildAvailableFilePath(
            rootPath,
            "My Template",
            occupiedPaths);

        Assert.AreEqual(@"D:\templates\My Template-3.profile", filePath);
    }
}
