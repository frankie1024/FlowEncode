using System.Text;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class PersistentFileWriterTests
{
    private string? _testRoot;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "FlowEncodePersistentFileWriterTests", Guid.NewGuid().ToString("N"));
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
    public void WriteAllText_WhenTargetExists_ReplacesFileWithoutLeavingTempFile()
    {
        var targetPath = Path.Combine(_testRoot!, "settings.json");
        File.WriteAllText(targetPath, "old", Encoding.UTF8);

        PersistentFileWriter.WriteAllText(targetPath, "new", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.AreEqual("new", File.ReadAllText(targetPath, Encoding.UTF8));
        AssertNoTemporaryFiles(targetPath);
    }

    [TestMethod]
    public void WriteAllText_WhenTargetIsLocked_PreservesExistingFileAndDeletesTempFile()
    {
        var targetPath = Path.Combine(_testRoot!, "settings.json");
        File.WriteAllText(targetPath, "old", Encoding.UTF8);

        Exception? exception = null;
        using (File.Open(targetPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                PersistentFileWriter.WriteAllText(targetPath, "new", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        }

        Assert.IsNotNull(exception);
        Assert.IsTrue(
            exception is IOException or UnauthorizedAccessException,
            $"Unexpected exception type: {exception.GetType().FullName}");
        Assert.AreEqual("old", File.ReadAllText(targetPath, Encoding.UTF8));
        AssertNoTemporaryFiles(targetPath);
    }

    [TestMethod]
    public async Task WriteAllTextAsync_WhenCancelledBeforeReplace_PreservesExistingFileAndDeletesTempFile()
    {
        var targetPath = Path.Combine(_testRoot!, "session.json");
        File.WriteAllText(targetPath, "old", Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Exception? exception = null;
        try
        {
            await PersistentFileWriter.WriteAllTextAsync(
                targetPath,
                "new",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellation.Token);
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        Assert.IsTrue(
            exception is OperationCanceledException,
            $"Unexpected exception type: {exception?.GetType().FullName ?? "<null>"}");
        Assert.AreEqual("old", File.ReadAllText(targetPath, Encoding.UTF8));
        AssertNoTemporaryFiles(targetPath);
    }

    private void AssertNoTemporaryFiles(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath)!;
        var pattern = $".{Path.GetFileName(targetPath)}.*.tmp";
        Assert.AreEqual(0, Directory.GetFiles(directory, pattern).Length);
    }
}
