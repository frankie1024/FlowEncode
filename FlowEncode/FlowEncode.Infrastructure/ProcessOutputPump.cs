using System.Text;

namespace FlowEncode.Infrastructure;

internal sealed record ProcessOutputPumpOptions(
    bool StripControlCharacters = false,
    bool PreserveEscape = false,
    Func<string, string>? NormalizeLine = null);

internal static class ProcessOutputPump
{
    public static async Task PumpLinesAsync(
        StreamReader reader,
        Action<string> onLine,
        CancellationToken cancellationToken,
        ProcessOutputPumpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(onLine);

        var effectiveOptions = options ?? new ProcessOutputPumpOptions();
        var buffer = new char[512];
        var current = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var ch = buffer[index];
                if (ch is '\r' or '\n')
                {
                    Flush(current, onLine, effectiveOptions);
                    continue;
                }

                if (ShouldAppend(ch, effectiveOptions))
                {
                    current.Append(ch);
                }
            }
        }

        Flush(current, onLine, effectiveOptions);
    }

    private static bool ShouldAppend(char ch, ProcessOutputPumpOptions options)
    {
        if (!options.StripControlCharacters)
        {
            return true;
        }

        return !char.IsControl(ch)
            || ch == '\t'
            || (options.PreserveEscape && ch == '\u001B');
    }

    private static void Flush(
        StringBuilder current,
        Action<string> onLine,
        ProcessOutputPumpOptions options)
    {
        if (current.Length == 0)
        {
            return;
        }

        var line = current.ToString();
        current.Clear();

        if (options.NormalizeLine is not null)
        {
            line = options.NormalizeLine(line);
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }
        }

        onLine(line);
    }
}
