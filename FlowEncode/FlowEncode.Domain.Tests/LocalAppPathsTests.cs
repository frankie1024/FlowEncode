using System.Text.Json;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LocalAppPathsTests
{
    private string? _testRoot;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "FlowEncodeLocalAppPathsTests", Guid.NewGuid().ToString("N"));
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
    public void Constructor_PreservesConfiguredWorkspacePathWhenStartupFallsBack()
    {
        var configuredWorkspacePath = Path.Combine(_testRoot!, "configured-workspace");
        var fallbackWorkspacePath = Path.Combine(_testRoot!, "fallback-workspace");
        var localApplicationDataPath = Path.Combine(_testRoot!, "local-state");
        var installRootPath = Path.Combine(_testRoot!, "install-root");

        Directory.CreateDirectory(localApplicationDataPath);
        Directory.CreateDirectory(installRootPath);
        File.WriteAllText(configuredWorkspacePath, "not-a-directory");
        WriteSettings(localApplicationDataPath, configuredWorkspacePath);

        var paths = new LocalAppPaths(localApplicationDataPath, installRootPath, [fallbackWorkspacePath]);

        var recoveryInfo = paths.ConsumeStartupWorkspaceRecoveryInfo();

        Assert.AreEqual(Path.GetFullPath(configuredWorkspacePath), paths.ConfiguredWorkspaceRootPath);
        Assert.AreEqual(Path.GetFullPath(fallbackWorkspacePath), paths.RootPath);
        Assert.IsNotNull(recoveryInfo);
        Assert.AreEqual(paths.ConfiguredWorkspaceRootPath, recoveryInfo.ConfiguredPath);
        Assert.AreEqual(paths.RootPath, recoveryInfo.ActivePath);
        Assert.IsNull(paths.ConsumeStartupWorkspaceRecoveryInfo());
    }

    [TestMethod]
    public void Constructor_PreservesInvalidConfiguredWorkspacePathWhenStartupFallsBack()
    {
        var configuredWorkspacePath = @"C:\invalid|workspace";
        var fallbackWorkspacePath = Path.Combine(_testRoot!, "fallback-workspace");
        var localApplicationDataPath = Path.Combine(_testRoot!, "local-state");
        var installRootPath = Path.Combine(_testRoot!, "install-root");

        Directory.CreateDirectory(localApplicationDataPath);
        Directory.CreateDirectory(installRootPath);
        WriteSettings(localApplicationDataPath, configuredWorkspacePath);

        var paths = new LocalAppPaths(localApplicationDataPath, installRootPath, [fallbackWorkspacePath]);

        var recoveryInfo = paths.ConsumeStartupWorkspaceRecoveryInfo();

        Assert.AreEqual(configuredWorkspacePath, paths.ConfiguredWorkspaceRootPath);
        Assert.AreEqual(Path.GetFullPath(fallbackWorkspacePath), paths.RootPath);
        Assert.IsNotNull(recoveryInfo);
        Assert.AreEqual(configuredWorkspacePath, recoveryInfo.ConfiguredPath);
        Assert.AreEqual(paths.RootPath, recoveryInfo.ActivePath);
    }

    [TestMethod]
    public void PrepareWorkspaceRootChange_ThrowsWhenTargetContainsConflictingFile()
    {
        var paths = CreatePaths("source-workspace");
        var sourceFilePath = Path.Combine(paths.ToolsRootPath, "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFilePath)!);
        File.WriteAllText(sourceFilePath, "new-tool");

        var targetWorkspacePath = Path.Combine(_testRoot!, "target-workspace");
        var targetFilePath = Path.Combine(targetWorkspacePath, "tools", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);
        File.WriteAllText(targetFilePath, "old-tool");

        WorkspaceRootConflictException? exception = null;

        try
        {
            paths.PrepareWorkspaceRootChange(targetWorkspacePath);
        }
        catch (WorkspaceRootConflictException ex)
        {
            exception = ex;
        }

        Assert.IsNotNull(exception);
        Assert.AreEqual(Path.Combine("tools", "ffmpeg.exe"), exception.RelativePath);
        Assert.AreEqual("old-tool", File.ReadAllText(targetFilePath));
    }

    [TestMethod]
    public void PrepareWorkspaceRootChange_DoesNotCopyEarlierRootsWhenLaterRootConflicts()
    {
        var paths = CreatePaths("source-workspace");
        var sourceDownloadPath = Path.Combine(paths.DownloadsRootPath, "cache.bin");
        var sourceToolPath = Path.Combine(paths.ToolsRootPath, "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDownloadPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceToolPath)!);
        File.WriteAllText(sourceDownloadPath, "download-cache");
        File.WriteAllText(sourceToolPath, "new-tool");

        var targetWorkspacePath = Path.Combine(_testRoot!, "target-workspace");
        var targetToolPath = Path.Combine(targetWorkspacePath, "tools", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(targetToolPath)!);
        File.WriteAllText(targetToolPath, "old-tool");

        Assert.ThrowsExactly<WorkspaceRootConflictException>(() => paths.PrepareWorkspaceRootChange(targetWorkspacePath));
        Assert.IsFalse(File.Exists(Path.Combine(targetWorkspacePath, "downloads", "cache.bin")));
        Assert.AreEqual("old-tool", File.ReadAllText(targetToolPath));
    }

    [TestMethod]
    public void PrepareWorkspaceRootChange_CopiesMissingFilesWhenExistingContentMatches()
    {
        var paths = CreatePaths("source-workspace");
        var sourceToolPath = Path.Combine(paths.ToolsRootPath, "ffmpeg.exe");
        var sourceTemplatePath = Path.Combine(paths.WorkspaceTemplatesRootPath, "profile.profile");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceToolPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceTemplatePath)!);
        File.WriteAllText(sourceToolPath, "same-tool");
        File.WriteAllText(sourceTemplatePath, "template");

        var targetWorkspacePath = Path.Combine(_testRoot!, "target-workspace");
        var targetToolPath = Path.Combine(targetWorkspacePath, "tools", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(targetToolPath)!);
        File.WriteAllText(targetToolPath, "same-tool");

        paths.PrepareWorkspaceRootChange(targetWorkspacePath);

        var copiedTemplatePath = Path.Combine(targetWorkspacePath, "Templates", "profile.profile");
        Assert.AreEqual("same-tool", File.ReadAllText(targetToolPath));
        Assert.IsTrue(File.Exists(copiedTemplatePath));
        Assert.AreEqual("template", File.ReadAllText(copiedTemplatePath));
    }

    [TestMethod]
    public void ActivateWorkspaceRootPath_SwitchesAllWorkspaceDerivedPathsImmediately()
    {
        var paths = CreatePaths("source-workspace");
        var targetWorkspacePath = Path.Combine(_testRoot!, "target-workspace");
        var originalRootPath = paths.RootPath;

        paths.PrepareWorkspaceRootChange(targetWorkspacePath);

        Assert.AreEqual(originalRootPath, paths.RootPath);
        Assert.AreEqual(Path.Combine(originalRootPath, "downloads"), paths.DownloadsRootPath);
        Assert.AreEqual(Path.Combine(originalRootPath, "encoders"), paths.ToolsetRootPath);
        Assert.AreEqual(Path.Combine(originalRootPath, "tools"), paths.ToolsRootPath);
        Assert.AreEqual(Path.Combine(originalRootPath, "Templates"), paths.WorkspaceTemplatesRootPath);

        paths.ActivateWorkspaceRootPath(targetWorkspacePath);

        var normalizedTargetPath = Path.GetFullPath(targetWorkspacePath);
        Assert.AreEqual(normalizedTargetPath, paths.RootPath);
        Assert.AreEqual(normalizedTargetPath, paths.WorkspaceRootPath);
        Assert.AreEqual(normalizedTargetPath, paths.ConfiguredWorkspaceRootPath);
        Assert.AreEqual(Path.Combine(normalizedTargetPath, "downloads"), paths.DownloadsRootPath);
        Assert.AreEqual(Path.Combine(normalizedTargetPath, "encoders"), paths.ToolsetRootPath);
        Assert.AreEqual(Path.Combine(normalizedTargetPath, "encoders"), paths.ToolDataRootPath);
        Assert.AreEqual(Path.Combine(normalizedTargetPath, "tools"), paths.ToolsRootPath);
        Assert.AreEqual(Path.Combine(normalizedTargetPath, "Templates"), paths.WorkspaceTemplatesRootPath);
    }

    [TestMethod]
    public void GetExpectedFileName_UsesUpstreamExecutableNames()
    {
        Assert.AreEqual("x264.exe", LocalAppPaths.GetExpectedFileName(EncoderKind.X264, EncoderArchitecture.X64));
        Assert.AreEqual("x265.exe", LocalAppPaths.GetExpectedFileName(EncoderKind.X265, EncoderArchitecture.X64));
        Assert.AreEqual("SvtAv1EncApp.exe", LocalAppPaths.GetExpectedFileName(EncoderKind.SvtAv1, EncoderArchitecture.X64));
    }

    [TestMethod]
    public void Constructor_MigratesLegacyEncoderBinaryToUpstreamName()
    {
        var workspaceRootPath = Path.Combine(_testRoot!, "workspace");
        var localApplicationDataPath = Path.Combine(_testRoot!, "local-state");
        var installRootPath = Path.Combine(_testRoot!, "install-root");
        var encoderDirectory = Path.Combine(workspaceRootPath, "encoders", "x264", "x64");
        var legacyPath = Path.Combine(encoderDirectory, "x264_x64.exe");
        Directory.CreateDirectory(encoderDirectory);
        Directory.CreateDirectory(installRootPath);
        File.WriteAllText(legacyPath, "legacy-binary");
        WriteSettings(localApplicationDataPath, workspaceRootPath);

        var paths = new LocalAppPaths(localApplicationDataPath, installRootPath);

        var canonicalPath = paths.GetBinaryPath(EncoderKind.X264, EncoderArchitecture.X64);
        Assert.AreEqual("x264.exe", Path.GetFileName(canonicalPath));
        Assert.AreEqual("legacy-binary", File.ReadAllText(canonicalPath));
        Assert.IsFalse(File.Exists(legacyPath));
    }

    [TestMethod]
    public void Constructor_WhenLegacyEncoderIsNewer_ReplacesStaleCanonicalBinary()
    {
        var workspaceRootPath = Path.Combine(_testRoot!, "workspace");
        var localApplicationDataPath = Path.Combine(_testRoot!, "local-state");
        var installRootPath = Path.Combine(_testRoot!, "install-root");
        var encoderDirectory = Path.Combine(workspaceRootPath, "encoders", "x265", "x64");
        var canonicalPath = Path.Combine(encoderDirectory, "x265.exe");
        var legacyPath = Path.Combine(encoderDirectory, "x265_x64.exe");
        Directory.CreateDirectory(encoderDirectory);
        Directory.CreateDirectory(installRootPath);
        File.WriteAllText(canonicalPath, "stale-canonical");
        File.WriteAllText(legacyPath, "newer-manual-import");
        File.SetLastWriteTimeUtc(canonicalPath, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(legacyPath, DateTime.UtcNow.AddMinutes(-1));
        WriteSettings(localApplicationDataPath, workspaceRootPath);

        _ = new LocalAppPaths(localApplicationDataPath, installRootPath);

        Assert.AreEqual("newer-manual-import", File.ReadAllText(canonicalPath));
        Assert.IsFalse(File.Exists(legacyPath));
    }

    [TestMethod]
    public void ManagedExternalToolPaths_UseDedicatedStableDirectories()
    {
        var paths = CreatePaths("managed-tools");

        Assert.AreEqual(
            Path.Combine(paths.ToolsRootPath, "ffmpeg", "ffmpeg.exe"),
            paths.GetManagedExternalToolPath(ExternalToolKind.Ffmpeg));
        Assert.AreEqual(
            Path.Combine(paths.ToolsRootPath, "av1an", "av1an.exe"),
            paths.GetManagedExternalToolPath(ExternalToolKind.Av1an));
    }

    private LocalAppPaths CreatePaths(string workspaceFolderName)
    {
        var workspaceRootPath = Path.Combine(_testRoot!, workspaceFolderName);
        var localApplicationDataPath = Path.Combine(_testRoot!, $"{workspaceFolderName}-local-state");
        var installRootPath = Path.Combine(_testRoot!, $"{workspaceFolderName}-install-root");
        Directory.CreateDirectory(localApplicationDataPath);
        Directory.CreateDirectory(installRootPath);
        WriteSettings(localApplicationDataPath, workspaceRootPath);
        return new LocalAppPaths(
            localApplicationDataPath,
            installRootPath,
            [Path.Combine(_testRoot!, $"{workspaceFolderName}-fallback-workspace")]);
    }

    private static void WriteSettings(string localApplicationDataPath, string workspaceRootPath)
    {
        var settingsDirectoryPath = Path.Combine(localApplicationDataPath, "FlowEncode", "data", "settings");
        Directory.CreateDirectory(settingsDirectoryPath);
        var settingsPath = Path.Combine(settingsDirectoryPath, "settings.json");
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(new
            {
                workspaceRootPath
            }));
    }
}
