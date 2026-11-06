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
    public void FinalizeFile_WhenReplacementIsDisabled_PreservesExistingAndStagedOutputs()
    {
        var finalPath = Path.Combine(_testRoot, "movie.mkv");
        var jobId = Guid.NewGuid();
        var stagedPath = ExecutionOutputStaging.CreateStagedFilePath(
            Path.Combine(_testRoot, "staging"), finalPath, jobId);
        File.WriteAllText(stagedPath, "new-content");
        // Simulate another program creating the destination after encoding has begun.
        File.WriteAllText(finalPath, "other-content");

        Assert.ThrowsExactly<IOException>(() =>
            ExecutionOutputStaging.FinalizeFile(stagedPath, finalPath, jobId, overwrite: false));

        Assert.AreEqual("other-content", File.ReadAllText(finalPath));
        Assert.AreEqual("new-content", File.ReadAllText(stagedPath));
    }

    [TestMethod]
    public void FinalizeFile_WhenReplacementIsDisabledAndDestinationIsAbsent_MovesOutput()
    {
        var finalPath = Path.Combine(_testRoot, "movie.mkv");
        var jobId = Guid.NewGuid();
        var stagedPath = ExecutionOutputStaging.CreateStagedFilePath(
            Path.Combine(_testRoot, "staging"), finalPath, jobId);
        File.WriteAllText(stagedPath, "encoded");

        ExecutionOutputStaging.FinalizeFile(stagedPath, finalPath, jobId, overwrite: false);

        Assert.AreEqual("encoded", File.ReadAllText(finalPath));
        Assert.IsFalse(File.Exists(stagedPath));
    }

    [TestMethod]
    public void TryPreserveStagedFile_WhenRecoveryDestinationIsUnavailable_RetainsOriginalAndReportsPath()
    {
        var finalPath = Path.Combine(_testRoot, "missing-directory", "movie.mkv");
        var jobId = Guid.NewGuid();
        var stagedPath = ExecutionOutputStaging.CreateStagedFilePath(
            Path.Combine(_testRoot, "staging"), finalPath, jobId);
        File.WriteAllText(stagedPath, "encoded");
        var diagnostics = new List<string>();

        var preservedPath = ExecutionOutputStaging.TryPreserveStagedFile(stagedPath, finalPath, jobId, diagnostics.Add);

        Assert.IsNotNull(preservedPath);
        Assert.AreEqual(stagedPath, preservedPath);
        Assert.AreEqual("encoded", File.ReadAllText(preservedPath));
        Assert.AreEqual(1, diagnostics.Count);
        StringAssert.Contains(diagnostics[0], stagedPath);
    }

    [TestMethod]
    public void TryPreserveStagedFile_MovesStagedOutputNextToFinalPath()
    {
        var finalPath = Path.Combine(_testRoot, "movie.mkv");
        var stagingDirectory = Path.Combine(_testRoot, ".flowencode-temp", "job");
        var jobId = Guid.NewGuid();
        var stagedPath = ExecutionOutputStaging.CreateStagedFilePath(stagingDirectory, finalPath, jobId);
        File.WriteAllText(stagedPath, "encoded");

        var preservedPath = ExecutionOutputStaging.TryPreserveStagedFile(stagedPath, finalPath, jobId);

        Assert.IsNotNull(preservedPath);
        Assert.AreEqual(_testRoot, Path.GetDirectoryName(preservedPath));
        Assert.AreEqual(".mkv", Path.GetExtension(preservedPath));
        Assert.AreEqual("encoded", File.ReadAllText(preservedPath));
        Assert.IsFalse(File.Exists(stagedPath));
    }

    [TestMethod]
    public void TryPreserveStagedFile_WhenStagedOutputIsMissing_ReturnsNull()
    {
        var finalPath = Path.Combine(_testRoot, "movie.mkv");

        var preservedPath = ExecutionOutputStaging.TryPreserveStagedFile(
            Path.Combine(_testRoot, "missing.tmp.mkv"),
            finalPath,
            Guid.NewGuid());

        Assert.IsNull(preservedPath);
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
