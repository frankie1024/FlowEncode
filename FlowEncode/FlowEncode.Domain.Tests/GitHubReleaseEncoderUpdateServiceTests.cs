using FlowEncode.Infrastructure;
using FlowEncode.Domain;
using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class GitHubReleaseEncoderUpdateServiceTests
{
    [TestMethod]
    public async Task BaselineX265_SelectsExactVerifiedStableAssetWithoutChangingGeneralUpdateChannel()
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var factory = new FixtureHttpFactory();
        using var service = new GitHubReleaseEncoderUpdateService(new LocalAppPaths(root.Path), factory);
        var baseline = await service.GetBaselinePackagesAsync();
        var x265 = baseline.Single();
        Assert.AreEqual(EncoderKind.X265, x265.Kind);
        Assert.AreEqual("4.3 (8+10-bit multilib)", x265.ReleaseName);
        StringAssert.Contains(x265.DownloadUrl, "/x265-windows-builds/releases/download/4.3-multilib.1/x265-4.3-x64.zip");
        Assert.IsTrue(x265.IsAutomatic);
        factory.Digest = new string('a', 64);
        Assert.AreEqual(0, (await service.GetBaselinePackagesAsync()).Count);
        await service.GetAvailableUpdatesAsync();
        Assert.IsTrue(factory.Paths.Contains("/repos/frankie1024/x265-windows-builds/releases/latest"));
    }

    private sealed class FixtureHttpFactory : HttpMessageHandler, IFlowEncodeHttpClientFactory
    {
        public string Digest = "499913e1e898b64078a795c9edcd0ea8805fe05417091ce9abad0572f70f0b3d";
        public List<string> Paths { get; } = [];
        public HttpClient CreateClient(FlowEncodeHttpClientProfile profile) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (!request.RequestUri.AbsolutePath.Contains("/x265-windows-builds/releases/tags/4.3-multilib.1")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                tag_name = "4.3-multilib.1", draft = false, prerelease = false,
                html_url = "https://github.com/frankie1024/x265-windows-builds/releases/tag/4.3-multilib.1", published_at = "2026-11-12T00:00:00Z",
                assets = new[] { new { name = "x265-4.3-x64.zip", digest = "sha256:" + Digest,
                    browser_download_url = "https://github.com/frankie1024/x265-windows-builds/releases/download/4.3-multilib.1/x265-4.3-x64.zip" } }
            })) });
        }
    }

    [TestMethod]
    [DataRow("x264-20260524-b35605ace3dd-x64.zip", "x264")]
    [DataRow("x265-4.2-x64.zip", "x265")]
    [DataRow("svt-av1-4.1.0-x64.zip", "svt-av1")]
    [DataRow("SVT-AV1-v4.1.0-x64.zip", "svt-av1")]
    public void IsExpectedEncoderAssetName_AcceptsCanonicalReleaseNames(string assetName, string prefix)
    {
        Assert.IsTrue(GitHubReleaseEncoderUpdateService.IsExpectedEncoderAssetName(assetName, prefix));
    }

    [TestMethod]
    [DataRow("x264-stable-20260524-b35605ace3dd-windows-x64.zip", "x264")]
    [DataRow("x265-4.2-windows-x64.zip", "x265")]
    [DataRow("SVT-AV1-v4.1.0-windows-x64.zip", "svt-av1")]
    [DataRow("x264-20260524-b35605ace3dd-x86.zip", "x264")]
    [DataRow("x265.zip", "x265")]
    [DataRow("svt-av1-4.1.0-x64.7z", "svt-av1")]
    [DataRow("other-4.1.0-x64.zip", "svt-av1")]
    public void IsExpectedEncoderAssetName_RejectsNonCanonicalReleaseNames(string assetName, string prefix)
    {
        Assert.IsFalse(GitHubReleaseEncoderUpdateService.IsExpectedEncoderAssetName(assetName, prefix));
    }
}
