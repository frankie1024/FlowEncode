using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ManagedDirectoryInstallerTests
{
    [TestMethod]
    public void ReplaceDirectoryContents_WhenNewPackageOmitsOldFiles_RemovesStaleFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FlowEncodeInstallTest-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "source");
        var targetDirectory = Path.Combine(root, "target");

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(targetDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "encoder.exe"), "new");
            File.WriteAllText(Path.Combine(sourceDirectory, "runtime.dll"), "new");
            File.WriteAllText(Path.Combine(targetDirectory, "encoder.exe"), "old");
            File.WriteAllText(Path.Combine(targetDirectory, "stale.dll"), "old");

            ManagedDirectoryInstaller.ReplaceDirectoryContents(sourceDirectory, targetDirectory);

            Assert.AreEqual("new", File.ReadAllText(Path.Combine(targetDirectory, "encoder.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(targetDirectory, "runtime.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(targetDirectory, "stale.dll")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ReplaceDirectory_WhenStagingCannotBeMoved_RestoresPreviousDirectory()
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var target = Directory.CreateDirectory(Path.Combine(root.Path, "tool")).FullName;
        var oldExecutable = Path.Combine(target, "tool.exe");
        File.WriteAllText(oldExecutable, "old executable");

        Assert.Throws<IOException>(() =>
            ManagedDirectoryInstaller.ReplaceDirectory(target, staging => Directory.Delete(staging)));

        Assert.AreEqual("old executable", File.ReadAllText(oldExecutable));
        Assert.IsFalse(Directory.EnumerateDirectories(root.Path, ".tool.backup-*").Any());
    }

    [TestMethod]
    public void ReplaceDirectoryContents_WhenStagedValidationFails_PreservesExistingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FlowEncodeInstallRollbackTest-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "source");
        var targetDirectory = Path.Combine(root, "target");

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(targetDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "tool.exe"), "invalid-new");
            File.WriteAllText(Path.Combine(targetDirectory, "tool.exe"), "working-old");

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                ManagedDirectoryInstaller.ReplaceDirectoryContents(
                    sourceDirectory,
                    targetDirectory,
                    _ => throw new InvalidOperationException("validation failed")));

            Assert.AreEqual("working-old", File.ReadAllText(Path.Combine(targetDirectory, "tool.exe")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
