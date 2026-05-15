using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LocalSetupGuideCacheServiceTests
{
    private string? _settingsRoot;

    [TestInitialize]
    public void SetUp()
    {
        _settingsRoot = Path.Combine(Path.GetTempPath(), "FlowEncodeSetupGuideCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_settingsRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (!string.IsNullOrWhiteSpace(_settingsRoot) && Directory.Exists(_settingsRoot))
        {
            Directory.Delete(_settingsRoot, recursive: true);
        }
    }

    [TestMethod]
    public void Save_WritesCacheWithoutLeavingTempFile()
    {
        var paths = CreatePaths();
        var service = new LocalSetupGuideCacheService(paths);
        var snapshot = CreateSnapshot();

        service.Save(snapshot);

        var loaded = new LocalSetupGuideCacheService(paths).Load();
        Assert.IsNotNull(loaded);
        Assert.AreEqual(snapshot.SavedAt, loaded.SavedAt);
        Assert.AreEqual(snapshot.StatusReport!.Dependencies[0].Kind, loaded.StatusReport!.Dependencies[0].Kind);
        AssertNoTemporaryFiles(paths.SetupGuideCachePath);
    }

    [TestMethod]
    public void Save_WhenExistingCacheFileIsLocked_PreservesExistingCache()
    {
        var paths = CreatePaths();
        var service = new LocalSetupGuideCacheService(paths);
        var oldSnapshot = CreateSnapshot();
        var newSnapshot = oldSnapshot with
        {
            SavedAt = oldSnapshot.SavedAt.AddMinutes(5)
        };
        service.Save(oldSnapshot);

        Exception? exception = null;
        using (File.Open(paths.SetupGuideCachePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                service.Save(newSnapshot);
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
        AssertNoTemporaryFiles(paths.SetupGuideCachePath);
        var loaded = new LocalSetupGuideCacheService(paths).Load();
        Assert.IsNotNull(loaded);
        Assert.AreEqual(oldSnapshot.SavedAt, loaded.SavedAt);
        Assert.AreNotEqual(newSnapshot.SavedAt, loaded.SavedAt);
        Assert.AreEqual(oldSnapshot.StatusReport!.Dependencies[0].Kind, loaded.StatusReport!.Dependencies[0].Kind);
    }

    private static SetupGuideCacheSnapshot CreateSnapshot()
    {
        var checkedAt = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);
        return new SetupGuideCacheSnapshot(
            SetupGuideCacheSnapshot.CurrentSchemaVersion,
            checkedAt,
            checkedAt,
            checkedAt,
            new SetupGuideCacheStatusReport(
                checkedAt,
                [
                    new SetupGuideCacheDependencyStatus(
                        SetupDependencyKind.FfmpegBundle,
                        ReadinessState.Ready,
                        "7.1",
                        "7.1",
                        false,
                        @"D:\tools\ffmpeg.exe",
                        true,
                        true,
                        "ready")
                ]));
    }

    private void AssertNoTemporaryFiles(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath)!;
        var pattern = $".{Path.GetFileName(targetPath)}.*.tmp";
        Assert.AreEqual(0, Directory.GetFiles(directory, pattern).Length);
    }

    private LocalAppPaths CreatePaths()
    {
        return new LocalAppPaths(_settingsRoot!, _settingsRoot!);
    }
}
