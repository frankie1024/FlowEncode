namespace FlowEncode.Domain;

public static class CommandArgumentTokenizer
{
    public static IReadOnlyList<string> Tokenize(string? value, bool throwOnUnclosedQuote = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var tokens = new List<string>();
        var builder = new System.Text.StringBuilder();
        char? quote = null;
        var tokenStarted = false;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (quote.HasValue)
            {
                if (character == '\\'
                    && index + 1 < value.Length
                    && value[index + 1] == quote.Value)
                {
                    builder.Append(quote.Value);
                    tokenStarted = true;
                    index++;
                    continue;
                }

                if (character == quote.Value)
                {
                    quote = null;
                    tokenStarted = true;
                    continue;
                }

                builder.Append(character);
                tokenStarted = true;
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                FlushToken(tokens, builder, ref tokenStarted);
                continue;
            }

            builder.Append(character);
            tokenStarted = true;
        }

        if (quote.HasValue && throwOnUnclosedQuote)
        {
            throw new InvalidOperationException("Command arguments contain an unclosed quote.");
        }

        FlushToken(tokens, builder, ref tokenStarted);
        return tokens;
    }

    public static bool TryParseInlineOption(string token, out string optionName, out string value)
    {
        optionName = string.Empty;
        value = string.Empty;

        var separatorIndex = token.IndexOf('=');
        if (separatorIndex <= 2)
        {
            return false;
        }

        optionName = token[..separatorIndex];
        value = token[(separatorIndex + 1)..];
        return optionName.StartsWith("--", StringComparison.Ordinal);
    }

    public static bool TryParseInlineOption(string token, string expectedOptionName, out string value)
    {
        value = string.Empty;
        if (!token.StartsWith(expectedOptionName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (token.Length == expectedOptionName.Length)
        {
            return false;
        }

        if (token[expectedOptionName.Length] != '=')
        {
            return false;
        }

        value = token[(expectedOptionName.Length + 1)..].Trim();
        return true;
    }

    public static bool TryReadValue(IReadOnlyList<string> tokens, int optionIndex, out string value)
    {
        value = string.Empty;
        if (optionIndex + 1 >= tokens.Count)
        {
            return false;
        }

        value = tokens[optionIndex + 1];
        return !IsOptionToken(value);
    }

    public static bool IsOptionToken(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal)
            || (value.StartsWith("-", StringComparison.Ordinal)
                && value.Length > 1
                && char.IsLetter(value[1]));
    }

    private static void FlushToken(ICollection<string> tokens, System.Text.StringBuilder builder, ref bool tokenStarted)
    {
        if (!tokenStarted)
        {
            return;
        }

        tokens.Add(builder.ToString());
        builder.Clear();
        tokenStarted = false;
    }
}
