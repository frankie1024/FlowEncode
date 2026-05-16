using System.Globalization;
using System.Text;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal sealed class EncodingJobLogWriter
{
    private const int MaxVisibleLogLength = 200_000;
    private const int RetainedVisibleLogLength = 120_000;
    private const string VisibleLogTruncationMarker = "[Log truncated; only latest output is kept]";

    private readonly LocalAppPaths _appPaths;
    private readonly Action<string> _writeDiagnostic;

    public EncodingJobLogWriter(LocalAppPaths appPaths, Action<string> writeDiagnostic)
    {
        _appPaths = appPaths;
        _writeDiagnostic = writeDiagnostic;
    }

    internal async Task<string> WriteSidecarLogAsync(
        EncodingJobRequest request,
        string displayCommand,
        EncodingJobState state,
        int exitCode,
        string rawLogPath)
    {
        var primaryLogPath = GetAvailableLogPath(request);
        var primaryError = await TryWriteSidecarLogAsync(primaryLogPath, request, displayCommand, state, exitCode, rawLogPath);
        if (primaryError is null)
        {
            return primaryLogPath;
        }

        var fallbackLogPath = GetFallbackLogPath(request);
        var fallbackError = await TryWriteSidecarLogAsync(fallbackLogPath, request, displayCommand, state, exitCode, rawLogPath);
        if (fallbackError is null)
        {
            _writeDiagnostic(
                $"Encoding job {request.JobId}: primary sidecar log write failed for '{primaryLogPath}', "
                + $"fallback saved to '{fallbackLogPath}'. {primaryError.GetType().Name}: {primaryError.Message}");
            return fallbackLogPath;
        }

        _writeDiagnostic(
            $"Encoding job {request.JobId}: failed to write sidecar log. "
            + $"Primary='{primaryLogPath}' ({primaryError.GetType().Name}: {primaryError.Message}); "
            + $"Fallback='{fallbackLogPath}' ({fallbackError.GetType().Name}: {fallbackError.Message}); "
            + $"RawLog='{rawLogPath}'.");
        return string.Empty;
    }

    internal static StreamWriter CreateRawLogWriter(string path)
    {
        var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream, Encoding.UTF8);
    }

    internal static string LastMeaningfulLine(string log)
    {
        return log
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? string.Empty;
    }

    internal static void AppendStageHeader(EncodingExecutionStep step, StreamWriter rawLogWriter, StringBuilder visibleLogBuilder)
    {
        if (step.StageCount <= 1)
        {
            return;
        }

        AppendLogLine(rawLogWriter, $"--- PASS {step.StageIndex}/{step.StageCount} ---");
        AppendLogLine(rawLogWriter, step.DisplayCommand);
        AppendLogLine(visibleLogBuilder, $"--- PASS {step.StageIndex}/{step.StageCount} ---");
        AppendLogLine(visibleLogBuilder, step.DisplayCommand);
    }

    internal static void TrimVisibleLogIfNeeded(StringBuilder builder)
    {
        if (builder.Length <= MaxVisibleLogLength)
        {
            return;
        }

        var removeCount = Math.Max(0, builder.Length - RetainedVisibleLogLength);
        if (removeCount > 0)
        {
            builder.Remove(0, removeCount);
        }

        var firstLineBreak = IndexOfLineBreak(builder);
        if (firstLineBreak >= 0 && firstLineBreak + 1 < builder.Length)
        {
            builder.Remove(0, firstLineBreak + 1);
        }

        if (!StartsWith(builder, VisibleLogTruncationMarker))
        {
            builder.Insert(0, $"{VisibleLogTruncationMarker}{Environment.NewLine}");
        }
    }

    internal static string TrimVisibleLogForTesting(string text)
    {
        var builder = new StringBuilder(text);
        TrimVisibleLogIfNeeded(builder);
        return builder.ToString();
    }

    private static async Task<Exception?> TryWriteSidecarLogAsync(
        string logPath,
        EncodingJobRequest request,
        string displayCommand,
        EncodingJobState state,
        int exitCode,
        string rawLogPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteLineAsync($"JobId: {request.JobId}");
            await writer.WriteLineAsync($"Encoder: {request.Profile.Kind.ToDisplayName()}");
            await writer.WriteLineAsync($"State: {state}");
            await writer.WriteLineAsync($"ExitCode: {exitCode}");
            await writer.WriteLineAsync($"Source: {request.SourcePath}");
            await writer.WriteLineAsync($"Output: {request.OutputPath}");
            await writer.WriteLineAsync($"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("--- COMMAND ---");
            await writer.WriteLineAsync(displayCommand);
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("--- LOG ---");

            if (File.Exists(rawLogPath))
            {
                await writer.FlushAsync();
                using var reader = File.OpenText(rawLogPath);
                await reader.BaseStream.CopyToAsync(stream);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void AppendLogLine(StreamWriter writer, string line)
    {
        writer.WriteLine(line);
        writer.WriteLine();
    }

    private static void AppendLogLine(StringBuilder builder, string line)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.AppendLine(line);
        TrimVisibleLogIfNeeded(builder);
    }

    private static string GetAvailableLogPath(EncodingJobRequest request)
    {
        var outputPath = request.OutputPath;
        var directory = Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory;
        var baseName = Path.GetFileNameWithoutExtension(outputPath);
        var suffix = BuildLogFileSuffix(request.Profile);
        var extension = ".log";
        var candidate = Path.Combine(directory, $"{baseName}{suffix}{extension}");

        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (var index = 0; index < 10000; index++)
        {
            var timestampSuffix = index == 0
                ? $"_{DateTime.Now:yyyyMMdd_HHmmss}"
                : $"_{DateTime.Now:yyyyMMdd_HHmmss}_{index + 1}";
            candidate = Path.Combine(directory, $"{baseName}{suffix}{timestampSuffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{baseName}{suffix}_{Guid.NewGuid():N}{extension}");
    }

    private string GetFallbackLogPath(EncodingJobRequest request)
    {
        var baseName = Path.GetFileNameWithoutExtension(request.OutputPath);
        var prefix = string.IsNullOrWhiteSpace(baseName)
            ? request.JobId.ToString("N")
            : SanitizeFileName(baseName);
        var suffix = BuildLogFileSuffix(request.Profile);
        var candidate = Path.Combine(_appPaths.LogsRootPath, $"{prefix}{suffix}.log");

        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (var index = 0; index < 10000; index++)
        {
            var timestampSuffix = index == 0
                ? $"_{DateTime.Now:yyyyMMdd_HHmmss}"
                : $"_{DateTime.Now:yyyyMMdd_HHmmss}_{index + 1}";
            candidate = Path.Combine(_appPaths.LogsRootPath, $"{prefix}{suffix}{timestampSuffix}.log");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(_appPaths.LogsRootPath, $"{prefix}{suffix}_{Guid.NewGuid():N}.log");
    }

    private static string BuildLogFileSuffix(EncodingProfile profile)
    {
        var encoderToken = profile.Kind.ToShortName();

        var rateToken = profile.RateControl switch
        {
            RateControlMode.Crf => $"_crf{FormatFileTokenNumber(profile.Quality)}",
            RateControlMode.Cq => $"_cq{FormatFileTokenNumber(profile.Quality)}",
            RateControlMode.Qp => $"_qp{FormatFileTokenNumber(profile.Quality)}",
            RateControlMode.Abr => $"_abr{profile.Bitrate ?? 3500}",
            RateControlMode.Vbr => $"_vbr{profile.Bitrate ?? 3500}",
            RateControlMode.TwoPass => $"_2pass{profile.Bitrate ?? 3500}",
            _ => string.Empty
        };

        return $"_{encoderToken}{rateToken}";
    }

    private static string FormatFileTokenNumber(double value)
    {
        return value
            .ToString("0.0##", CultureInfo.InvariantCulture)
            .TrimEnd('0')
            .TrimEnd('.')
            .Replace('.', '_');
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? "encoding-job" : sanitized;
    }

    private static int IndexOfLineBreak(StringBuilder builder)
    {
        for (var index = 0; index < builder.Length; index++)
        {
            var character = builder[index];
            if (character is '\r' or '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static bool StartsWith(StringBuilder builder, string value)
    {
        if (builder.Length < value.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (builder[index] != value[index])
            {
                return false;
            }
        }

        return true;
    }
}
