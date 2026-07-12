using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FlowEncode.Application;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ResumablePackageDownloaderTests
{
    private string _testRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), nameof(ResumablePackageDownloaderTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [TestMethod]
    public async Task DownloadAsync_WithPartialFile_ResumesFromExistingLength()
    {
        var destinationPath = Path.Combine(_testRoot, "package.zip");
        await File.WriteAllTextAsync(destinationPath + ".part", "abc");
        var handler = new StaticResponseHandler(request =>
        {
            Assert.AreEqual("bytes=3-", request.Headers.Range?.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("def"))
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        });
        using var client = new HttpClient(handler);
        var progress = new CollectingProgress();

        await ResumablePackageDownloader.DownloadAsync(
            client,
            "https://example.test/package.zip",
            destinationPath,
            progress,
            CancellationToken.None);

        Assert.AreEqual("abcdef", await File.ReadAllTextAsync(destinationPath));
        Assert.IsFalse(File.Exists(destinationPath + ".part"));
        Assert.AreEqual(6L, progress.Items.Last().BytesReceived);
        Assert.AreEqual(6L, progress.Items.Last().TotalBytes);
    }

    [TestMethod]
    public async Task DownloadAsync_WhenServerIgnoresRange_RestartsTheFile()
    {
        var destinationPath = Path.Combine(_testRoot, "package.zip");
        await File.WriteAllTextAsync(destinationPath + ".part", "obsolete");
        var handler = new StaticResponseHandler(request =>
        {
            Assert.AreEqual("bytes=8-", request.Headers.Range?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("fresh"))
            };
        });
        using var client = new HttpClient(handler);

        await ResumablePackageDownloader.DownloadAsync(
            client,
            "https://example.test/package.zip",
            destinationPath,
            null,
            CancellationToken.None);

        Assert.AreEqual("fresh", await File.ReadAllTextAsync(destinationPath));
    }

    private sealed class StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class CollectingProgress : IProgress<PackageDownloadProgress>
    {
        public List<PackageDownloadProgress> Items { get; } = [];

        public void Report(PackageDownloadProgress value)
        {
            Items.Add(value);
        }
    }
}
