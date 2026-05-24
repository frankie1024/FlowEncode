using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class GitHubReleaseEncoderUpdateServiceTests
{
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
