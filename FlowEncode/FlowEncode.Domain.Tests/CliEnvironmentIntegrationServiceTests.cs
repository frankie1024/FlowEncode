using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class CliEnvironmentIntegrationServiceTests
{
    [TestMethod]
    public void BuildUserPathSynchronizationPlan_ReplacesOwnedWorkspaceEntries()
    {
        var previousEntries = new[]
        {
            new ManagedPathEntry(@"D:\OldWorkspace\tools", WasPreexisting: false, OriginalIndex: 0)
        };

        var result = CliEnvironmentIntegrationService.BuildUserPathSynchronizationPlan(
            string.Join(Path.PathSeparator, @"D:\OldWorkspace\tools", @"C:\Windows\System32"),
            previousEntries,
            [@"E:\NewWorkspace\tools", @"E:\NewWorkspace\encoders\x264\x64"]);

        var segments = SplitPath(result.UserPath);
        CollectionAssert.AreEqual(
            new[]
            {
                @"E:\NewWorkspace\tools",
                @"E:\NewWorkspace\encoders\x264\x64",
                @"C:\Windows\System32"
            },
            segments);
        Assert.IsTrue(result.ManagedEntries.All(static entry => !entry.WasPreexisting));
    }

    [TestMethod]
    public void BuildUserPathSynchronizationPlan_MovesPreexistingEntryWithoutDuplicatingIt()
    {
        var result = CliEnvironmentIntegrationService.BuildUserPathSynchronizationPlan(
            string.Join(Path.PathSeparator, @"C:\Windows", @"F:\FlowEncode\tools", @"C:\Git"),
            [],
            [@"F:\FlowEncode\tools"]);

        var segments = SplitPath(result.UserPath);
        CollectionAssert.AreEqual(
            new[] { @"F:\FlowEncode\tools", @"C:\Windows", @"C:\Git" },
            segments);
        Assert.AreEqual(1, result.ManagedEntries.Count);
        Assert.IsTrue(result.ManagedEntries[0].WasPreexisting);
        Assert.AreEqual(1, result.ManagedEntries[0].OriginalIndex);
    }

    [TestMethod]
    public void BuildUserPathSynchronizationPlan_RestoresPreexistingEntryWhenNoLongerManaged()
    {
        var previousEntries = new[]
        {
            new ManagedPathEntry(@"F:\FlowEncode\tools", WasPreexisting: true, OriginalIndex: 1)
        };

        var result = CliEnvironmentIntegrationService.BuildUserPathSynchronizationPlan(
            string.Join(Path.PathSeparator, @"F:\FlowEncode\tools", @"C:\Windows", @"C:\Git"),
            previousEntries,
            [@"G:\FlowEncode\tools"]);

        var segments = SplitPath(result.UserPath);
        CollectionAssert.AreEqual(
            new[] { @"G:\FlowEncode\tools", @"C:\Windows", @"F:\FlowEncode\tools", @"C:\Git" },
            segments);
    }

    [TestMethod]
    public void BuildCleanShellPath_CombinesMachineAndUserPathWithoutDuplicates()
    {
        var result = CliEnvironmentIntegrationService.BuildCleanShellPath(
            string.Join(Path.PathSeparator, @"D:\FlowEncode\tools", @"C:\Windows"),
            string.Join(Path.PathSeparator, @"C:\Windows", @"C:\Windows\System32"));

        CollectionAssert.AreEqual(
            new[] { @"C:\Windows", @"C:\Windows\System32", @"D:\FlowEncode\tools" },
            SplitPath(result));
    }

    [TestMethod]
    public void BuildProcessPathSynchronizationValue_RemovesPreviousWorkspaceEntries()
    {
        var result = CliEnvironmentIntegrationService.BuildProcessPathSynchronizationValue(
            string.Join(Path.PathSeparator, @"D:\Old\tools", @"C:\Windows", @"D:\Old\encoders\x264\x64"),
            [@"E:\New\tools", @"E:\New\encoders\x264\x64"],
            [@"D:\Old\tools", @"D:\Old\encoders\x264\x64"]);

        CollectionAssert.AreEqual(
            new[] { @"E:\New\tools", @"E:\New\encoders\x264\x64", @"C:\Windows" },
            SplitPath(result));
    }

    [TestMethod]
    public void ShouldUpdateEnvironmentVariable_OnlyReturnsTrueForChangedValues()
    {
        Assert.IsFalse(CliEnvironmentIntegrationService.ShouldUpdateEnvironmentVariable(null, null));
        Assert.IsFalse(CliEnvironmentIntegrationService.ShouldUpdateEnvironmentVariable(@"F:\FlowEncode", @"F:\FlowEncode"));
        Assert.IsTrue(CliEnvironmentIntegrationService.ShouldUpdateEnvironmentVariable(null, @"F:\FlowEncode"));
        Assert.IsTrue(CliEnvironmentIntegrationService.ShouldUpdateEnvironmentVariable(@"F:\FlowEncode", @"G:\FlowEncode"));
        Assert.IsTrue(CliEnvironmentIntegrationService.ShouldUpdateEnvironmentVariable(@"F:\FlowEncode", null));
    }

    [TestMethod]
    public void ShouldWriteManifest_OnlyReturnsTrueForChangedContent()
    {
        const string manifest = "{\"schemaVersion\":1}";

        Assert.IsFalse(CliEnvironmentIntegrationService.ShouldWriteManifest(manifest, manifest));
        Assert.IsTrue(CliEnvironmentIntegrationService.ShouldWriteManifest(null, manifest));
        Assert.IsTrue(CliEnvironmentIntegrationService.ShouldWriteManifest(manifest, "{\"schemaVersion\":2}"));
    }

    [TestMethod]
    public void ComponentOwnership_PreexistingComponentRemainsUnownedAndTracksOwnedItems()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(CliEnvironmentIntegrationServiceTests), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LocalAppPaths(root, root, [Path.Combine(root, "workspace")]);
            var service = new CliEnvironmentIntegrationService(paths);
            var key = CliEnvironmentIntegrationService.GetPythonPackageComponentKey("vsrepo");

            service.RecordComponentOwnership(key, ownsComponent: false, @"C:\Python\python.exe", "1.0", ["preexisting"]);
            service.RecordComponentOwnership(key, ownsComponent: false, @"C:\Python\python.exe", "1.1", ["updated"]);

            var ownership = service.GetComponentOwnership(key);
            Assert.IsNotNull(ownership);
            Assert.IsFalse(ownership.OwnsComponent);
            Assert.AreEqual("1.1", ownership.InstalledVersion);
            CollectionAssert.AreEquivalent(new[] { "preexisting", "updated" }, ownership.OwnedItems.ToArray());
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
    public void ComponentOwnership_WhenInstallationPathChanges_DoesNotTransferOwnership()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(CliEnvironmentIntegrationServiceTests), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LocalAppPaths(root, root, [Path.Combine(root, "workspace")]);
            var service = new CliEnvironmentIntegrationService(paths);
            var key = CliEnvironmentIntegrationService.GetPythonPackageComponentKey("vsrepo");

            service.RecordComponentOwnership(key, ownsComponent: true, @"C:\Python312\python.exe", "1.0", ["owned-in-312"]);
            service.RecordComponentOwnership(key, ownsComponent: false, @"C:\Python314\python.exe", "1.0", ["preexisting-in-314"]);

            var ownership = service.GetComponentOwnership(key);
            Assert.IsNotNull(ownership);
            Assert.IsFalse(ownership.OwnsComponent);
            Assert.AreEqual(@"C:\Python314\python.exe", ownership.InstallationPath);
            CollectionAssert.AreEqual(new[] { "preexisting-in-314" }, ownership.OwnedItems.ToArray());
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
    public void RelocateComponentOwnership_MovesOnlyPathsCopiedToNewWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(CliEnvironmentIntegrationServiceTests), Guid.NewGuid().ToString("N"));
        var oldWorkspace = Path.Combine(root, "old");
        var newWorkspace = Path.Combine(root, "new");
        var oldExecutable = Path.Combine(oldWorkspace, "tools", "7zip", "7z.exe");
        var newExecutable = Path.Combine(newWorkspace, "tools", "7zip", "7z.exe");
        var missingOldItem = Path.Combine(oldWorkspace, "downloads", "old-installer.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newExecutable)!);
            File.WriteAllText(newExecutable, "copied");
            var ownership = new ManagedComponentOwnership(
                true,
                oldExecutable,
                "26.02",
                [oldExecutable, missingOldItem, @"C:\Python\python.exe"],
                DateTimeOffset.UtcNow.AddDays(-1));

            var relocated = CliEnvironmentIntegrationService.RelocateComponentOwnership(
                ownership,
                oldWorkspace,
                newWorkspace);

            Assert.AreEqual(newExecutable, relocated.InstallationPath);
            CollectionAssert.AreEqual(
                new[] { newExecutable, missingOldItem, @"C:\Python\python.exe" },
                relocated.OwnedItems.ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string[] SplitPath(string value)
        => value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
