using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LocalEncodingJobRunnerVisibleLogTests
{
    [TestMethod]
    public void TrimVisibleLogForTesting_WhenLogIsShort_ReturnsOriginalText()
    {
        const string text = "line-1\r\nline-2";

        var trimmed = EncodingJobLogWriter.TrimVisibleLogForTesting(text);

        Assert.AreEqual(text, trimmed);
    }

    [TestMethod]
    public void TrimVisibleLogForTesting_WhenLogExceedsLimit_PrependsMarkerOnce()
    {
        var oversized = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 30_000).Select(index => $"line-{index:00000}"));

        var trimmed = EncodingJobLogWriter.TrimVisibleLogForTesting(oversized);

        StringAssert.StartsWith(trimmed, "[Log truncated; only latest output is kept]");
        Assert.AreEqual(1, CountOccurrences(trimmed, "[Log truncated; only latest output is kept]"));
        Assert.IsTrue(trimmed.Length <= 120_000 + 128);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;

        while ((startIndex = text.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }
}
