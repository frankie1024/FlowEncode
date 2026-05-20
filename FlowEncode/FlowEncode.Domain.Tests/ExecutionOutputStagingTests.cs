using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ExecutionOutputStagingTests
{
    private string _testRoot = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "FlowEncodeExecutionOutputStagingTests",
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
    public void FinalizeFile_WhenDestinationExists_ReplacesContentWithoutLeavingTempArtifacts()
    {
        var finalPath = Path.Combine(_testRoot, "movie.opus");
        var stagingDirectory = Path.Combine(_testRoot, ".flowencode-temp", "audio", "job");
        var jobId = Guid.NewGuid();
        Directory.CreateDirectory(stagingDirectory);
        File.WriteAllText(finalPath, "old-content");

        var stagedPath = ExecutionOutputStaging.CreateStagedFilePath(stagingDirectory, finalPath, jobId);
        File.WriteAllText(stagedPath, "new-content");

        ExecutionOutputStaging.FinalizeFile(stagedPath, finalPath, jobId);

        Assert.AreEqual("new-content", File.ReadAllText(finalPath));
        Assert.IsFalse(File.Exists(stagedPath));
        Assert.IsFalse(Directory.EnumerateFiles(_testRoot, "*.backup.tmp*", SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public void FinalizeDirectory_MovesStagedFilesIntoFinalDirectoryAndRemovesStage()
    {
        var finalDirectory = Path.Combine(_testRoot, "demux-out");
        var stagedDirectory = Path.Combine(finalDirectory, ".flowencode-temp", "bluray-demux", "job");
        Directory.CreateDirectory(Path.Combine(stagedDirectory, "nested"));
        Directory.CreateDirectory(finalDirectory);
        File.WriteAllText(Path.Combine(finalDirectory, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(stagedDirectory, "00001.track.ac3"), "audio");
        File.WriteAllText(Path.Combine(stagedDirectory, "nested", "chapters.txt"), "chapters");

        ExecutionOutputStaging.FinalizeDirectory(stagedDirectory, finalDirectory);

        Assert.IsTrue(File.Exists(Path.Combine(finalDirectory, "keep.txt")));
        Assert.AreEqual("audio", File.ReadAllText(Path.Combine(finalDirectory, "00001.track.ac3")));
        Assert.AreEqual("chapters", File.ReadAllText(Path.Combine(finalDirectory, "nested", "chapters.txt")));
        Assert.IsFalse(Directory.Exists(stagedDirectory));
    }

    [TestMethod]
    public void CleanupStagedDirectory_WhenParentsBecomeEmpty_PrunesTheTempTree()
    {
        var stagedDirectory = Path.Combine(_testRoot, ".flowencode-temp", "audio", "job");
        Directory.CreateDirectory(stagedDirectory);
        File.WriteAllText(Path.Combine(stagedDirectory, "partial.tmp"), "partial");

        ExecutionOutputStaging.CleanupStagedDirectory(stagedDirectory, emptyParentLevels: 2);

        Assert.IsFalse(Directory.Exists(stagedDirectory));
        Assert.IsFalse(Directory.Exists(Path.Combine(_testRoot, ".flowencode-temp", "audio")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_testRoot, ".flowencode-temp")));
    }
}
