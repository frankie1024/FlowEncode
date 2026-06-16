using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class JsonlEventParserTests
{
    [TestMethod]
    public void TryParse_WithEncodeProgressEvent_ParsesStructuredEvent()
    {
        const string line = """
            {"type":"encode_progress","ts":"2026-05-20T15:54:05.578560100+00:00","fraction_done":12,"fraction_total":50,"chunk_index":13}
            """;

        var result = JsonlEventParser.TryParse(line, out var parsedEvent);

        Assert.IsTrue(result);
        Assert.IsNotNull(parsedEvent);
        Assert.AreEqual("encode_progress", parsedEvent.Type);
        Assert.AreEqual(AutoCompressionExecutionStage.Encoding, JsonlEventParser.MapStage(parsedEvent.Type));
    }

    [TestMethod]
    public void TryGetProgressFraction_WithEncodeProgressEvent_ReturnsFraction()
    {
        const string line = """
            {"type":"encode_progress","ts":"2026-05-20T15:54:05.578560100+00:00","fraction_done":25,"fraction_total":50,"chunk_index":29}
            """;

        JsonlEventParser.TryParse(line, out var parsedEvent);
        var fraction = JsonlEventParser.TryGetProgressFraction(parsedEvent!);

        Assert.IsNotNull(fraction);
        Assert.AreEqual(0.5, fraction.Value, 0.000001);
    }

    [TestMethod]
    public void BuildDetailLine_WithRunFailedEvent_UsesFailureMessage()
    {
        const string line = """
            {"type":"run_failed","ts":"2026-05-20T16:00:01.576139700+00:00","exit_code":1,"message":"Input path missing.vpy does not exist."}
            """;

        JsonlEventParser.TryParse(line, out var parsedEvent);
        var detail = JsonlEventParser.BuildDetailLine(parsedEvent!);

        Assert.AreEqual("run failed: Input path missing.vpy does not exist.", detail);
        Assert.AreEqual("Input path missing.vpy does not exist.", JsonlEventParser.TryGetFailureMessage(parsedEvent!));
    }

    [TestMethod]
    public void BuildDetailLine_WithChunkPlanEvent_IncludesChunkCount()
    {
        const string line = """
            {"type":"chunk_plan","ts":"2026-05-20T15:53:53.475810600+00:00","chunk_count":50}
            """;

        JsonlEventParser.TryParse(line, out var parsedEvent);
        var detail = JsonlEventParser.BuildDetailLine(parsedEvent!);

        Assert.AreEqual("chunk plan: 50 chunks", detail);
        Assert.AreEqual(AutoCompressionExecutionStage.ChunkPlanning, JsonlEventParser.MapStage(parsedEvent!.Type));
    }

    [TestMethod]
    public void BuildEncoderLogLines_WithEncoderLogEvent_ReturnsChunkLogBlock()
    {
        const string line = """
            {"type":"encoder_log","ts":"2026-06-16T06:40:00Z","chunk_index":3,"encoder":"x264","frames":120,"stderr":"x264 [info]: frame I:1    Avg QP:12.00\nencoded 120 frames, 45.00 fps, 5000.00 kb/s\n"}
            """;

        JsonlEventParser.TryParse(line, out var parsedEvent);
        var detail = JsonlEventParser.BuildDetailLine(parsedEvent!);
        var lines = JsonlEventParser.BuildEncoderLogLines(parsedEvent!);

        Assert.AreEqual("encoder log captured: chunk 3 (x264)", detail);
        Assert.AreEqual(AutoCompressionExecutionStage.Encoding, JsonlEventParser.MapStage(parsedEvent!.Type));
        CollectionAssert.AreEqual(
            new[]
            {
                "--- ENCODER LOG chunk 3 (x264, 120 frames) ---",
                "x264 [info]: frame I:1    Avg QP:12.00",
                "encoded 120 frames, 45.00 fps, 5000.00 kb/s"
            },
            lines.ToArray());
    }

    [TestMethod]
    public void BuildEncoderLogLines_WithX265EncoderLogEvent_ReturnsChunkLogBlock()
    {
        const string line = """
            {"type":"encoder_log","ts":"2026-06-16T06:40:00Z","chunk_index":4,"encoder":"x265","frames":90,"stderr":"x265 [info]: frame I:      1, Avg QP:18.00\nencoded 90 frames in 2.00s (45.00 fps), 4200.00 kb/s\n"}
            """;

        JsonlEventParser.TryParse(line, out var parsedEvent);
        var detail = JsonlEventParser.BuildDetailLine(parsedEvent!);
        var lines = JsonlEventParser.BuildEncoderLogLines(parsedEvent!);

        Assert.AreEqual("encoder log captured: chunk 4 (x265)", detail);
        CollectionAssert.AreEqual(
            new[]
            {
                "--- ENCODER LOG chunk 4 (x265, 90 frames) ---",
                "x265 [info]: frame I:      1, Avg QP:18.00",
                "encoded 90 frames in 2.00s (45.00 fps), 4200.00 kb/s"
            },
            lines.ToArray());
    }

    [TestMethod]
    public void BuildEncoderLogLines_WithSvtAv1EncoderLogEvent_ReturnsChunkLogBlock()
    {
        const string line = """
            {"type":"encoder_log","ts":"2026-06-16T06:40:00Z","chunk_index":5,"encoder":"svt-av1","frames":72,"stderr":"Encoding frame 72 45.00 fps\nSUMMARY --------------------------------- Channel 1\nTotal Frames\t\t\t72\n"}
            """;

        JsonlEventParser.TryParse(line, out var parsedEvent);
        var detail = JsonlEventParser.BuildDetailLine(parsedEvent!);
        var lines = JsonlEventParser.BuildEncoderLogLines(parsedEvent!);

        Assert.AreEqual("encoder log captured: chunk 5 (svt-av1)", detail);
        CollectionAssert.AreEqual(
            new[]
            {
                "--- ENCODER LOG chunk 5 (svt-av1, 72 frames) ---",
                "Encoding frame 72 45.00 fps",
                "SUMMARY --------------------------------- Channel 1",
                "Total Frames\t\t\t72"
            },
            lines.ToArray());
    }
}
