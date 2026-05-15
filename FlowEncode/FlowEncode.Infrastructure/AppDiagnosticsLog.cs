using FlowEncode.Application;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FlowEncode.Infrastructure;

internal static class AppDiagnosticsLog
{
    private static readonly object SyncRoot = new();
    private static readonly Encoding LogEncoding = new UTF8Encoding(false);

    private const string FileName = "diagnostics.log";
    private const long MaxLogFileBytes = 10L * 1024 * 1024;
    private const int RetainedArchiveCount = 3;

    public static void Write(
        LocalAppPaths appPaths,
        string source,
        string message,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Information,
        IReadOnlyDictionary<string, string?>? context = null,
        Exception? exception = null,
        long maxLogFileBytes = MaxLogFileBytes,
        int retainedArchiveCount = RetainedArchiveCount)
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
                TryRotateIfNeeded(path, LogEncoding.GetByteCount(line), maxLogFileBytes, retainedArchiveCount);
                File.AppendAllText(path, line, LogEncoding);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write app diagnostic log. {ex}");
        }
    }

    private static void TryRotateIfNeeded(
        string path,
        long incomingByteCount,
        long maxLogFileBytes,
        int retainedArchiveCount)
    {
        try
        {
            RotateIfNeeded(path, incomingByteCount, maxLogFileBytes, retainedArchiveCount);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to rotate app diagnostic log '{path}'. {ex}");
        }
    }

    private static void RotateIfNeeded(
        string path,
        long incomingByteCount,
        long maxLogFileBytes,
        int retainedArchiveCount)
    {
        if (maxLogFileBytes <= 0
            || retainedArchiveCount <= 0
            || !File.Exists(path))
        {
            return;
        }

        var currentLength = new FileInfo(path).Length;
        if (currentLength + incomingByteCount <= maxLogFileBytes)
        {
            return;
        }

        var lastArchivePath = BuildArchivePath(path, retainedArchiveCount);
        if (File.Exists(lastArchivePath))
        {
            File.Delete(lastArchivePath);
        }

        for (var index = retainedArchiveCount - 1; index >= 1; index--)
        {
            var archivePath = BuildArchivePath(path, index);
            if (!File.Exists(archivePath))
            {
                continue;
            }

            File.Move(archivePath, BuildArchivePath(path, index + 1), overwrite: true);
        }

        File.Move(path, BuildArchivePath(path, 1), overwrite: true);
    }

    private static string BuildArchivePath(string path, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{path}.{index}");

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
