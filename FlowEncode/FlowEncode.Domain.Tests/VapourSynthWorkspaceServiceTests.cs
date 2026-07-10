using FlowEncode.Application;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class VapourSynthWorkspaceServiceTests
{
    private string? _workspaceRoot;

    [TestInitialize]
    public void SetUp()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "FlowEncodeWorkspaceSessionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (!string.IsNullOrWhiteSpace(_workspaceRoot) && Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveSessionAsync_WritesSessionAtomicallyWithoutLeavingTempFile()
    {
        var paths = CreatePaths();
        using var service = new VapourSynthWorkspaceService(paths);
        var session = CreateSession();

        await service.SaveSessionAsync(session);

        var sessionPath = GetSessionPath(paths);
        var loaded = await service.LoadSessionAsync();

        Assert.IsTrue(File.Exists(sessionPath));
        Assert.IsNotNull(loaded);
        Assert.AreEqual(session.ActiveTabId, loaded.ActiveTabId);
        Assert.AreEqual(session.LeftTabId, loaded.LeftTabId);
        Assert.AreEqual(session.RightTabId, loaded.RightTabId);
        Assert.AreEqual(session.IsCompareMode, loaded.IsCompareMode);
        Assert.AreEqual(session.ActivePane, loaded.ActivePane);
        CollectionAssert.AreEqual(session.Tabs.ToArray(), loaded.Tabs.ToArray());
        Assert.IsFalse(File.Exists(sessionPath + ".tmp"));
        AssertNoTemporaryFiles(sessionPath);
    }

    [TestMethod]
    public async Task SaveDocumentAsync_ReplacesDocumentAtomicallyAndAllowsEmptyContent()
    {
        var paths = CreatePaths();
        using var service = new VapourSynthWorkspaceService(paths);
        var documentPath = Path.Combine(_workspaceRoot!, "scripts", "sample.vpy");
        Directory.CreateDirectory(Path.GetDirectoryName(documentPath)!);
        await File.WriteAllTextAsync(documentPath, "old content");

        var saved = await service.SaveDocumentAsync(documentPath, string.Empty);

        Assert.AreEqual(string.Empty, saved.Content);
        Assert.AreEqual(string.Empty, await File.ReadAllTextAsync(documentPath));
        AssertNoTemporaryFiles(documentPath);
    }

    [TestMethod]
    public async Task SaveDocumentAsync_WhenDocumentIsLocked_PreservesExistingContent()
    {
        var paths = CreatePaths();
        using var service = new VapourSynthWorkspaceService(paths);
        var documentPath = Path.Combine(_workspaceRoot!, "locked.vpy");
        await File.WriteAllTextAsync(documentPath, "old content");

        Exception? exception = null;
        using (File.Open(documentPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                await service.SaveDocumentAsync(documentPath, "new content");
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        }

        Assert.IsNotNull(exception);
        Assert.IsTrue(exception is IOException or UnauthorizedAccessException);
        Assert.AreEqual("old content", await File.ReadAllTextAsync(documentPath));
        AssertNoTemporaryFiles(documentPath);
    }

    [TestMethod]
    public async Task SaveSessionAsync_WhenExistingSessionFileIsLocked_PreservesExistingSession()
    {
        var paths = CreatePaths();
        using var service = new VapourSynthWorkspaceService(paths);
        var oldSession = CreateSession();
        var newSession = new VapourSynthWorkspaceSession(
            oldSession.Tabs,
            oldSession.ActiveTabId,
            oldSession.RightTabId,
            oldSession.LeftTabId,
            false,
            oldSession.ActivePane);
        await service.SaveSessionAsync(oldSession);
        var sessionPath = GetSessionPath(paths);

        Exception? exception = null;
        using (File.Open(sessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                await service.SaveSessionAsync(newSession);
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
        AssertNoTemporaryFiles(sessionPath);

        using var reloadedService = new VapourSynthWorkspaceService(paths);
        var loaded = await reloadedService.LoadSessionAsync();
        Assert.IsNotNull(loaded);
        Assert.AreEqual(oldSession.LeftTabId, loaded.LeftTabId);
        Assert.AreEqual(oldSession.RightTabId, loaded.RightTabId);
        Assert.AreEqual(oldSession.IsCompareMode, loaded.IsCompareMode);
    }

    [TestMethod]
    public async Task LoadSessionAsync_BacksUpBrokenSessionAndReturnsNull()
    {
        var paths = CreatePaths();
        var sessionPath = GetSessionPath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        await File.WriteAllTextAsync(sessionPath, "{not-json");

        using var service = new VapourSynthWorkspaceService(paths);

        var loaded = await service.LoadSessionAsync();
        var backupFiles = Directory.GetFiles(Path.GetDirectoryName(sessionPath)!, "editor-session.json.broken-*");

        Assert.IsNull(loaded);
        Assert.AreEqual(1, backupFiles.Length);
        Assert.IsFalse(File.Exists(sessionPath));
    }

    private LocalAppPaths CreatePaths()
    {
        return new LocalAppPaths(_workspaceRoot!, _workspaceRoot!);
    }

    private static void AssertNoTemporaryFiles(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath)!;
        var pattern = $".{Path.GetFileName(targetPath)}.*.tmp";
        Assert.AreEqual(0, Directory.GetFiles(directory, pattern).Length);
    }

    private static string GetSessionPath(LocalAppPaths paths)
    {
        return Path.Combine(paths.DataRootPath, "vapoursynth-workspace", "editor-session.json");
    }

    private static VapourSynthWorkspaceSession CreateSession()
    {
        return new VapourSynthWorkspaceSession(
            [
                new VapourSynthWorkspaceTabSession(
                    "tab-left",
                    @"D:\scripts\left.vpy",
                    "clip = core.ffms2.Source('left.mkv')\n",
                    "clip = core.ffms2.Source('left.mkv')\n",
                    false,
                    true,
                    "ready",
                    "[INFO] ok",
                    3,
                    5,
                    8,
                    42),
                new VapourSynthWorkspaceTabSession(
                    "tab-right",
                    null,
                    "clip = core.std.BlankClip()\n",
                    string.Empty,
                    true,
                    false,
                    "draft",
                    "[WARN] unsaved",
                    1,
                    1,
                    1,
                    29)
            ],
            "tab-right",
            "tab-left",
            "tab-right",
            true,
            "Right");
    }
}
