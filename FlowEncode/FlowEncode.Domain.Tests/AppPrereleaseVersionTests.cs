using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class AppPrereleaseVersionTests
{
    [TestMethod]
    [DataRow("3.0.0-beta.1", "3.0.0", -1)]
    [DataRow("3.0.0-beta.2", "3.0.0-beta.10", -1)]
    [DataRow("v3.0.0-rc.1", "3.0.0-beta.10", 1)]
    [DataRow("3.0.0-beta.1", "2.0.2", 1)]
    [DataRow("3.0.0-beta.1+abc1234", "3.0.0-beta.1", 0)]
    public void Prerelease_IsPreservedAndComparesBeforeStable(string current, string latest, int expected) =>
        Assert.AreEqual(expected, GitHubAppUpdateService.CompareVersionLabels(current, latest));
}
