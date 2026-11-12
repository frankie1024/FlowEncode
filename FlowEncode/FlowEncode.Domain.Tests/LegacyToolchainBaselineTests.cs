using System.Text.Json;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LegacyToolchainBaselineTests
{
    [TestMethod]
    public async Task UpdateServices_ReturnPinnedPackagesWithoutConsultingFloatingMetadata()
    {
        var temp = Path.Combine(Path.GetTempPath(), nameof(LegacyToolchainBaselineTests), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LocalAppPaths(temp, Path.Combine(temp, "install"), [Path.Combine(temp, "workspace")]);
            var factory = new OfflineFactory();
            using var encoders = new GitHubReleaseEncoderUpdateService(paths, factory);
            using var tools = new LocalExternalToolService(paths, factory);
            var encoderPackages = await encoders.GetAvailableUpdatesAsync();
            var toolPackages = await tools.GetAvailableUpdatesAsync();
            CollectionAssert.AreEquivalent(new[] { "stable-20250608-b35605ace3dd", "4.2", "4.1.0" },
                encoderPackages.Select(p => p.ReleaseName).ToArray());
            CollectionAssert.AreEquivalent(new[] { "8.1", "0.5.2-flowencode.2" }, toolPackages.Select(p => p.ReleaseName).ToArray());
            foreach (var package in encoderPackages)
            {
                Assert.IsTrue(package.IsAutomatic);
                Assert.AreEqual(64, package.Sha256!.Length);
                Assert.IsFalse(package.DownloadUrl.Contains("latest"));
            }
            // Even a caller with a cached or forged newer package cannot bypass the baseline.
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => encoders.InstallUpdateAsync(
                encoderPackages[0] with { DownloadUrl = "https://example.test/newer.zip" }));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => tools.InstallUpdateAsync(
                toolPackages[0] with { Sha256 = new string('a', 64) }));
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    [TestMethod]
    public void PluginSnapshot_ContainsSinglePinnedReleaseAndArchiveHashes()
    {
        using var stream = typeof(LocalExternalToolService).Assembly.GetManifestResourceStream("FlowEncode.Infrastructure.Resources.VsrepoBaseline.json");
        Assert.IsNotNull(stream);
        using var doc = JsonDocument.Parse(stream);
        var packages = doc.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        Assert.IsTrue(packages.Length >= 7);
        foreach (var package in packages)
        {
            var releases = package.GetProperty("releases");
            Assert.AreEqual(1, releases.GetArrayLength(), package.GetProperty("identifier").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(releases[0].GetProperty("version").GetString()));
            var artifacts = releases[0].EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("files", out _)).ToArray();
            Assert.IsTrue(artifacts.Length > 0);
            foreach (var artifact in artifacts)
            {
                Assert.IsFalse(artifact.Value.GetProperty("url").GetString()!.Contains("/latest/"));
                foreach (var file in artifact.Value.GetProperty("files").EnumerateObject())
                    Assert.AreEqual(64, file.Value[1].GetString()!.Length);
            }
        }
        var arguments = VapourSynthToolchainPolicy.InstallArguments("vapoursynth", "constraints.txt");
        CollectionAssert.Contains(arguments.ToArray(), "vapoursynth==79");
        CollectionAssert.Contains(arguments.ToArray(), "--constraint");
        CollectionAssert.Contains(arguments.ToArray(), "--only-binary=:all:");
    }

    [TestMethod]
    [DataRow("deew 3.2.2", false)]
    [DataRow("deew 3.2.2 / DEE 5.2.1", false)]
    [DataRow("deew 5.1.0", true)]
    [DataRow("deew 6.0", true)]
    public void Probe_RejectsKnownIncompatibleDeew(string version, bool expected) =>
        Assert.AreEqual(expected, ProcessToolProbeService.IsUnsupportedDeewVersion(version));

    [TestMethod]
    [TestCategory("ExternalToolSmoke")]
    public async Task PinnedArchives_InstallThroughRealStagedValidation()
    {
        var fixtures = Environment.GetEnvironmentVariable("FLOWENCODE_LEGACY_SMOKE_ARCHIVES");
        if (string.IsNullOrWhiteSpace(fixtures)) Assert.Inconclusive("Set FLOWENCODE_LEGACY_SMOKE_ARCHIVES to downloaded pinned archives.");
        var temp = Path.Combine(Path.GetTempPath(), "LegacyInstallSmoke", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LocalAppPaths(temp, Path.Combine(temp, "install"), [Path.Combine(temp, "workspace")]);
            Assert.AreEqual(Path.Combine(temp, "workspace"), paths.WorkspaceRootPath);
            var factory = new ArchiveFactory(fixtures);
            using var encoders = new GitHubReleaseEncoderUpdateService(paths, factory);
            using var tools = new LocalExternalToolService(paths, factory);
            foreach (var package in await encoders.GetAvailableUpdatesAsync())
            {
                var path = await encoders.InstallUpdateAsync(package);
                Assert.IsTrue(File.Exists(path), package.AssetName);
                Console.WriteLine($"Installed and validated {package.AssetName}");
            }
            foreach (var package in await tools.GetAvailableUpdatesAsync())
            {
                var path = await tools.InstallUpdateAsync(package);
                Assert.IsTrue(File.Exists(path), package.AssetName);
                Console.WriteLine($"Installed and validated {package.AssetName}");
            }
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    private sealed class ArchiveFactory(string fixtures) : IFlowEncodeHttpClientFactory
    {
        public HttpClient CreateClient(FlowEncodeHttpClientProfile profile) => new(new ArchiveHandler(fixtures));
    }
    private sealed class ArchiveHandler(string fixtures) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = Path.GetFileName(request.RequestUri!.LocalPath);
            var path = Path.Combine(fixtures, name);
            Assert.IsTrue(File.Exists(path), $"Unexpected download {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StreamContent(File.OpenRead(path)), RequestMessage = request
            });
        }
    }

    private sealed class OfflineFactory : IFlowEncodeHttpClientFactory
    {
        public HttpClient CreateClient(FlowEncodeHttpClientProfile profile) => new(new RejectNetworkHandler());
    }
    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new AssertFailedException("A pinned package lookup must not consult online release metadata.");
    }
}
