using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class FlowEncodeHttpClientFactoryTests
{
    [TestMethod]
    public void CreateClient_AppliesUserAgentAndProfileTimeouts()
    {
        using var factory = new FlowEncodeHttpClientFactory();

        using var apiClient = factory.CreateClient(FlowEncodeHttpClientProfile.Api);
        using var downloadClient = factory.CreateClient(FlowEncodeHttpClientProfile.Download);

        StringAssert.Contains(apiClient.DefaultRequestHeaders.UserAgent.ToString(), "FlowEncode/");
        StringAssert.Contains(downloadClient.DefaultRequestHeaders.UserAgent.ToString(), "FlowEncode/");
        Assert.AreEqual(FlowEncodeHttpClientFactory.ApiTimeout, apiClient.Timeout);
        Assert.AreEqual(FlowEncodeHttpClientFactory.DownloadTimeout, downloadClient.Timeout);
    }

    [TestMethod]
    public void Constructor_ConfiguresConnectionRefreshPolicy()
    {
        using var factory = new FlowEncodeHttpClientFactory();

        Assert.AreEqual(FlowEncodeHttpClientFactory.ConnectionLifetime, factory.PooledConnectionLifetimeForTesting);
        Assert.AreEqual(FlowEncodeHttpClientFactory.ConnectionIdleTimeout, factory.PooledConnectionIdleTimeoutForTesting);
    }
}
