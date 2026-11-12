using System.Net;
using System.Text.Json;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LocalExternalToolServiceTests
{
    [TestMethod]
    public async Task BaselineAv1an_UsesExactMaintainedReleaseAndRejectsChangedDigest()
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var factory = new Av1anReleaseFixture();
        using var service = new LocalExternalToolService(new LocalAppPaths(root.Path), factory);
        var package = (await service.GetBaselinePackagesAsync()).Single();
        Assert.AreEqual(ExternalToolKind.Av1an, package.Kind);
        Assert.AreEqual("0.6.0-flowencode.1", package.ReleaseName);
        StringAssert.Contains(package.DownloadUrl, "/releases/download/v0.6.0-flowencode.1/av1an.exe");
        Assert.IsTrue(package.IsAutomatic);
        factory.Digest = new string('a', 64);
        Assert.AreEqual(0, (await service.GetBaselinePackagesAsync()).Count);
        CollectionAssert.Contains(factory.Paths, "/repos/frankie1024/Av1an/releases/tags/v0.6.0-flowencode.1");
    }

    private sealed class Av1anReleaseFixture : HttpMessageHandler, IFlowEncodeHttpClientFactory
    {
        public string Digest = "ded80fced3cbcbd80ee31fe6243eca6c8970c48367a47064a61698e2e288df6b";
        public List<string> Paths { get; } = [];
        public HttpClient CreateClient(FlowEncodeHttpClientProfile profile) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath != "/repos/frankie1024/Av1an/releases/tags/v0.6.0-flowencode.1")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    tag_name = "v0.6.0-flowencode.1", draft = false, prerelease = false,
                    html_url = "https://github.com/frankie1024/Av1an/releases/tag/v0.6.0-flowencode.1",
                    published_at = "2026-11-12T06:00:00Z",
                    assets = new[] { new { name = "av1an.exe", digest = "sha256:" + Digest,
                        browser_download_url = "https://github.com/frankie1024/Av1an/releases/download/v0.6.0-flowencode.1/av1an.exe" } }
                }))
            });
        }
    }

    [TestMethod]
    public void GetManagedAv1anRepositoryCandidates_WithoutOverride_ReturnsManagedForkRepository()
    {
        Environment.SetEnvironmentVariable("FLOWENCODE_AV1AN_RELEASE_REPO", null);

        var candidates = LocalExternalToolService.GetManagedAv1anRepositoryCandidates();

        CollectionAssert.AreEqual(new[] { "frankie1024/Av1an" }, candidates.ToArray());
    }

    [TestMethod]
    public void IsManagedAv1anAssetName_WithExecutableOrArchiveNames_ReturnsExpectedResult()
    {
        Assert.IsTrue(LocalExternalToolService.IsManagedAv1anAssetName("av1an.exe"));
        Assert.IsTrue(LocalExternalToolService.IsManagedAv1anAssetName("flowencode-av1an-r76.zip"));
        Assert.IsFalse(LocalExternalToolService.IsManagedAv1anAssetName("av1an.exe.sha256"));
        Assert.IsFalse(LocalExternalToolService.IsManagedAv1anAssetName("FlowEncode_Setup_v1.9.2.exe"));
    }

    [TestMethod]
    [DataRow("v1.0-rc1")]
    [DataRow("v1.0-rc")]
    [DataRow("v1.0.rc2")]
    [DataRow("v1.0_rc1")]
    [DataRow("v1.0 rc")]
    [DataRow("rc1")]
    [DataRow("rc")]
    [DataRow("v2.5.0-RC3")]
    public void ContainsUnstableReleaseMarker_ReleaseCandidateFormats_ReturnsTrue(string value)
    {
        Assert.IsTrue(LocalExternalToolService.ContainsUnstableReleaseMarker(value));
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("resource")]
    [DataRow("architecture")]
    [DataRow("March")]
    [DataRow("arc")]
    [DataRow("search")]
    [DataRow("recorder")]
    [DataRow("v1.0-source")]
    [DataRow("resource-pack")]
    public void ContainsUnstableReleaseMarker_WordsContainingRc_ReturnsFalse(string value)
    {
        Assert.IsFalse(LocalExternalToolService.ContainsUnstableReleaseMarker(value));
    }

    [TestMethod]
    [DataRow("v1.0-beta1")]
    [DataRow("nightly-build")]
    [DataRow("dev-20260517")]
    [DataRow("alpha")]
    [DataRow("preview")]
    [DataRow("unstable")]
    [DataRow("latest")]
    public void ContainsUnstableReleaseMarker_OtherUnstableMarkers_ReturnsTrue(string value)
    {
        Assert.IsTrue(LocalExternalToolService.ContainsUnstableReleaseMarker(value));
    }

    [TestMethod]
    [DataRow("v1.0.0")]
    [DataRow("2026.05.17")]
    [DataRow("")]
    [DataRow(null)]
    [DataRow("   ")]
    public void ContainsUnstableReleaseMarker_StableOrNull_ReturnsFalse(string? value)
    {
        Assert.IsFalse(LocalExternalToolService.ContainsUnstableReleaseMarker(value));
    }

    [TestMethod]
    public void GetFfmpegRuntimeLibraryFileNames_UsesReportedMajorVersionsOnly()
    {
        const string output = """
            ffmpeg version N-123
            libavutil      61.  3.100 / 61.  3.100
            libavcodec     63.  5.100 / 63.  5.100
            libavformat    63.  2.100 / 63.  2.100
            libswresample   7.  1.100 /  7.  1.100
            """;

        var names = LocalExternalToolService.GetFfmpegRuntimeLibraryFileNames(output);

        CollectionAssert.AreEquivalent(
            new[] { "avutil-61.dll", "avcodec-63.dll", "avformat-63.dll", "swresample-7.dll" },
            names.ToArray());
        Assert.IsFalse(names.Contains("avcodec-54.dll", StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(names.Contains("avutil-52.dll", StringComparer.OrdinalIgnoreCase));
    }
}
