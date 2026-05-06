using System;
using System.Linq;

namespace FlowEncode.Infrastructure;

internal static class MeaningfulLineHelpers
{
    public static string FirstMeaningfulLine(string value, string fallback)
    {
        return value
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? fallback;
    }
}
