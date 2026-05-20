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
}
