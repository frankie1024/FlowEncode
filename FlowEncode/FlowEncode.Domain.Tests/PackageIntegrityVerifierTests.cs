using System.Security.Cryptography;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class PackageIntegrityVerifierTests
{
    private string _testRoot = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "FlowEncodePackageIntegrityVerifierTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [TestMethod]
    public void NormalizeSha256Digest_WhenPrefixExists_StripsPrefixAndNormalizesCase()
    {
        var normalized = PackageIntegrityVerifier.NormalizeSha256Digest("sha256:ABCDEF1234");

        Assert.AreEqual("abcdef1234", normalized);
    }

    [TestMethod]
    public async Task VerifySha256Async_WhenHashMatches_DoesNotThrow()
    {
        var filePath = Path.Combine(_testRoot, "payload.bin");
        await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4, 5]);
        var expectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(filePath)));

        await PackageIntegrityVerifier.VerifySha256Async(
            filePath,
            $"sha256:{expectedHash}",
            CancellationToken.None,
            "测试包");
    }

    [TestMethod]
    public async Task VerifySha256Async_WhenHashDiffers_Throws()
    {
        var filePath = Path.Combine(_testRoot, "payload.bin");
        await File.WriteAllBytesAsync(filePath, [5, 4, 3, 2, 1]);

        try
        {
            await PackageIntegrityVerifier.VerifySha256Async(
                filePath,
                "sha256:0000",
                CancellationToken.None,
                "测试包");
            Assert.Fail("Expected InvalidOperationException.");
        }
        catch (InvalidOperationException)
        {
        }
    }
}
