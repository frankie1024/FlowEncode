using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LocalAppSettingsServiceTests
{
    private string? _settingsRoot;

    [TestInitialize]
    public void SetUp()
    {
        _settingsRoot = Path.Combine(Path.GetTempPath(), "FlowEncodeSettingsTests", Guid.NewGuid().ToString("N"));
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
    public void Save_WritesSettingsAtomicallyWithoutLeavingTempFile()
    {
        var paths = CreatePaths();
        var service = new LocalAppSettingsService(paths);
        var settings = AppSettings.Default with
        {
            WorkspaceRootPath = @"D:\workspace\flowencode",
            AutoCheckUpdatesOnStartup = false
        };

        service.Save(settings);

        Assert.IsTrue(File.Exists(paths.SettingsPath));
        var loaded = new LocalAppSettingsService(paths).Load();
        Assert.AreEqual(settings.WorkspaceRootPath, loaded.WorkspaceRootPath);
        Assert.AreEqual(settings.AutoCheckUpdatesOnStartup, loaded.AutoCheckUpdatesOnStartup);
        Assert.IsFalse(File.Exists(paths.SettingsPath + ".tmp"));
        AssertNoTemporaryFiles(paths.SettingsPath);
    }

    [TestMethod]
    public void Save_WhenExistingSettingsFileIsLocked_PreservesExistingSettings()
    {
        var paths = CreatePaths();
        var service = new LocalAppSettingsService(paths);
        var oldSettings = AppSettings.Default with
        {
            WorkspaceRootPath = @"D:\workspace\old"
        };
        var newSettings = oldSettings with
        {
            WorkspaceRootPath = @"D:\workspace\new"
        };
        service.Save(oldSettings);

        Exception? exception = null;
        using (File.Open(paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                service.Save(newSettings);
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
        AssertNoTemporaryFiles(paths.SettingsPath);
        var loaded = new LocalAppSettingsService(paths).Load();
        Assert.AreEqual(oldSettings.WorkspaceRootPath, loaded.WorkspaceRootPath);
        Assert.AreNotEqual(newSettings.WorkspaceRootPath, loaded.WorkspaceRootPath);
    }

    [TestMethod]
    public void Load_BacksUpBrokenSettingsAndFallsBackToDefault()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.SettingsRootPath);
        File.WriteAllText(paths.SettingsPath, "{not-json");

        var service = new LocalAppSettingsService(paths);

        var loaded = service.Load();
        var recoveryInfo = service.ConsumeLastLoadRecoveryInfo();
        var backupFiles = Directory.GetFiles(paths.SettingsRootPath, "settings.json.broken-*");

        Assert.AreEqual(AppSettings.Default, loaded);
        Assert.IsNotNull(recoveryInfo);
        Assert.AreEqual(1, backupFiles.Length);
        Assert.AreEqual(backupFiles[0], recoveryInfo.BackupPath);
        Assert.IsFalse(File.Exists(paths.SettingsPath));
        Assert.IsNull(service.ConsumeLastLoadRecoveryInfo());
    }

    [TestMethod]
    public void Load_WhenAnotherProcessSavedSettings_ReturnsTheNewSettings()
    {
        var paths = CreatePaths();
        var desktop = new LocalAppSettingsService(paths);
        var mcp = new LocalAppSettingsService(paths);
        desktop.Save(AppSettings.Default with { MaxConcurrentEncodingJobs = 1 });
        Assert.AreEqual(1, mcp.Load().MaxConcurrentEncodingJobs);

        desktop.Save(AppSettings.Default with { MaxConcurrentEncodingJobs = 3, QueueCompletionAction = QueueCompletionAction.Sleep });
        File.SetLastWriteTimeUtc(paths.SettingsPath, DateTime.UtcNow.AddSeconds(5));

        var reloaded = mcp.Load();
        Assert.AreEqual(3, reloaded.MaxConcurrentEncodingJobs);
        Assert.AreEqual(QueueCompletionAction.Sleep, reloaded.QueueCompletionAction);
    }

    [TestMethod]
    public void Load_WhenFileIsUnchanged_ReturnsCachedInstance()
    {
        var paths = CreatePaths();
        var service = new LocalAppSettingsService(paths);
        service.Save(AppSettings.Default with { MaxConcurrentEncodingJobs = 2 });

        Assert.AreSame(service.Load(), service.Load());
    }

    [TestMethod]
    public void Load_WhenReloadHitsAPartialWrite_KeepsLastGoodSettingsWithoutBackingUpTheFile()
    {
        var paths = CreatePaths();
        var service = new LocalAppSettingsService(paths);
        service.Save(AppSettings.Default with { MaxConcurrentEncodingJobs = 4 });
        Assert.AreEqual(4, service.Load().MaxConcurrentEncodingJobs);

        File.WriteAllText(paths.SettingsPath, "{\"maxConcurrentEncodingJobs\": ");
        File.SetLastWriteTimeUtc(paths.SettingsPath, DateTime.UtcNow.AddSeconds(5));

        Assert.AreEqual(4, service.Load().MaxConcurrentEncodingJobs);
        Assert.IsTrue(File.Exists(paths.SettingsPath));
        Assert.AreEqual(0, Directory.GetFiles(paths.SettingsRootPath, "settings.json.broken-*").Length);
        Assert.IsNull(service.ConsumeLastLoadRecoveryInfo());
    }

    [TestMethod]
    public void Load_WhenFileDisappearsAfterLoading_KeepsCachedSettings()
    {
        var paths = CreatePaths();
        var service = new LocalAppSettingsService(paths);
        service.Save(AppSettings.Default with { MaxConcurrentEncodingJobs = 2 });
        Assert.AreEqual(2, service.Load().MaxConcurrentEncodingJobs);

        File.Delete(paths.SettingsPath);

        Assert.AreEqual(2, service.Load().MaxConcurrentEncodingJobs);
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
