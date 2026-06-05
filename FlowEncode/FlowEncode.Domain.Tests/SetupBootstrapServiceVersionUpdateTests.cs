using System.Runtime.Versioning;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class SetupBootstrapServiceVersionUpdateTests
{
    [TestMethod]
    public void HasSetupDependencyVersionUpdate_X264SameShortAndLongRevision_ReturnsFalse()
    {
        var result = SetupBootstrapService.HasSetupDependencyVersionUpdate(
            SetupDependencyKind.X264,
            "x264 0.165.3222 b35605a",
            "stable-20250608-b35605ace3dd");

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void HasSetupDependencyVersionUpdate_X264DifferentDatedRevision_ReturnsTrue()
    {
        var result = SetupBootstrapService.HasSetupDependencyVersionUpdate(
            SetupDependencyKind.X264,
            "stable-20250601-a1234567890",
            "stable-20250608-b35605ace3dd");

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasSetupDependencyVersionUpdate_X265SemanticVersionStillCompares()
    {
        var result = SetupBootstrapService.HasSetupDependencyVersionUpdate(
            SetupDependencyKind.X265,
            "HEVC encoder version 4.1+12",
            "4.2");

        Assert.IsTrue(result);
    }
}
