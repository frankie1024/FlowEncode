using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class AudioSourceInfoTests
{
    [TestMethod]
    public void IsLossless_WhenCodecIsFlac_ReturnsTrue()
    {
        var info = CreateInfo("flac");

        Assert.IsTrue(info.IsLossless());
    }

    [TestMethod]
    public void IsLossless_WhenCodecIsPcm_ReturnsTrue()
    {
        var info = CreateInfo("pcm_s24le");

        Assert.IsTrue(info.IsLossless());
    }

    [TestMethod]
    public void IsLossless_WhenCodecIsDtsHdMa_ReturnsTrue()
    {
        var info = CreateInfo("dts", "DTS-HD MA");

        Assert.IsTrue(info.IsLossless());
    }

    [TestMethod]
    public void IsLossless_WhenCodecIsAac_ReturnsFalse()
    {
        var info = CreateInfo("aac");

        Assert.IsFalse(info.IsLossless());
    }

    [TestMethod]
    public void HasStereoOrGreaterChannels_WhenSourceIsStereo_ReturnsTrue()
    {
        var info = CreateInfo("flac", channels: 2);

        Assert.IsTrue(info.HasStereoOrGreaterChannels());
        Assert.AreEqual(AudioChannelProfile.Stereo, info.InferProfile());
    }

    [TestMethod]
    public void BitDepth_WhenProvided_IsExposedOnRecord()
    {
        var info = CreateInfo("flac", bitDepth: 24);

        Assert.AreEqual(24, info.BitDepth);
    }

    [TestMethod]
    public void HasSurround51Or71Channels_ReturnsTrueForSixAndEightChannels()
    {
        Assert.IsTrue(CreateInfo("flac", channels: 6).HasSurround51Or71Channels());
        Assert.IsTrue(CreateInfo("flac", channels: 8).HasSurround51Or71Channels());
    }

    [TestMethod]
    public void HasSurround51Or71Channels_ReturnsFalseForOtherChannelCounts()
    {
        Assert.IsFalse(CreateInfo("flac", channels: 2).HasSurround51Or71Channels());
        Assert.IsFalse(CreateInfo("flac", channels: 10).HasSurround51Or71Channels());
    }

    private static AudioSourceInfo CreateInfo(string codecName, string profileName = "", int channels = 2, int? bitDepth = 24)
    {
        return new AudioSourceInfo(codecName, profileName, channels, string.Empty, 48000, bitDepth, null);
    }
}
