namespace FlowEncode.Infrastructure;

internal static class CommandLineDisplay
{
    private static readonly char[] QuotedArgumentCharacters =
    [
        ' ',
        '\t',
        '"',
        '\'',
        ';',
        '|',
        '&',
        '<',
        '>',
        '(',
        ')'
    ];

    public static string JoinArguments(IEnumerable<string> arguments)
    {
        return string.Join(' ', arguments.Select(FormatArgument));
    }

    public static string FormatArgument(string value)
    {
        return ShouldQuote(value)
            ? Quote(value)
            : value;
    }

    public static string Quote(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 2);
        builder.Append('"');

        var backslashCount = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount);
                backslashCount = 0;
            }

            builder.Append(character);
        }

        if (backslashCount > 0)
        {
            builder.Append('\\', backslashCount * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static bool ShouldQuote(string value)
    {
        return value.Length == 0
            || value.IndexOfAny(QuotedArgumentCharacters) >= 0
            || Path.IsPathFullyQualified(value);
    }
}
