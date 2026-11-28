using System.Net;
using System.Runtime.Versioning;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class SetupBootstrapOfflineStatusTests
{
    [TestMethod]
    public async Task RecommendedVersionCheck_DoesNotQueryReleaseApisAndKeepsInstallsEnabled()
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var paths = new LocalAppPaths(root.Path, root.Path);
        using var factory = new OfflineFactory();
        using var updates = new GitHubReleaseEncoderUpdateService(paths, factory);
        using var tools = new LocalExternalToolService(paths, factory);
        using var service = new SetupBootstrapService(paths, updates, null!, tools,
            new CliEnvironmentIntegrationService(paths), factory);
        var readiness = new EnvironmentReadinessReport(DateTimeOffset.Now, [], []);

        var report = await service.GetStatusReportAsync(readiness);
        var local = await service.GetLocalStatusReportAsync(readiness, report);

        Assert.AreEqual(0, factory.RequestCount);
        foreach (var kind in new[] { SetupDependencyKind.FfmpegBundle, SetupDependencyKind.X264,
            SetupDependencyKind.X265, SetupDependencyKind.SvtAv1, SetupDependencyKind.Av1an })
        {
            var status = local.Dependencies.Single(item => item.Kind == kind);
            Assert.AreEqual(ToolchainBaseline.VersionFor(kind), status.LatestVersion);
            Assert.IsTrue(status.IsInstallSupported);
            Assert.IsTrue(status.IsInstallEnabled);
        }
    }

    [TestMethod]
    public async Task LocalStatus_ReusesPythonAndDistributionVersionsFromReadiness()
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var paths = new LocalAppPaths(root.Path, root.Path);
        using var factory = new OfflineFactory();
        using var service = new SetupBootstrapService(paths, null!, null!, null!,
            new CliEnvironmentIntegrationService(paths), factory);
        ToolProbeResult Tool(RegisteredToolKind kind, string version) => new(kind, ReadinessState.Ready,
            ToolDetectionSource.ManualSelection, "retained", Path.Combine(root.Path, "python.exe"), version, "", "");
        PythonPackageStatus[] jetPackages = [new("vapoursynth-zsmooth", "0.20.0", true), new("vapoursynth-bwdif", "", false)];
        PythonPackageStatus[] utilityPlugins = [new("vapoursynth-fillborders", "5.0", true), new("vsfpng", "1.3", false)];
        var readiness = new EnvironmentReadinessReport(DateTimeOffset.Now,
            [Tool(RegisteredToolKind.Python, "3.14.10 x64"), Tool(RegisteredToolKind.Pip, "25.0"),
                Tool(RegisteredToolKind.Vspipe, "R82"),
                Tool(RegisteredToolKind.PythonModuleAwsmfunc, "1.3.5"),
                Tool(RegisteredToolKind.PythonModuleVsjetpack, "3.0.0") with
                {
                    State = ReadinessState.Partial,
                    FailureReason = "Missing bwdif",
                    PythonPackages = jetPackages
                },
                Tool(RegisteredToolKind.VapourSynthUtilityPlugins, "") with
                {
                    State = ReadinessState.Partial,
                    PythonPackages = utilityPlugins
                }], []);

        var report = await service.GetLocalStatusReportAsync(readiness);

        Assert.AreEqual("3.14.10 x64", report.Dependencies.Single(item => item.Kind == SetupDependencyKind.Python312).InstalledVersion);
        Assert.AreEqual("1.3.5", report.Dependencies.Single(item => item.Kind == SetupDependencyKind.Awsmfunc).InstalledVersion);
        var jet = report.Dependencies.Single(item => item.Kind == SetupDependencyKind.Vsjetpack);
        Assert.AreEqual("3.0.0", jet.InstalledVersion);
        Assert.AreEqual(string.Empty, jet.LatestVersion);
        Assert.AreEqual(string.Empty, jet.Detail);
        Assert.IsTrue(jet.IsInstallEnabled);
        CollectionAssert.AreEqual(jetPackages, jet.PythonPackages);
        var plugins = report.Dependencies.Single(item => item.Kind == SetupDependencyKind.VapourSynthUtilityPlugins);
        Assert.AreEqual(ReadinessState.Partial, plugins.State);
        Assert.IsTrue(plugins.IsInstallEnabled);
        CollectionAssert.AreEqual(utilityPlugins, plugins.PythonPackages);
        Assert.AreEqual(ReadinessState.Ready, report.Dependencies.Single(item => item.Kind == SetupDependencyKind.Awsmfunc).State);
        Assert.AreEqual(0, factory.RequestCount);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task LocalStatus_ProbesDeeSubfolderThenDeewDirectoryThenPath()
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var paths = new LocalAppPaths(root.Path, root.Path);
        using var factory = new OfflineFactory();
        using var service = new SetupBootstrapService(paths, null!, null!, null!,
            new CliEnvironmentIntegrationService(paths), factory);
        var portable = Directory.CreateDirectory(Path.Combine(root.Path, "portable")).FullName;
        var deew = Path.Combine(portable, "deew.exe");
        var adjacent = Directory.CreateDirectory(Path.Combine(portable, "dee")).FullName;
        var onPath = Directory.CreateDirectory(Path.Combine(root.Path, "path")).FullName;
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", onPath);
            var missing = await CheckAsync();
            Assert.AreEqual(ReadinessState.Missing, missing.State);

            var localEncoder = AddComponent(adjacent, "dee_ddp_encoder");
            var flatEncoder = AddComponent(portable, "dee_ddp_encoder");
            var fallbackEncoder = AddComponent(onPath, "dee_ddp_encoder");
            var scenario = Path.Combine(adjacent, "business-scenario.txt");
            await File.WriteAllTextAsync(scenario, "legacy-dee");

            var incompatible = await CheckAsync();
            Assert.AreEqual(ReadinessState.Misconfigured, incompatible.State);
            Assert.AreEqual("6.6.0", incompatible.InstalledVersion);
            StringAssert.Contains(incompatible.Detail, localEncoder);
            Assert.IsFalse(incompatible.Detail.Contains(flatEncoder, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(incompatible.Detail.Contains(fallbackEncoder, StringComparison.OrdinalIgnoreCase));

            File.Delete(scenario);
            var aac = AddComponent(portable, "dee_aac_encoder");
            var pathAac = AddComponent(onPath, "dee_aac_encoder");
            var measurement = AddComponent(onPath, "dee_measure_loudness");
            var partial = await CheckAsync();
            Assert.AreEqual(ReadinessState.Partial, partial.State);
            Assert.AreEqual("6.7.0", partial.InstalledVersion);
            StringAssert.Contains(partial.Detail, aac);
            Assert.IsFalse(partial.Detail.Contains(pathAac, StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(partial.Detail, measurement);
            StringAssert.Contains(partial.Detail, "dee_ddpjoc_encoder: Missing");

            var executionPath = DeewToolchain.StartInfo(deew, "", "").Environment["PATH"];
            Assert.AreEqual(aac, DeewToolchain.Resolve(deew, "dee_aac_encoder", executionPath!));

            foreach (var name in new[] { "dee_ddpjoc_encoder", "dee_dthd_encoder", "dee_ac4_encoder", "dee_ac4ajoc_encoder" })
            {
                AddComponent(adjacent, name);
            }

            var ready = await CheckAsync();
            Assert.AreEqual(ReadinessState.Ready, ready.State);
            Assert.IsFalse(ready.IsInstallSupported);
            Assert.AreEqual(0, factory.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }

        async Task<SetupDependencyStatus> CheckAsync()
        {
            var readiness = new EnvironmentReadinessReport(DateTimeOffset.Now,
                [new ToolProbeResult(RegisteredToolKind.Deew, ReadinessState.Ready, ToolDetectionSource.ManualSelection,
                    "test", deew, DeewArguments.MinimumVersion, "", ""),
                 new ToolProbeResult(RegisteredToolKind.Dee, ReadinessState.Ready, ToolDetectionSource.ManualSelection,
                    "test", Path.Combine(portable, "dee.exe"), "5.2.1", "", "")], []);
            var report = await service.GetLocalStatusReportAsync(readiness);
            return report.Dependencies.Single(item => item.Kind == SetupDependencyKind.Dee);
        }

        static string AddComponent(string directory, string name)
        {
            var stubDirectory = Path.Combine(AppContext.BaseDirectory, "ToolStub");
            foreach (var file in Directory.EnumerateFiles(stubDirectory, "FlowEncode.ToolStub.*")
                .Where(file => !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
            }

            var path = Path.Combine(directory, name + ".exe");
            File.Copy(Path.Combine(stubDirectory, "FlowEncode.ToolStub.exe"), path);
            return path;
        }
    }

    private sealed class OfflineFactory : HttpMessageHandler, IFlowEncodeHttpClientFactory
    {
        public int RequestCount { get; private set; }
        public HttpClient CreateClient(FlowEncodeHttpClientProfile profile = FlowEncodeHttpClientProfile.Api)
            => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
