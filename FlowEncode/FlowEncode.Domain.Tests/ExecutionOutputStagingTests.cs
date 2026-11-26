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
    public void FinalizeFiles_WhenALaterReplacementFails_RestoresPreviousFilesAndStaging()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("This test requires Windows file sharing.");

        var stage = Directory.CreateDirectory(Path.Combine(_testRoot, "stage")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(_testRoot, "output")).FullName;
        var newFile = (Source: Path.Combine(stage, "new.ac3"), Target: Path.Combine(output, "new.ac3"));
        var replaced = (Source: Path.Combine(stage, "existing.ac3"), Target: Path.Combine(output, "existing.ac3"));
        var locked = (Source: Path.Combine(stage, "locked.sup"), Target: Path.Combine(output, "locked.sup"));
        File.WriteAllText(newFile.Source, "new track");
        File.WriteAllText(replaced.Source, "new audio");
        File.WriteAllText(replaced.Target, "old audio");
        File.WriteAllText(locked.Source, "new subtitle");
        File.WriteAllText(locked.Target, "old subtitle");
        using var handle = File.Open(locked.Target, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.ThrowsExactly<IOException>(() => ExecutionOutputStaging.FinalizeFiles([newFile, replaced, locked], overwrite: true));

        Assert.IsFalse(File.Exists(newFile.Target));
        Assert.AreEqual("new track", File.ReadAllText(newFile.Source));
        Assert.AreEqual("new audio", File.ReadAllText(replaced.Source));
        Assert.AreEqual("old audio", File.ReadAllText(replaced.Target));
        Assert.AreEqual("old subtitle", File.ReadAllText(locked.Target));
        Assert.AreEqual("new subtitle", File.ReadAllText(locked.Source));
        Assert.IsFalse(Directory.EnumerateFiles(output, "*.backup.tmp*").Any());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MoveBack_WhenPublishedTargetIsEditedOrReplaced_LeavesItInPlace(bool replace)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("This test requires Windows file identity.");

        var staged = Path.Combine(_testRoot, "stage.ac3");
        var target = Path.Combine(_testRoot, "target.ac3");
        File.WriteAllText(staged, "original");
        File.SetLastWriteTimeUtc(staged, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var published = PublishedOutputFile.Capture(staged);
        File.Move(staged, target);
        if (replace)
        {
            var otherFile = Path.Combine(_testRoot, "other.ac3");
            File.WriteAllText(otherFile, "external");
            File.SetLastWriteTimeUtc(otherFile, published.LastWriteTimeUtc);
            File.Move(otherFile, target, overwrite: true);
        }
        else
        {
            File.WriteAllText(target, "external");
        }

        var error = Assert.ThrowsExactly<IOException>(() => published.MoveBack(target, staged));

        StringAssert.Contains(error.Message, "changed after publication");
        Assert.AreEqual("external", File.ReadAllText(target));
        Assert.IsFalse(File.Exists(staged));
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
