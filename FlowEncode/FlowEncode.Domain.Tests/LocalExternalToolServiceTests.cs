using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LocalExternalToolServiceTests
{
    [DataTestMethod]
    [DataRow("v1.0-rc1")]
    [DataRow("v1.0-rc")]
    [DataRow("v1.0.rc2")]
    [DataRow("v1.0_rc1")]
    [DataRow("v1.0 rc")]
    [DataRow("rc1")]
    [DataRow("rc")]
    [DataRow("v2.5.0-RC3")]
    public void ContainsUnstableReleaseMarker_ReleaseCandidateFormats_ReturnsTrue(string value)
    {
        Assert.IsTrue(LocalExternalToolService.ContainsUnstableReleaseMarker(value));
    }

    [DataTestMethod]
    [DataRow("source")]
    [DataRow("resource")]
    [DataRow("architecture")]
    [DataRow("March")]
    [DataRow("arc")]
    [DataRow("search")]
    [DataRow("recorder")]
    [DataRow("v1.0-source")]
    [DataRow("resource-pack")]
    public void ContainsUnstableReleaseMarker_WordsContainingRc_ReturnsFalse(string value)
    {
        Assert.IsFalse(LocalExternalToolService.ContainsUnstableReleaseMarker(value));
    }

    [DataTestMethod]
    [DataRow("v1.0-beta1")]
    [DataRow("nightly-build")]
    [DataRow("dev-20260517")]
    [DataRow("alpha")]
    [DataRow("preview")]
    [DataRow("unstable")]
    [DataRow("latest")]
    public void ContainsUnstableReleaseMarker_OtherUnstableMarkers_ReturnsTrue(string value)
    {
        Assert.IsTrue(LocalExternalToolService.ContainsUnstableReleaseMarker(value));
    }

    [DataTestMethod]
    [DataRow("v1.0.0")]
    [DataRow("2026.05.17")]
    [DataRow("")]
    [DataRow(null)]
    [DataRow("   ")]
    public void ContainsUnstableReleaseMarker_StableOrNull_ReturnsFalse(string? value)
    {
        Assert.IsFalse(LocalExternalToolService.ContainsUnstableReleaseMarker(value));
    }
}
