using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class CommandLineDisplayTests
{
    [TestMethod]
    public void FormatArgument_WhenValueContainsSpecialCharacters_QuotesAndEscapes()
    {
        var argument = "clip; John's \"sample\".wav";

        var formatted = CommandLineDisplay.FormatArgument(argument);

        Assert.AreEqual("\"clip; John's \\\"sample\\\".wav\"", formatted);
    }

    [TestMethod]
    public void JoinArguments_WhenPathContainsSpaces_QuotesOnlyDisplayText()
    {
        var command = CommandLineDisplay.JoinArguments(new[] { "-i", @"D:\Audio Jobs\input.wav", "-np" });

        Assert.AreEqual("-i \"D:\\Audio Jobs\\input.wav\" -np", command);
    }

    [TestMethod]
    public void FormatArgument_WhenQuotedPathEndsWithBackslash_DoublesTrailingBackslash()
    {
        var formatted = CommandLineDisplay.FormatArgument(@"D:\Audio Jobs\");

        Assert.AreEqual("\"D:\\Audio Jobs\\\\\"", formatted);
    }
}
