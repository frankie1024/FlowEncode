using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ExecutablePathResolverTests
{
    private string _testRoot = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "FlowEncodeExecutablePathResolverTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [TestMethod]
    public void ResolveFromInput_WhenValueIsExactFile_ReturnsFullPath()
    {
        var executablePath = Path.Combine(_testRoot, "tool.exe");
        File.WriteAllText(executablePath, "stub");

        var resolved = ExecutablePathResolver.ResolveFromInput(executablePath, ["tool.exe"]);

        Assert.AreEqual(Path.GetFullPath(executablePath), resolved);
    }

    [TestMethod]
    public void ResolveFromInput_WhenValueIsDirectory_ReturnsContainedExecutable()
    {
        var executablePath = Path.Combine(_testRoot, "tool.exe");
        File.WriteAllText(executablePath, "stub");

        var resolved = ExecutablePathResolver.ResolveFromInput(_testRoot, ["tool.exe"]);

        Assert.AreEqual(Path.GetFullPath(executablePath), resolved);
    }

    [TestMethod]
    public void ResolveFromInput_WhenValueIsBareFileName_SearchesProvidedRoots()
    {
        var pathRoot = Path.Combine(_testRoot, "path-root");
        Directory.CreateDirectory(pathRoot);
        var executablePath = Path.Combine(pathRoot, "tool.exe");
        File.WriteAllText(executablePath, "stub");

        var resolved = ExecutablePathResolver.ResolveFromInput("tool.exe", ["tool.exe"], [pathRoot]);

        Assert.AreEqual(Path.GetFullPath(executablePath), resolved);
    }
}
