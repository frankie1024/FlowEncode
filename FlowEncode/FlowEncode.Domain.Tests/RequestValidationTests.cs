using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class RequestValidationTests
{
    [TestMethod]
    public void ValidateAutoCompressionRequest_WhenTargetVmafIsNan_Throws()
    {
        var request = new AutoCompressionRequest(
            Guid.NewGuid(),
            @"D:\input.mkv",
            @"D:\out.mkv",
            EncoderKind.X265,
            AutoCompressionMetric.Vmaf,
            double.NaN,
            4,
            string.Empty,
            string.Empty,
            null);

        AssertThrows<ArgumentOutOfRangeException>(() => RequestValidation.ValidateAutoCompressionRequest(request));
    }

    [TestMethod]
    public void ValidateAutoCompressionRequest_WhenProbeCountIsZero_Throws()
    {
        var request = new AutoCompressionRequest(
            Guid.NewGuid(),
            @"D:\input.mkv",
            @"D:\out.mkv",
            EncoderKind.X265,
            AutoCompressionMetric.Vmaf,
            95,
            0,
            string.Empty,
            string.Empty,
            null);

        AssertThrows<ArgumentOutOfRangeException>(() => RequestValidation.ValidateAutoCompressionRequest(request));
    }

    [TestMethod]
    public void ValidateEncodingJobRequest_WhenProfileBitrateIsNegative_Throws()
    {
        var request = new EncodingJobRequest(
            Guid.NewGuid(),
            CreateProfile() with
            {
                RateControl = RateControlMode.Abr,
                Bitrate = -1
            },
            @"D:\input.mkv",
            @"D:\out.264",
            InputPipelineKind.Y4mFile,
            EncoderArchitecture.X64);

        AssertThrows<ArgumentOutOfRangeException>(() => RequestValidation.ValidateEncodingJobRequest(request));
    }

    [TestMethod]
    public void ValidateEncodingJobRequest_WhenSourcePathIsEmpty_Throws()
    {
        var request = new EncodingJobRequest(
            Guid.NewGuid(),
            CreateProfile(),
            string.Empty,
            @"D:\out.264",
            InputPipelineKind.Y4mFile,
            EncoderArchitecture.X64);

        AssertThrows<ArgumentException>(() => RequestValidation.ValidateEncodingJobRequest(request));
    }

    [TestMethod]
    public void ValidateEncodingProfile_WhenQualityIsNan_Throws()
    {
        var profile = CreateProfile() with
        {
            Quality = double.NaN
        };

        AssertThrows<ArgumentOutOfRangeException>(() => RequestValidation.ValidateEncodingProfile(profile));
    }

    [TestMethod]
    public void ValidateAudioProcessingRequest_WhenOpusBitrateIsZero_Throws()
    {
        var request = new AudioProcessingRequest(
            Guid.NewGuid(),
            @"D:\input.wav",
            @"D:\out.opus",
            AudioProcessingMode.Opus,
            null,
            [],
            1,
            2,
            "stereo",
            0,
            false);

        AssertThrows<ArgumentOutOfRangeException>(() => RequestValidation.ValidateAudioProcessingRequest(request));
    }

    [TestMethod]
    public void NormalizeAppSettings_ClampsConcurrentJobs()
    {
        var settings = RequestValidation.NormalizeAppSettings(AppSettings.Default with
        {
            MaxConcurrentEncodingJobs = 99
        });

        Assert.AreEqual(RequestValidation.MaxConcurrentEncodingJobs, settings.MaxConcurrentEncodingJobs);
    }

    [TestMethod]
    public void NormalizeConcurrentEncodingJobs_WhenValueIsNan_ReturnsMinimum()
    {
        var normalized = RequestValidation.NormalizeConcurrentEncodingJobs(double.NaN);

        Assert.AreEqual(RequestValidation.MinConcurrentEncodingJobs, normalized);
    }

    [TestMethod]
    public void NormalizeConcurrentEncodingJobs_WhenValueIsHuge_ReturnsMaximum()
    {
        var normalized = RequestValidation.NormalizeConcurrentEncodingJobs(double.MaxValue);

        Assert.AreEqual(RequestValidation.MaxConcurrentEncodingJobs, normalized);
    }

    private static EncodingProfile CreateProfile()
    {
        return new EncodingProfile(
            EncoderKind.X264,
            "Test",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            RateControlMode.Crf,
            18,
            null,
            ".264",
            string.Empty,
            string.Empty);
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            Assert.Fail($"Expected {typeof(TException).Name}.");
        }
        catch (TException)
        {
        }
    }
}
