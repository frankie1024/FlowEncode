using FlowEncode.Domain;
using FlowEncode.Infrastructure;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class EncoderManifestCatalogTests
{
    [TestMethod]
    public void X264AndX265_DoNotAdvertiseMp4Output()
    {
        var capabilities = EncoderManifestCatalog.GetAll();

        var x264 = capabilities.Single(static capability => capability.Kind == EncoderKind.X264);
        var x265 = capabilities.Single(static capability => capability.Kind == EncoderKind.X265);

        CollectionAssert.AreEqual(new[] { "264", "mkv" }, x264.OutputFormats.ToArray());
        CollectionAssert.AreEqual(new[] { "hevc", "mkv" }, x265.OutputFormats.ToArray());
    }
}
