using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ParallelVideoAv1anArgumentBuilderTests
{
    [TestMethod]
    public void BuildCommand_WhenX264Crf_IncludesX264VideoParameters()
    {
        var request = CreateRequest(EncoderKind.X264, crf: 18.5, videoParameters: "--keyint 240");

        var command = ParallelVideoAv1anArgumentBuilder.BuildCommand(request, "av1an.exe", "temp", "out.mkv");

        CollectionAssert.Contains(command.Arguments.ToArray(), "--encoder");
        CollectionAssert.Contains(command.Arguments.ToArray(), "x264");
        var videoParams = GetArgumentValue(command.Arguments, "--video-params");
        StringAssert.Contains(videoParams, "--crf 18.5");
        StringAssert.Contains(videoParams, "--preset slow");
        StringAssert.Contains(videoParams, "--keyint 240");
        StringAssert.Contains(command.DisplayCommand, "av1an.exe");
    }

    [TestMethod]
    public void BuildCommand_WhenX265Crf_IncludesX265Encoder()
    {
        var request = CreateRequest(EncoderKind.X265, crf: 20.0);

        var command = ParallelVideoAv1anArgumentBuilder.BuildCommand(request, "av1an.exe", "temp", "out.mkv");

        CollectionAssert.Contains(command.Arguments.ToArray(), "x265");
        StringAssert.Contains(GetArgumentValue(command.Arguments, "--video-params"), "--crf 20.0");
    }

    [TestMethod]
    public void BuildCommand_WhenX265SourceHasColorMetadata_IncludesVideoMetadataParameters()
    {
        var request = CreateRequest(EncoderKind.X265, crf: 20.0);
        var sourceInfo = new SourceVideoInfo(
            3840,
            2160,
            2400,
            10,
            24000,
            1001,
            "yuv420p10le",
            "tv",
            "bt2020",
            "smpte2084",
            "bt2020nc");

        var command = ParallelVideoAv1anArgumentBuilder.BuildCommand(request, "av1an.exe", "temp", "out.mkv", sourceInfo);

        var videoParams = GetArgumentValue(command.Arguments, "--video-params");
        StringAssert.Contains(videoParams, "--range limited");
        StringAssert.Contains(videoParams, "--colorprim bt2020");
        StringAssert.Contains(videoParams, "--transfer smpte2084");
        StringAssert.Contains(videoParams, "--colormatrix bt2020nc");
    }

    [TestMethod]
    public void BuildCommand_WhenSvtAv1Crf_IncludesSvtRateControlArguments()
    {
        var request = CreateRequest(EncoderKind.SvtAv1, crf: 30.0, preset: "6");
        var sourceInfo = new SourceVideoInfo(1920, 1080, 2400, 10, 24000, 1001, "yuv420p10le");

        var command = ParallelVideoAv1anArgumentBuilder.BuildCommand(request, "av1an.exe", "temp", "out.mkv", sourceInfo);

        CollectionAssert.Contains(command.Arguments.ToArray(), "svt-av1");
        var videoParams = GetArgumentValue(command.Arguments, "--video-params");
        StringAssert.Contains(videoParams, "--rc 0");
        StringAssert.Contains(videoParams, "--crf 30.0");
        StringAssert.Contains(videoParams, "--preset 6");
        StringAssert.Contains(videoParams, "--width 1920");
        StringAssert.Contains(videoParams, "--height 1080");
    }

    [TestMethod]
    public void BuildCommand_WhenSvtAv1SourceHasColorMetadata_IncludesVideoMetadataParameters()
    {
        var request = CreateRequest(EncoderKind.SvtAv1, crf: 30.0, preset: "6");
        var sourceInfo = new SourceVideoInfo(
            3840,
            2160,
            2400,
            10,
            24000,
            1001,
            "yuv420p10le",
            "tv",
            "bt2020",
            "smpte2084",
            "bt2020nc",
            "left");

        var command = ParallelVideoAv1anArgumentBuilder.BuildCommand(request, "av1an.exe", "temp", "out.mkv", sourceInfo);

        var videoParams = GetArgumentValue(command.Arguments, "--video-params");
        StringAssert.Contains(videoParams, "--color-range 0");
        StringAssert.Contains(videoParams, "--color-primaries 9");
        StringAssert.Contains(videoParams, "--transfer-characteristics 16");
        StringAssert.Contains(videoParams, "--matrix-coefficients 9");
        StringAssert.Contains(videoParams, "--chroma-sample-position left");
    }

    [TestMethod]
    public void BuildCommand_WhenWorkersAreNull_DoesNotIncludeWorkers()
    {
        var request = CreateRequest(EncoderKind.X264, workers: null);

        var command = ParallelVideoAv1anArgumentBuilder.BuildCommand(request, "av1an.exe", "temp", "out.mkv");

        CollectionAssert.DoesNotContain(command.Arguments.ToArray(), "--workers");
    }

    [TestMethod]
    public void BuildCommand_WhenWorkersAreSpecified_IncludesWorkers()
    {
        var request = CreateRequest(EncoderKind.X264, workers: 4);

        var command = ParallelVideoAv1anArgumentBuilder.BuildCommand(request, "av1an.exe", "temp", "out.mkv");

        Assert.AreEqual("4", GetArgumentValue(command.Arguments, "--workers"));
    }

    [TestMethod]
    public void CreateTemporaryRawLogPath_DoesNotPlaceLogInsideAv1anTempDirectory()
    {
        var jobId = Guid.NewGuid();
        var request = new EncodingJobRequest(
            jobId,
            new EncodingProfile(
                EncoderKind.X264,
                "Test",
                string.Empty,
                "slow",
                string.Empty,
                string.Empty,
                RateControlMode.Crf,
                18.0,
                null,
                "mkv",
                string.Empty,
                string.Empty),
            "source.mkv",
            "output.mkv",
            InputPipelineKind.FfmpegPipe,
            EncoderArchitecture.X64,
            UseAv1anParallelVideoEncoding: true);
        var av1anTempDirectory = Path.Combine(
            "D:\\work",
            ".flowencode-temp",
            "av1an-parallel",
            jobId.ToString("N"));

        var rawLogPath = ParallelVideoEncodingAv1anRunner.CreateTemporaryRawLogPath(request, av1anTempDirectory);

        var tempPrefix = Path.GetFullPath(av1anTempDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.IsFalse(
            Path.GetFullPath(rawLogPath).StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase),
            "FlowEncode raw logs must not be opened inside Av1an's temp directory because Av1an deletes that directory before exiting.");
    }

    [TestMethod]
    [DataRow("--pass 1", "--pass")]
    [DataRow("--stats stats.log", "--stats")]
    [DataRow("-o output.264", "-o")]
    [DataRow("--input source.y4m", "--input")]
    [DataRow("-b output.ivf", "-b")]
    public void BuildCommand_WhenForbiddenArgumentIsPresent_Throws(string arguments, string forbiddenOption)
    {
        var request = CreateRequest(EncoderKind.X265, videoParameters: arguments);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ParallelVideoAv1anArgumentBuilder.BuildCommand(request, "av1an.exe", "temp", "out.mkv"));
        StringAssert.Contains(exception.Message, forbiddenOption);
    }

    private static ParallelVideoEncodingRequest CreateRequest(
        EncoderKind encoderKind,
        double crf = 18.0,
        string preset = "slow",
        int? workers = null,
        string videoParameters = "")
    {
        return new ParallelVideoEncodingRequest(
            Guid.NewGuid(),
            "source.mkv",
            "output.mkv",
            encoderKind,
            crf,
            preset,
            string.Empty,
            string.Empty,
            videoParameters,
            string.Empty,
            workers,
            InputPipelineKind.FfmpegPipe,
            EncoderArchitecture.X64);
    }

    private static string GetArgumentValue(IReadOnlyList<string> arguments, string option)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], option, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        Assert.IsTrue(index >= 0, $"Option '{option}' was not found.");
        Assert.IsTrue(index + 1 < arguments.Count, $"Option '{option}' does not have a value.");
        return arguments[index + 1];
    }
}
