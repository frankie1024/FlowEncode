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
    [DataRow(3, 12, true)]
    [DataRow(3, 13, false)]
    [DataRow(4, 12, false)]
    public void IsTargetMinor_OnlyMatchesPython312(int major, int minor, bool expected)
    {
        var result = PythonRuntimeCompatibility.IsTargetMinor(new Version(major, minor));

        Assert.AreEqual(expected, result);
    }
}
