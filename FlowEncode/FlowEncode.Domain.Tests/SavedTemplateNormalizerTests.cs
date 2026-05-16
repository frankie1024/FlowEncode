using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class SavedTemplateNormalizerTests
{
    [TestMethod]
    public void Normalize_TrimsTemplateNameAndNotesAndSyncsProfile()
    {
        var fallbackUpdatedAt = new DateTimeOffset(2026, 5, 16, 15, 30, 0, TimeSpan.FromHours(8));
        var template = CreateTemplate(
            name: "  My Template  ",
            notes: "  Notes  ",
            updatedAt: fallbackUpdatedAt);

        var normalized = SavedTemplateNormalizer.Normalize(template, fallbackUpdatedAt);

        Assert.AreEqual("My Template", normalized.Name);
        Assert.AreEqual("Notes", normalized.Notes);
        Assert.AreEqual("My Template", normalized.Profile.Name);
        Assert.AreEqual("Notes", normalized.Profile.Description);
    }

    [TestMethod]
    public void Normalize_WhenUpdatedAtIsDefault_UsesFallbackUpdatedAt()
    {
        var fallbackUpdatedAt = new DateTimeOffset(2026, 5, 16, 15, 30, 0, TimeSpan.FromHours(8));
        var template = CreateTemplate(updatedAt: default);

        var normalized = SavedTemplateNormalizer.Normalize(template, fallbackUpdatedAt);

        Assert.AreEqual(fallbackUpdatedAt, normalized.UpdatedAt);
    }

    [TestMethod]
    public void Normalize_WhenUpdatedAtIsSet_PreservesUpdatedAt()
    {
        var originalUpdatedAt = new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.FromHours(8));
        var fallbackUpdatedAt = new DateTimeOffset(2026, 5, 16, 15, 30, 0, TimeSpan.FromHours(8));
        var template = CreateTemplate(updatedAt: originalUpdatedAt);

        var normalized = SavedTemplateNormalizer.Normalize(template, fallbackUpdatedAt);

        Assert.AreEqual(originalUpdatedAt, normalized.UpdatedAt);
    }

    private static SavedTemplate CreateTemplate(
        string name = "Template",
        string notes = "",
        DateTimeOffset updatedAt = default)
    {
        return new SavedTemplate(
            "template-id",
            name,
            notes,
            CreateProfile(),
            updatedAt,
            IsPinned: false);
    }

    private static EncodingProfile CreateProfile()
    {
        return new EncodingProfile(
            EncoderKind.X264,
            "Old Profile",
            "Old notes",
            string.Empty,
            string.Empty,
            string.Empty,
            RateControlMode.Crf,
            18,
            null,
            "264",
            string.Empty,
            string.Empty);
    }
}
