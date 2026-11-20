using System.Runtime.Versioning;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class ManualToolPackageTests
{
    [TestMethod]
    public async Task Import_PreservesPackageLayoutAndUsesItInsteadOfTheLegacyExecutable()
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var paths = new LocalAppPaths(root.Path, root.Path);
        var legacyExecutable = Path.Combine(paths.ToolsRootPath, "eac3to.exe");
        File.WriteAllText(legacyExecutable, "legacy");
        var otherTool = Path.Combine(paths.ToolsRootPath, "other.exe");
        File.WriteAllText(otherTool, "unrelated");

        var source = Path.Combine(root.Path, "extracted package");
        Directory.CreateDirectory(Path.Combine(source, "plugins"));
        File.WriteAllText(Path.Combine(source, "eac3to.exe"), "new");
        File.WriteAllText(Path.Combine(source, "runtime.dll"), "runtime");
        File.WriteAllText(Path.Combine(source, "plugins", "decoder.dll"), "decoder");
        var package = ManualToolPackage.Find(SetupDependencyKind.Eac3To)!;
        var target = package.GetDirectory(paths.ToolsRootPath);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "obsolete.dll"), "old");

        package.Import(source, paths.ToolsRootPath);

        var installedExecutable = Path.Combine(target, "eac3to.exe");
        Assert.AreEqual("runtime", File.ReadAllText(Path.Combine(target, "runtime.dll")));
        Assert.AreEqual("decoder", File.ReadAllText(Path.Combine(target, "plugins", "decoder.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "obsolete.dll")));
        var settings = new LocalAppSettingsService(paths);
        var probe = new ProcessToolProbeService(new DefaultToolRegistryService(), paths,
            new LocalEncoderDiscoveryService(paths, settings), settings);
        Assert.AreEqual(installedExecutable, (await probe.ProbeAsync(RegisteredToolKind.Eac3To)).ExecutablePath);

        var manualKey = ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Eac3To);
        settings.Save(settings.Load() with { ManualToolPaths = new Dictionary<string, string> { [manualKey] = legacyExecutable } });
        Assert.AreEqual(installedExecutable, (await probe.ProbeAsync(RegisteredToolKind.Eac3To)).ExecutablePath);

        var sourceExecutable = Path.Combine(source, "eac3to.exe");
        settings.Save(settings.Load() with { ManualToolPaths = new Dictionary<string, string> { [manualKey] = sourceExecutable } });
        Assert.AreEqual(sourceExecutable, (await probe.ProbeAsync(RegisteredToolKind.Eac3To)).ExecutablePath);

        using var clients = new FlowEncodeHttpClientFactory();
        using var bootstrap = new SetupBootstrapService(paths, null!, null!, null!,
            new CliEnvironmentIntegrationService(paths), clients);
        await bootstrap.UninstallAsync(SetupDependencyKind.Eac3To);
        Assert.IsFalse(Directory.Exists(target));
        Assert.IsFalse(File.Exists(legacyExecutable));
        Assert.IsTrue(File.Exists(otherTool));
        Assert.IsTrue(File.Exists(sourceExecutable));
    }

    [TestMethod]
    public void Import_RejectsInvalidAndOverlappingSourceFolders()
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var package = ManualToolPackage.Find(SetupDependencyKind.Eac3To)!;
        var target = package.GetDirectory(root.Path);
        Directory.CreateDirectory(target);
        var installedExecutable = Path.Combine(target, "eac3to.exe");
        File.WriteAllText(installedExecutable, "working");

        var invalidSource = Directory.CreateDirectory(Path.Combine(root.Path, "invalid")).FullName;
        Assert.ThrowsExactly<FileNotFoundException>(() => package.Import(invalidSource, root.Path));

        File.WriteAllText(Path.Combine(root.Path, "eac3to.exe"), "legacy");
        Assert.ThrowsExactly<InvalidOperationException>(() => package.Import(root.Path, root.Path));
        Assert.AreEqual("working", File.ReadAllText(installedExecutable));
    }

    [TestMethod]
    [DataRow(SetupDependencyKind.Avs2PipeMod, "avs2pipemod.exe")]
    [DataRow(SetupDependencyKind.DgDemux, "DGDemux.exe")]
    [DataRow(SetupDependencyKind.Eac3To, "eac3to.exe")]
    [DataRow(SetupDependencyKind.Deew, "deew.exe")]
    [DataRow(SetupDependencyKind.Dee, "DolbyEncodingEngine.exe")]
    [DataRow(SetupDependencyKind.OpusExt, "opusenc.exe")]
    public void Import_SupportsNestedPackageExecutablesAndStandaloneReplacements(SetupDependencyKind kind, string executableName)
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var toolsRoot = Path.Combine(root.Path, "tools");
        var source = Path.Combine(root.Path, "package");
        Directory.CreateDirectory(Path.Combine(source, "bin"));
        var sourceExecutable = Path.Combine(source, "bin", executableName);
        File.WriteAllText(sourceExecutable, "executable");
        File.WriteAllText(Path.Combine(source, "runtime.dll"), "dependency");
        var package = ManualToolPackage.Find(kind)!;
        var target = package.GetDirectory(toolsRoot);

        package.Import(source, toolsRoot);

        Assert.AreEqual(Path.Combine(target, "bin", executableName), package.FindExecutable(target));
        Assert.AreEqual("dependency", File.ReadAllText(Path.Combine(target, "runtime.dll")));

        package.Import(sourceExecutable, toolsRoot);

        Assert.AreEqual(Path.Combine(target, package.ExecutableNames[0]), package.FindExecutable(target));
        Assert.IsFalse(Directory.Exists(Path.Combine(target, "bin")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "runtime.dll")));
    }
}
