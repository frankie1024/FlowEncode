using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LocalEncodingJobRunnerTests
{
    [TestMethod]
    public void TokenizeCommandLine_PreservesQuotedPathAndCompositeValues()
    {
        var commandLine = "--crf 16 --dhdr10-info \"D:\\hdr meta\\hdr10plus.json\" --master-display \"G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)\" --max-cll 1000,400";

        var tokens = EncodingCommandBuilder.TokenizeCommandLine(commandLine);

        CollectionAssert.AreEqual(
            new[]
            {
                "--crf",
                "16",
                "--dhdr10-info",
                "D:\\hdr meta\\hdr10plus.json",
                "--master-display",
                "G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)",
                "--max-cll",
                "1000,400"
            },
            tokens.ToArray());
    }

    [TestMethod]
    public void TokenizeCommandLine_PreservesLegacySingleQuotedValues()
    {
        var commandLine = "--dhdr10-info 'D:\\hdr meta\\hdr10plus.json' --zones '0,100,b=0.8'";

        var tokens = EncodingCommandBuilder.TokenizeCommandLine(commandLine);

        CollectionAssert.AreEqual(
            new[]
            {
                "--dhdr10-info",
                "D:\\hdr meta\\hdr10plus.json",
                "--zones",
                "0,100,b=0.8"
            },
            tokens.ToArray());
    }

    [TestMethod]
    public void CreateProcess_UsesArgumentListWithoutShellQuotingArtifacts()
    {
        var command = new ProcessCommand(
            "x265.exe",
            [
                "--input",
                "-",
                "--stats",
                "D:\\temp path\\x265 pass.log",
                "--dhdr10-info",
                "D:\\hdr meta\\hdr10plus.json"
            ],
            "\"x265.exe\" --input -");

        using var process = LocalEncodingJobRunner.CreateProcess(command, "D:\\encoders\\x265.exe", redirectStandardInput: true);

        Assert.AreEqual("x265.exe", process.StartInfo.FileName);
        Assert.IsFalse(process.StartInfo.UseShellExecute);
        Assert.IsTrue(process.StartInfo.RedirectStandardInput);
        CollectionAssert.AreEqual(command.Arguments.ToArray(), process.StartInfo.ArgumentList.ToArray());
    }
}
