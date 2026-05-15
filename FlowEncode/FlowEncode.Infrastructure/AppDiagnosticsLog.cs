using FlowEncode.Application;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FlowEncode.Infrastructure;

internal static class AppDiagnosticsLog
{
    private static readonly object SyncRoot = new();
    private const string FileName = "diagnostics.log";

    public static void Write(
        LocalAppPaths appPaths,
        string source,
        string message,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Information,
        IReadOnlyDictionary<string, string?>? context = null,
        Exception? exception = null)
    {
        if (string.IsNullOrWhiteSpace(message) && exception is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(appPaths.LogsRootPath);
            var path = Path.Combine(appPaths.LogsRootPath, FileName);
            var line = FormatLine(source, message, severity, context, exception);

            lock (SyncRoot)
            {
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write app diagnostic log. {ex}");
        }
    }

    private static string FormatLine(
        string source,
        string message,
        AppDiagnosticSeverity severity,
        IReadOnlyDictionary<string, string?>? context,
        Exception? exception)
    {
        var builder = new StringBuilder();
        builder.Append(
            string.Create(
                CultureInfo.InvariantCulture,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {severity} {NormalizeSource(source)}: {message?.Trim()}"));

        AppendContext(builder, context);

        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append(exception);
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private static string NormalizeSource(string source)
    {
        return string.IsNullOrWhiteSpace(source)
            ? "FlowEncode"
            : source.Trim();
    }

    private static void AppendContext(StringBuilder builder, IReadOnlyDictionary<string, string?>? context)
    {
        if (context is null || context.Count == 0)
        {
            return;
        }

        foreach (var field in context)
        {
            if (string.IsNullOrWhiteSpace(field.Key))
            {
                continue;
            }

            builder
                .Append(" | ")
                .Append(NormalizeContextValue(field.Key))
                .Append('=')
                .Append(NormalizeContextValue(field.Value ?? "<null>"));
        }
    }

    private static string NormalizeContextValue(string value)
    {
        return value
            .Trim()
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
