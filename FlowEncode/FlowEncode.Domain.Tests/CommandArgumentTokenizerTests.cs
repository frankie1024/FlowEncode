using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class CommandArgumentTokenizerTests
{
    [TestMethod]
    public void Tokenize_WhenQuotedValuesAppear_RemovesQuoteDelimiters()
    {
        var tokens = CommandArgumentTokenizer.Tokenize(
            "--path \"D:\\Encode Jobs\\input.mkv\" --name='A B' --title \"a \\\"quoted\\\" value\"");

        CollectionAssert.AreEqual(
            new[]
            {
                "--path",
                @"D:\Encode Jobs\input.mkv",
                "--name=A B",
                "--title",
                "a \"quoted\" value"
            },
            tokens.ToArray());
    }

    [TestMethod]
    public void Tokenize_WhenStrictAndQuoteIsUnclosed_Throws()
    {
        try
        {
            CommandArgumentTokenizer.Tokenize("--name \"unfinished", throwOnUnclosedQuote: true);
            Assert.Fail("Expected an InvalidOperationException for an unclosed quote.");
        }
        catch (InvalidOperationException)
        {
        }
    }
}
