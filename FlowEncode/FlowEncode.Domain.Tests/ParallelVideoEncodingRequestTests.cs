using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ParallelVideoEncodingRequestTests
{
    [TestMethod]
    public void ValidateParallelVideoEncodingRequest_WhenWorkersAreNull_AllowsAutoWorkers()
    {
        var request = CreateRequest(workers: null);

        RequestValidation.ValidateParallelVideoEncodingRequest(request);
    }

    [TestMethod]
    public void ValidateParallelVideoEncodingRequest_WhenWorkersAreZero_Throws()
    {
        var request = CreateRequest(workers: 0);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RequestValidation.ValidateParallelVideoEncodingRequest(request));
    }

    [TestMethod]
    public void ValidateParallelVideoEncodingRequest_WhenCrfIsNan_Throws()
    {
        var request = CreateRequest(crf: double.NaN);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RequestValidation.ValidateParallelVideoEncodingRequest(request));
    }

    [TestMethod]
    public void ValidateEncodingJobRequest_WhenParallelModeUsesNonCrf_Throws()
    {
        var profile = new EncodingProfile(
            EncoderKind.X265,
            "Test",
            string.Empty,
            "slow",
            string.Empty,
            string.Empty,
            RateControlMode.TwoPass,
            18.0,
            4000,
            "mkv",
            string.Empty,
            string.Empty);
        var request = new EncodingJobRequest(
            Guid.NewGuid(),
            profile,
            "source.mkv",
            "output.mkv",
            InputPipelineKind.FfmpegPipe,
            EncoderArchitecture.X64,
            UseAv1anParallelVideoEncoding: true);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RequestValidation.ValidateEncodingJobRequest(request));
    }

    [TestMethod]
    public void CreateParallelVideoEncodingRequest_WhenParallelEncodingEnabled_MapsProfileFields()
    {
        var profile = new EncodingProfile(
            EncoderKind.SvtAv1,
            "Test",
            string.Empty,
            "6",
            string.Empty,
            "main",
            RateControlMode.Crf,
            28.0,
            null,
            "mkv",
            "--film-grain 8",
            string.Empty);
        var request = new EncodingJobRequest(
            Guid.NewGuid(),
            profile,
            "source.mkv",
            "output.mkv",
            InputPipelineKind.FfmpegPipe,
            EncoderArchitecture.X64,
            UseAv1anParallelVideoEncoding: true,
            Av1anParallelWorkers: 3);

        var parallelRequest = RequestValidation.CreateParallelVideoEncodingRequest(request);

        Assert.AreEqual(request.JobId, parallelRequest.JobId);
        Assert.AreEqual(EncoderKind.SvtAv1, parallelRequest.EncoderKind);
        Assert.AreEqual(28.0, parallelRequest.Crf);
        Assert.AreEqual(3, parallelRequest.Workers);
        Assert.AreEqual("--film-grain 8", parallelRequest.VideoParameters);
    }

    private static ParallelVideoEncodingRequest CreateRequest(
        EncoderKind encoderKind = EncoderKind.X264,
        double crf = 18.0,
        int? workers = null,
        string videoParameters = "")
    {
        return new ParallelVideoEncodingRequest(
            Guid.NewGuid(),
            "source.mkv",
            "output.mkv",
            encoderKind,
            crf,
            "slow",
            string.Empty,
            string.Empty,
            videoParameters,
            string.Empty,
            workers,
            InputPipelineKind.FfmpegPipe,
            EncoderArchitecture.X64);
    }
}
