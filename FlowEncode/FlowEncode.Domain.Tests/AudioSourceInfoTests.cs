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

    [TestMethod]
    public void InferProfile_SevenPointOneRequiresEightChannels()
    {
        Assert.AreEqual(AudioChannelProfile.Surround71, CreateInfo("dts", channels: 8).InferProfile());
        Assert.IsNull(CreateInfo("dts", channels: 7).InferProfile());
    }

    [TestMethod]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(6, true)]
    [DataRow(8, true)]
    [DataRow(0, false)]
    [DataRow(4, false)]
    [DataRow(7, false)]
    [DataRow(10, false)]
    public void IsDeewChannelCountSupported_MatchesBackendInputCapabilities(int channels, bool expected)
    {
        Assert.AreEqual(expected, AudioSourceInfo.IsDeewChannelCountSupported(channels));
    }

    [TestMethod]
    [DataRow("dts", "DTS-HD MA + DTS:X", 12, true)]
    [DataRow("dts", "DTS-HD MA + DTS:X", 8, false)]
    [DataRow("DTS", "DTS-HD MA", 12, true)]
    [DataRow("dts", "DTS-HD MA", 8, false)]
    [DataRow("dts", "DTS", 6, false)]
    [DataRow("flac", "", 12, false)]
    [DataRow("truehd", "Atmos", 12, false)]
    public void RequiresDdpDtsCompatibilityInput_RecognizesDtsXWithoutChangingOtherFormats(
        string codec, string profile, int channels, bool expected)
    {
        Assert.AreEqual(expected, CreateInfo(codec, profile, channels).RequiresDdpDtsCompatibilityInput());
    }

    private static AudioSourceInfo CreateInfo(string codecName, string profileName = "", int channels = 2, int? bitDepth = 24)
    {
        return new AudioSourceInfo(codecName, profileName, channels, string.Empty, 48000, bitDepth, null);
    }
}
