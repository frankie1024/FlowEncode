using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class DeewStageTimeEstimateTests
{
    [TestMethod]
    public void Estimate_MatchesUpstreamExamplesAfterFiveSeconds()
    {
        var clock = new ManualClock();
        var estimate = new DeewStageTimeEstimate(clock);
        estimate.Update("measure", 4);
        clock.Advance(4.99);
        Assert.IsNull(estimate.Remaining);
        clock.Advance(.01);
        Assert.AreEqual(TimeSpan.FromSeconds(120), estimate.Remaining);
        clock.Advance(3);
        Assert.AreEqual(TimeSpan.FromSeconds(192), estimate.Remaining);

        estimate.Update("ffmpeg", 63.01);
        Assert.IsNull(estimate.Remaining);
        clock.Advance(14);
        Assert.AreEqual(TimeSpan.FromSeconds(8), estimate.Remaining);
    }

    [TestMethod]
    public void EachStage_HasItsOwnClockAndEstimate()
    {
        var clock = new ManualClock();
        var estimate = new DeewStageTimeEstimate(clock);
        foreach (var stage in new[] { "ffmpeg", "decode", "measure", "encode" })
        {
            estimate.Update(stage, 25);
            Assert.IsNull(estimate.Remaining);
            clock.Advance(8);
            Assert.AreEqual(TimeSpan.FromSeconds(24), estimate.Remaining);
            estimate.Update(stage, 50);
            Assert.AreEqual(TimeSpan.FromSeconds(8), estimate.Remaining);
        }
    }

    [TestMethod]
    public void UnknownProgress_HidesEstimateWithoutRestartingTheStage()
    {
        var clock = new ManualClock();
        var estimate = new DeewStageTimeEstimate(clock);
        estimate.Update("encode", null);
        clock.Advance(10);
        Assert.IsNull(estimate.Remaining);
        estimate.Update("encode", 50);
        Assert.AreEqual(TimeSpan.FromSeconds(10), estimate.Remaining);
        estimate.Update("encode", null);
        Assert.IsNull(estimate.Remaining);
    }

    [TestMethod]
    public void InvalidCompletedOrExcessiveEstimates_AreHidden()
    {
        var clock = new ManualClock();
        var estimate = new DeewStageTimeEstimate(clock);
        estimate.Update("encode", 0);
        clock.Advance(10);
        foreach (var percent in new[] { -1.0, 0, 100, 101, double.NaN, double.PositiveInfinity, .001 })
        {
            estimate.Update("encode", percent);
            Assert.IsNull(estimate.Remaining, $"percent={percent}");
        }
        estimate.Update("starting", 50);
        clock.Advance(10);
        Assert.IsNull(estimate.Remaining);
        estimate.Update("future-stage", 50);
        clock.Advance(10);
        Assert.IsNull(estimate.Remaining);
    }

    [TestMethod]
    public void Reader_UsesRawStageProgressAndIgnoresOtherJobs()
    {
        var clock = new ManualClock();
        var reader = new DeewEventsReader(DeewArguments.Resolve(new(), 2), _ => { }, (_, _, _, _) => { }, clock);
        reader.Accept("""{"type":"progress","job":0,"stage":"measure","percent":0}""");
        clock.Advance(8);
        reader.Accept("""{"type":"progress","job":0,"stage":"measure","percent":4,"db":-27}""");
        Assert.AreEqual(.354, reader.Fraction, .000001);
        Assert.AreEqual(TimeSpan.FromSeconds(192), reader.Remaining);
        reader.Accept("""{"type":"progress","job":1,"stage":"encode","percent":90}""");
        Assert.AreEqual(TimeSpan.FromSeconds(192), reader.Remaining);
        reader.Accept("""{"type":"progress","job":0,"stage":"encode","percent":0}""");
        Assert.IsNull(reader.Remaining);
        clock.Advance(6);
        reader.Accept("""{"type":"progress","job":0,"stage":"encode","percent":50}""");
        Assert.AreEqual(TimeSpan.FromSeconds(6), reader.Remaining);
        reader.Accept("""{"type":"progress","job":0,"stage":"encode","percent":100}""");
        Assert.IsNull(reader.Remaining);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
