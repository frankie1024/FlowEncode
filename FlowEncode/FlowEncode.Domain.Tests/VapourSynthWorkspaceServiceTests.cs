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
        var service = new VapourSynthWorkspaceService(paths);
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
    }

    [TestMethod]
    public async Task LoadSessionAsync_BacksUpBrokenSessionAndReturnsNull()
    {
        var paths = CreatePaths();
        var sessionPath = GetSessionPath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        await File.WriteAllTextAsync(sessionPath, "{not-json");

        var service = new VapourSynthWorkspaceService(paths);

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
