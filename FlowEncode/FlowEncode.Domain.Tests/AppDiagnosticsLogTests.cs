using FlowEncode.Application;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class AppDiagnosticsLogTests
{
    private string? _testRoot;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "FlowEncodeAppDiagnosticsLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (!string.IsNullOrWhiteSpace(_testRoot) && Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [TestMethod]
    public void Write_WithSeverityContextAndException_PersistsDiagnosticEntry()
    {
        var paths = CreatePaths();
        var exception = new InvalidOperationException("Synthetic failure");

        AppDiagnosticsLog.Write(
            paths,
            "DiagnosticsTest",
            "Operation failed",
            AppDiagnosticSeverity.Error,
            new Dictionary<string, string?>
            {
                ["section"] = "overview",
                ["path"] = "C:\\Temp\\input.vpy"
            },
            exception);

        var logPath = Path.Combine(paths.LogsRootPath, "diagnostics.log");
        var log = File.ReadAllText(logPath);

        StringAssert.Contains(log, "Error DiagnosticsTest: Operation failed");
        StringAssert.Contains(log, "section=overview");
        StringAssert.Contains(log, "path=C:\\Temp\\input.vpy");
        StringAssert.Contains(log, nameof(InvalidOperationException));
        StringAssert.Contains(log, "Synthetic failure");
    }

    [TestMethod]
    public void Write_WhenLogWouldExceedLimit_RotatesBeforeAppending()
    {
        var paths = CreatePaths();
        var logPath = Path.Combine(paths.LogsRootPath, "diagnostics.log");
        File.WriteAllText(logPath, "old log entry");

        AppDiagnosticsLog.Write(
            paths,
            "DiagnosticsTest",
            "new log entry",
            maxLogFileBytes: 16,
            retainedArchiveCount: 2);

        Assert.IsTrue(File.Exists(logPath));
        Assert.IsTrue(File.Exists(logPath + ".1"));
        StringAssert.Contains(File.ReadAllText(logPath), "new log entry");
        Assert.AreEqual("old log entry", File.ReadAllText(logPath + ".1"));
    }

    [TestMethod]
    public void Write_WhenArchivesExist_RetainsConfiguredArchiveCount()
    {
        var paths = CreatePaths();
        var logPath = Path.Combine(paths.LogsRootPath, "diagnostics.log");
        File.WriteAllText(logPath, "current");
        File.WriteAllText(logPath + ".1", "archive1");
        File.WriteAllText(logPath + ".2", "archive2");

        AppDiagnosticsLog.Write(
            paths,
            "DiagnosticsTest",
            "new current",
            maxLogFileBytes: 4,
            retainedArchiveCount: 2);

        StringAssert.Contains(File.ReadAllText(logPath), "new current");
        Assert.AreEqual("current", File.ReadAllText(logPath + ".1"));
        Assert.AreEqual("archive1", File.ReadAllText(logPath + ".2"));
        Assert.IsFalse(File.Exists(logPath + ".3"));
    }

    private LocalAppPaths CreatePaths()
    {
        var localStatePath = Path.Combine(_testRoot!, "local-state");
        var installRootPath = Path.Combine(_testRoot!, "install-root");
        var workspaceRootPath = Path.Combine(_testRoot!, "workspace");
        Directory.CreateDirectory(localStatePath);
        Directory.CreateDirectory(installRootPath);

        return new LocalAppPaths(localStatePath, installRootPath, [workspaceRootPath]);
    }
}
