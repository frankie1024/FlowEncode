using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ManagedFileInstallerTests
{
    [TestMethod]
    public async Task ReplaceFileAsync_WhenTargetExists_ReplacesContentsWithoutLeavingArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(ManagedFileInstallerTests), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var sourcePath = Path.Combine(root, "source.exe");
            var targetPath = Path.Combine(root, "target.exe");
            await File.WriteAllTextAsync(sourcePath, "new");
            await File.WriteAllTextAsync(targetPath, "old");

            await ManagedFileInstaller.ReplaceFileAsync(sourcePath, targetPath);

            Assert.AreEqual("new", await File.ReadAllTextAsync(targetPath));
            Assert.AreEqual(0, Directory.EnumerateFiles(root, ".target.exe.*").Count());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ReplaceFileAsync_WhenCancelledBeforeCopy_PreservesExistingTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(ManagedFileInstallerTests), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var sourcePath = Path.Combine(root, "source.exe");
            var targetPath = Path.Combine(root, "target.exe");
            await File.WriteAllTextAsync(sourcePath, "new");
            await File.WriteAllTextAsync(targetPath, "old");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
                ManagedFileInstaller.ReplaceFileAsync(sourcePath, targetPath, cancellation.Token));

            Assert.AreEqual("old", await File.ReadAllTextAsync(targetPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
