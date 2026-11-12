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
        var readiness = new EnvironmentReadinessReport(DateTimeOffset.Now,
            [Tool(RegisteredToolKind.Python, "3.12.10 x64"), Tool(RegisteredToolKind.Pip, "25.0"),
                Tool(RegisteredToolKind.PythonModuleAwsmfunc, "1.3.5"),
                Tool(RegisteredToolKind.PythonModuleVsjetpack, "2.2.2")], []);

        var report = await service.GetLocalStatusReportAsync(readiness);

        Assert.AreEqual("3.12.10 x64", report.Dependencies.Single(item => item.Kind == SetupDependencyKind.Python312).InstalledVersion);
        Assert.AreEqual("1.3.5", report.Dependencies.Single(item => item.Kind == SetupDependencyKind.Awsmfunc).InstalledVersion);
        Assert.AreEqual("2.2.2", report.Dependencies.Single(item => item.Kind == SetupDependencyKind.Vsjetpack).InstalledVersion);
        Assert.AreEqual(0, factory.RequestCount);
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
