using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class AutoCompressionOutputFinalizerTests
{
    private string _testRoot = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "FlowEncodeAutoCompressionOutputFinalizerTests",
            Guid.NewGuid().ToString("N"));
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
    public void TryFinalizeOutput_WhenStagedFileExists_MovesFileToFinalPath()
    {
        var jobId = Guid.NewGuid();
        var stagedDirectory = Path.Combine(_testRoot, ".flowencode-temp", "av1an", jobId.ToString("N"));
        var finalOutputPath = Path.Combine(_testRoot, "final.mkv");
        var stagedOutputPath = Path.Combine(stagedDirectory, "final.mkv");
        Directory.CreateDirectory(stagedDirectory);
        File.WriteAllText(stagedOutputPath, "encoded-data");

        var success = AutoCompressionOutputFinalizer.TryFinalizeOutput(
            jobId,
            stagedOutputPath,
            finalOutputPath,
            AppLanguage.English,
            writeDiagnostic: null,
            out var failureSummary);

        Assert.IsTrue(success);
        Assert.AreEqual(string.Empty, failureSummary);
        Assert.AreEqual("encoded-data", File.ReadAllText(finalOutputPath));
        Assert.IsFalse(File.Exists(stagedOutputPath));
    }

    [TestMethod]
    public void TryFinalizeOutput_WhenStagedFileIsMissing_ReturnsFailureAndDoesNotCreateFinalOutput()
    {
        var jobId = Guid.NewGuid();
        var stagedDirectory = Path.Combine(_testRoot, ".flowencode-temp", "av1an", jobId.ToString("N"));
        var finalOutputPath = Path.Combine(_testRoot, "missing.mkv");
        var stagedOutputPath = Path.Combine(stagedDirectory, "missing.mkv");
        Directory.CreateDirectory(stagedDirectory);

        var success = AutoCompressionOutputFinalizer.TryFinalizeOutput(
            jobId,
            stagedOutputPath,
            finalOutputPath,
            AppLanguage.English,
            writeDiagnostic: null,
            out var failureSummary);

        Assert.IsFalse(success);
        StringAssert.Contains(failureSummary, "did not produce the output file");
        StringAssert.Contains(failureSummary, finalOutputPath);
        Assert.IsFalse(File.Exists(finalOutputPath));
    }

    [TestMethod]
    public async Task TryFinalizeOutput_WhenStagedFileAppearsLate_StillFinalizesSuccessfully()
    {
        var jobId = Guid.NewGuid();
        var stagedDirectory = Path.Combine(_testRoot, ".flowencode-temp", "av1an", jobId.ToString("N"));
        var finalOutputPath = Path.Combine(_testRoot, "late.mkv");
        var stagedOutputPath = Path.Combine(stagedDirectory, "late.mkv");

        var writerTask = Task.Run(async () =>
        {
            await Task.Delay(300);
            Directory.CreateDirectory(stagedDirectory);
            File.WriteAllText(stagedOutputPath, "encoded-data");
        });

        var success = AutoCompressionOutputFinalizer.TryFinalizeOutput(
            jobId,
            stagedOutputPath,
            finalOutputPath,
            AppLanguage.English,
            writeDiagnostic: null,
            out var failureSummary);

        await writerTask;

        Assert.IsTrue(success);
        Assert.AreEqual(string.Empty, failureSummary);
        Assert.AreEqual("encoded-data", File.ReadAllText(finalOutputPath));
        Assert.IsFalse(File.Exists(stagedOutputPath));
    }
}
