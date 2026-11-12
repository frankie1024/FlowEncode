using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class PythonRuntimeCompatibilityTests
{
    [TestMethod]
    [DataRow(3, 12, true, true)]
    [DataRow(3, 13, true, true)]
    [DataRow(3, 12, false, false)]
    [DataRow(3, 11, true, false)]
    [DataRow(4, 0, true, false)]
    public void IsSupportedRuntime_RequiresPython312OrNewerX64(
        int major,
        int minor,
        bool is64Bit,
        bool expected)
    {
        var result = PythonRuntimeCompatibility.IsSupportedRuntime(new Version(major, minor), is64Bit);

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    [DataRow(3, 12, false)]
    [DataRow(3, 13, false)]
    [DataRow(3, 14, true)]
    [DataRow(3, 15, false)]
    [DataRow(4, 12, false)]
    public void IsTargetMinor_OnlyMatchesApprovedPython314(int major, int minor, bool expected)
    {
        var result = PythonRuntimeCompatibility.IsTargetMinor(new Version(major, minor));

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void Selection_Preserves20BindingWhenNewPythonIsInstalled()
    {
        var candidates = new[]
        {
            (Path: @"C:\Python314\python.exe", Version: new Version(3, 14, 10), Vs: true),
            (Path: @"C:\Python312\python.exe", Version: new Version(3, 12, 10), Vs: true)
        };
        var selected = candidates.OrderBy(item => PythonRuntimeCompatibility.SelectionRank(
            item.Path, item.Vs, item.Version, [candidates[1].Path], [candidates[0].Path])).First();
        Assert.AreEqual(candidates[1].Path, selected.Path);
    }

    [TestMethod]
    public void Selection_KeepsExistingVsEnvironmentBeforeFreshBaseline()
    {
        var legacy = PythonRuntimeCompatibility.SelectionRank("legacy", true, new Version(3, 12, 10), [], []);
        var baseline = PythonRuntimeCompatibility.SelectionRank("new", false, new Version(3, 14, 10), [], ["new"]);
        Assert.IsLessThan(baseline, legacy);
        Assert.IsLessThan(PythonRuntimeCompatibility.SelectionRank("future", false, new Version(3, 15), [], []), baseline);
    }

    [TestMethod]
    public void Selection_ManualBindingWinsOverManagedEnvironmentBinding()
    {
        string[] bindings = ["manual", "managed"];
        Assert.IsLessThan(PythonRuntimeCompatibility.SelectionRank("managed", true, new Version(3, 14), bindings, []),
            PythonRuntimeCompatibility.SelectionRank("manual", true, new Version(3, 12), bindings, []));
    }
}
