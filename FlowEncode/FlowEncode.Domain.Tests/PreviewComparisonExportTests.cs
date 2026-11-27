using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class PreviewComparisonExportTests
{
    [TestMethod]
    public void ResolveHtmlPackageMode_UsesTenFrameThreshold()
    {
        Assert.AreEqual(PreviewHtmlPackageMode.SingleFileEmbedded, PreviewComparisonExportCoordinator.ResolveHtmlPackageMode(1));
        Assert.AreEqual(PreviewHtmlPackageMode.SingleFileEmbedded, PreviewComparisonExportCoordinator.ResolveHtmlPackageMode(10));
        Assert.AreEqual(PreviewHtmlPackageMode.FolderLinked, PreviewComparisonExportCoordinator.ResolveHtmlPackageMode(11));
        Assert.AreEqual(PreviewHtmlPackageMode.SingleFileEmbedded, PreviewComparisonExportCoordinator.ResolveHtmlPackageMode(25, PreviewHtmlPackageModePreference.SingleFileEmbedded));
        Assert.AreEqual(PreviewHtmlPackageMode.FolderLinked, PreviewComparisonExportCoordinator.ResolveHtmlPackageMode(3, PreviewHtmlPackageModePreference.FolderLinked));
    }

    [TestMethod]
    public void BuildUniformSampleFrames_ReturnsOrderedUniqueInRangeFrames()
    {
        var frames = PreviewComparisonExportCoordinator.BuildUniformSampleFrames(1000, 8);
        Assert.AreEqual(8, frames.Count);
        Assert.IsTrue(frames[0] >= 70 && frames[0] <= 90);
        Assert.IsTrue(frames[^1] >= 900 && frames[^1] <= 930);
        for (var i = 1; i < frames.Count; i++)
        {
            Assert.IsTrue(frames[i] > frames[i - 1]);
        }
    }

    [TestMethod]
    public void BuildHtml_EscapesTitleAndEmbedsOrLinksImages()
    {
        var groups = new[]
        {
            new PreviewComparisonGroup("#100", 100,
            [
                new PreviewComparisonImageEntry("Source", "images/a.png", "data:image/png;base64,AAAA"),
                new PreviewComparisonImageEntry("Encode", "images/b.png", "data:image/png;base64,BBBB")
            ])
        };

        var singleHtml = PreviewOfflineHtmlBuilder.BuildHtml("Test </script> <Comp>", groups, PreviewHtmlPackageMode.SingleFileEmbedded);
        StringAssert.Contains(singleHtml, "data:image/png;base64,AAAA");
        StringAssert.Contains(singleHtml, "Test &lt;/script&gt; &lt;Comp&gt;");
        Assert.IsFalse(singleHtml.Contains("</script> <Comp>", StringComparison.Ordinal));

        var folderHtml = PreviewOfflineHtmlBuilder.BuildHtml("Folder Comp", groups, PreviewHtmlPackageMode.FolderLinked);
        StringAssert.Contains(folderHtml, "images/a.png");
        Assert.IsFalse(folderHtml.Contains("data:image/png;base64,AAAA", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SlowPicsUploadService_PerformsHandshakeMultipartUploadAndParsesUrl()
    {
        var tempPng = Path.Combine(Path.GetTempPath(), $"flowencode-test-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tempPng, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        try
        {
            var sawGet = false;
            var sawPostMultipart = false;
            var service = new SlowPicsUploadService(() => new FakeHttpHandler((req, _) =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    sawGet = true;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }

                if (req.Method == HttpMethod.Post)
                {
                    sawPostMultipart = req.Content is MultipartFormDataContent;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"key\":\"AbCd1234\"}")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            }));

            var result = await service.UploadAsync(new SlowPicsUploadRequest(
                "Sample Comp",
                IsPublic: false,
                OptimizeImages: false,
                [
                    new PreviewComparisonGroup("#100", 100, [new PreviewComparisonImageEntry("Source", tempPng)])
                ]));

            Assert.IsTrue(sawGet);
            Assert.IsTrue(sawPostMultipart);
            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual("https://slow.pics/c/AbCd1234", result.ComparisonUrl);
            Assert.AreEqual("AbCd1234", result.Key);
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                File.Delete(tempPng);
            }
        }
    }

    [TestMethod]
    public async Task SlowPicsUploadService_HandlesHttpFailureGracefully()
    {
        var tempPng = Path.Combine(Path.GetTempPath(), $"flowencode-test-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tempPng, [0x89, 0x50, 0x4E, 0x47]);

        try
        {
            var service = new SlowPicsUploadService(() => new FakeHttpHandler((req, _) =>
            {
                var status = req.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests;
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent("rate limited")
                });
            }));

            var result = await service.UploadAsync(new SlowPicsUploadRequest(
                "Sample Comp",
                false,
                false,
                [
                    new PreviewComparisonGroup("#10", 10, [new PreviewComparisonImageEntry("Source", tempPng)])
                ]));

            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains(result.ErrorMessage ?? string.Empty, "429");
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                File.Delete(tempPng);
            }
        }
    }

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
