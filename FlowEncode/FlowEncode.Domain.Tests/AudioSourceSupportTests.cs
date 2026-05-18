using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class AudioSourceSupportTests
{
    [TestMethod]
    public void IsSupportedExtension_WhenFfmpegBackedExtension_ReturnsTrue()
    {
        Assert.IsTrue(AudioSourceSupport.IsSupportedExtension(@"C:\audio\track.m4a"));
    }

    [TestMethod]
    public void IsSupportedExtension_WhenEac3ToOnlyExtension_ReturnsTrue()
    {
        Assert.IsTrue(AudioSourceSupport.IsSupportedExtension(@"C:\audio\track.rf64"));
        Assert.IsTrue(AudioSourceSupport.IsSupportedExtension(@"C:\audio\track.pcm"));
    }

    [TestMethod]
    public void IsSupportedExtension_WhenExtensionIsUnknown_ReturnsFalse()
    {
        Assert.IsFalse(AudioSourceSupport.IsSupportedExtension(@"C:\audio\track.txt"));
    }

    [TestMethod]
    public void SupportedDropExtensions_ContainsUnionOfSupportedAudioExtensions()
    {
        CollectionAssert.Contains(AudioSourceSupport.SupportedDropExtensions.ToList(), ".m4a");
        CollectionAssert.Contains(AudioSourceSupport.SupportedDropExtensions.ToList(), ".rf64");
    }
}
