using System.Text;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ProcessOutputPumpTests
{
    [TestMethod]
    public async Task PumpLinesAsync_WithRawMode_PreservesAnsiEscapeSequencesAndFlushesTailLine()
    {
        const string text = "Encoding: \u001b[33m114/5400 Frames\u001b[0m\r\nx265 3025 frames @ 46.49 fps | 13974 kb/s | 220.4 MB";
        using var reader = CreateReader(text);
        var lines = new List<string>();

        await ProcessOutputPump.PumpLinesAsync(reader, lines.Add, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                "Encoding: \u001b[33m114/5400 Frames\u001b[0m",
                "x265 3025 frames @ 46.49 fps | 13974 kb/s | 220.4 MB"
            },
            lines);
    }

    [TestMethod]
    public async Task PumpLinesAsync_WithNormalization_PreservesEscapeUntilNormalizerStripsAnsi()
    {
        const string text = "\u001b[33mEncoding:\u001b[0m 114/5400 Frames\r\n\r\n";
        using var reader = CreateReader(text);
        var lines = new List<string>();
        var options = new ProcessOutputPumpOptions(
            StripControlCharacters: true,
            PreserveEscape: true,
            NormalizeLine: ConsoleOutputLineNormalizer.Normalize);

        await ProcessOutputPump.PumpLinesAsync(reader, lines.Add, CancellationToken.None, options);

        CollectionAssert.AreEqual(
            new[]
            {
                "Encoding: 114/5400 Frames"
            },
            lines);
    }

    private static StreamReader CreateReader(string text)
    {
        return new StreamReader(
            new MemoryStream(Encoding.UTF8.GetBytes(text)),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: false);
    }
}
