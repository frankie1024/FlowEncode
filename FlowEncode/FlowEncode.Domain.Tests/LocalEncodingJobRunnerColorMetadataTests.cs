using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LocalEncodingJobRunnerColorMetadataTests
{
    [TestMethod]
    public void BuildEncoderColorMetadataArguments_WithBt2020PqSource_MapsX265Metadata()
    {
        var sourceInfo = CreateSourceInfo(
            colorRange: "tv",
            colorPrimaries: "bt2020",
            colorTransfer: "smpte2084",
            colorMatrix: "bt2020nc");

        var arguments = EncodingCommandBuilder.BuildEncoderColorMetadataArguments(
            EncoderKind.X265,
            sourceInfo,
            additionalArguments: string.Empty,
            x265UhdParameters: string.Empty);

        Assert.AreEqual("--range limited --colorprim bt2020 --transfer smpte2084 --colormatrix bt2020nc", arguments);
    }

    [TestMethod]
    public void BuildEncoderColorMetadataArguments_WithBt2020PqSource_MapsSvtMetadata()
    {
        var sourceInfo = CreateSourceInfo(
            colorRange: "tv",
            colorPrimaries: "bt2020",
            colorTransfer: "smpte2084",
            colorMatrix: "bt2020nc",
            chromaLocation: "left");

        var arguments = EncodingCommandBuilder.BuildEncoderColorMetadataArguments(
            EncoderKind.SvtAv1,
            sourceInfo,
            additionalArguments: string.Empty,
            x265UhdParameters: string.Empty);

        Assert.AreEqual("--color-range 0 --color-primaries 9 --transfer-characteristics 16 --matrix-coefficients 9 --chroma-sample-position left", arguments);
    }

    [TestMethod]
    public void BuildEncoderColorMetadataArguments_WhenUserProvidesSameOption_KeepsUserOverride()
    {
        var sourceInfo = CreateSourceInfo(
            colorRange: "tv",
            colorPrimaries: "bt2020",
            colorTransfer: "smpte2084",
            colorMatrix: "bt2020nc");

        var arguments = EncodingCommandBuilder.BuildEncoderColorMetadataArguments(
            EncoderKind.X265,
            sourceInfo,
            additionalArguments: "--colorprim bt709",
            x265UhdParameters: string.Empty);

        Assert.AreEqual("--range limited --transfer smpte2084 --colormatrix bt2020nc", arguments);
    }

    [TestMethod]
    public void BuildEncoderColorMetadataArguments_WhenX265SignalPresetIsManual_SkipsAutomaticMetadata()
    {
        var sourceInfo = CreateSourceInfo(
            colorRange: "tv",
            colorPrimaries: "bt2020",
            colorTransfer: "smpte2084",
            colorMatrix: "bt2020nc");

        var arguments = EncodingCommandBuilder.BuildEncoderColorMetadataArguments(
            EncoderKind.X265,
            sourceInfo,
            additionalArguments: string.Empty,
            x265UhdParameters: "--video-signal-type-preset BT2100_PQ_YCC");

        Assert.AreEqual(string.Empty, arguments);
    }

    private static SourceVideoInfo CreateSourceInfo(
        string? colorRange = null,
        string? colorPrimaries = null,
        string? colorTransfer = null,
        string? colorMatrix = null,
        string? chromaLocation = null)
    {
        return new SourceVideoInfo(
            3840,
            2160,
            1000,
            10,
            24000,
            1001,
            "yuv420p10le",
            colorRange,
            colorPrimaries,
            colorTransfer,
            colorMatrix,
            chromaLocation);
    }
}
