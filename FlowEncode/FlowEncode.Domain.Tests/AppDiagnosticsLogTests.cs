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
        var localStatePath = Path.Combine(_testRoot!, "local-state");
        var installRootPath = Path.Combine(_testRoot!, "install-root");
        var workspaceRootPath = Path.Combine(_testRoot!, "workspace");
        Directory.CreateDirectory(localStatePath);
        Directory.CreateDirectory(installRootPath);

        var paths = new LocalAppPaths(localStatePath, installRootPath, [workspaceRootPath]);
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
}
