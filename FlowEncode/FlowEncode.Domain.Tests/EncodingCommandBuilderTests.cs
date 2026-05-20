using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class EncodingCommandBuilderTests
{
    private string _testRoot = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "FlowEncodeEncodingCommandBuilderTests", Guid.NewGuid().ToString("N"));
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
    public void BuildPlan_WhenX264TwoPass_ReturnsTwoExecutionSteps()
    {
        var builder = CreateBuilder();
        var request = CreateRequest(EncoderKind.X264, RateControlMode.TwoPass, bitrate: 4500, outputContainer: "264");

        var plan = builder.BuildPlan(
            request,
            encoderPath: "x264.exe",
            pipelineKind: InputPipelineKind.Y4mFile,
            sourceInfo: null,
            statsPath: "stats.log");

        Assert.AreEqual(2, plan.Steps.Count);
        CollectionAssert.AreEqual(new[] { 1, 2 }, plan.Steps.Select(static step => step.StageIndex).ToArray());
        Assert.AreEqual("NUL", plan.Steps[0].EncoderCommand.Arguments[^1]);
        Assert.AreEqual(request.OutputPath, plan.Steps[1].EncoderCommand.Arguments[^1]);
        CollectionAssert.Contains(plan.Steps[0].EncoderCommand.Arguments.ToArray(), "--pass");
        CollectionAssert.Contains(plan.Steps[1].EncoderCommand.Arguments.ToArray(), "--pass");
        CollectionAssert.Contains(plan.Steps[0].EncoderCommand.Arguments.ToArray(), "stats.log");
    }

    [TestMethod]
    public void BuildPlan_WhenSvtTwoPassWithoutSourceInfo_Throws()
    {
        var builder = CreateBuilder();
        var request = CreateRequest(EncoderKind.SvtAv1, RateControlMode.TwoPass, bitrate: 3200, outputContainer: "ivf");

        Assert.ThrowsExactly<InvalidOperationException>(() => builder.BuildPlan(
            request,
            encoderPath: "SvtAv1EncApp.exe",
            pipelineKind: InputPipelineKind.Y4mFile,
            sourceInfo: null,
            statsPath: "stats.log"));
    }

    [TestMethod]
    public void BuildPlan_WhenSvtTwoPassWithSourceInfo_ReturnsTwoExecutionSteps()
    {
        var builder = CreateBuilder();
        var request = CreateRequest(EncoderKind.SvtAv1, RateControlMode.TwoPass, bitrate: 3200, outputContainer: "ivf");
        var sourceInfo = new SourceVideoInfo(1920, 1080, 2400, 10, 24000, 1001, "yuv420p10le");

        var plan = builder.BuildPlan(
            request,
            encoderPath: "SvtAv1EncApp.exe",
            pipelineKind: InputPipelineKind.Y4mFile,
            sourceInfo: sourceInfo,
            statsPath: "stats.log");

        Assert.AreEqual(2, plan.Steps.Count);
        CollectionAssert.Contains(plan.Steps[0].EncoderCommand.Arguments.ToArray(), "--pass");
        CollectionAssert.Contains(plan.Steps[1].EncoderCommand.Arguments.ToArray(), "--pass");
        CollectionAssert.Contains(plan.Steps[0].EncoderCommand.Arguments.ToArray(), "stats.log");
        Assert.AreEqual(request.OutputPath, plan.Steps[1].EncoderCommand.Arguments[^1]);
    }

    [TestMethod]
    public void BuildPlan_WhenOutputOverrideProvided_UsesOverrideOnlyForFinalOutput()
    {
        var builder = CreateBuilder();
        var request = CreateRequest(EncoderKind.X265, RateControlMode.TwoPass, bitrate: 2600, outputContainer: "hevc");
        const string stagedOutputPath = @"D:\temp\.flowencode-temp\job\output.staging.tmp.hevc";

        var plan = builder.BuildPlan(
            request,
            encoderPath: "x265.exe",
            pipelineKind: InputPipelineKind.Y4mFile,
            sourceInfo: null,
            statsPath: "stats.log",
            outputPathOverride: stagedOutputPath);

        Assert.AreEqual("NUL", plan.Steps[0].EncoderCommand.Arguments[^1]);
        Assert.AreEqual(stagedOutputPath, plan.Steps[1].EncoderCommand.Arguments[^1]);
        StringAssert.Contains(plan.DisplayCommand, stagedOutputPath);
        CollectionAssert.Contains(plan.CleanupPaths!.ToArray(), "stats.log");
    }

    private EncodingCommandBuilder CreateBuilder()
    {
        var paths = new LocalAppPaths(_testRoot, _testRoot);
        return new EncodingCommandBuilder(new ExternalToolLocator(paths));
    }

    private static EncodingJobRequest CreateRequest(
        EncoderKind kind,
        RateControlMode rateControl,
        int? bitrate,
        string outputContainer)
    {
        var profile = new EncodingProfile(
            kind,
            "Test",
            string.Empty,
            "slow",
            string.Empty,
            string.Empty,
            rateControl,
            18.0,
            bitrate,
            outputContainer,
            string.Empty,
            string.Empty);

        return new EncodingJobRequest(
            Guid.NewGuid(),
            profile,
            SourcePath: "input.y4m",
            OutputPath: $"output.{outputContainer}",
            PipelineKind: InputPipelineKind.Y4mFile,
            PreferredArchitecture: EncoderArchitecture.X64);
    }
}
