using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class AutoCompressionOutputFinalizer
{
    private static readonly TimeSpan StagedOutputWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StagedOutputPollInterval = TimeSpan.FromMilliseconds(250);

    public static bool TryFinalizeOutput(
        Guid jobId,
        string stagedOutputPath,
        string finalOutputPath,
        AppLanguage language,
        Action<string>? writeDiagnostic,
        out string failureSummary,
        bool overwrite = true)
    {
        failureSummary = string.Empty;

        if (!WaitForStagedOutput(stagedOutputPath))
        {
            failureSummary = BuildFailureSummary(language, finalOutputPath);
            writeDiagnostic?.Invoke(
                $"Auto compression finished without producing final output '{finalOutputPath}'. Staged path: '{stagedOutputPath}'.");
            return false;
        }

        var failure = ExecutionOutputStaging.FinalizeFileOrPreserve(stagedOutputPath, finalOutputPath, jobId, overwrite, writeDiagnostic);
        if (failure is not null)
        {
            failureSummary = BuildFailureSummary(language, finalOutputPath, failure.Exception) + failure.DescribePreservedOutput(language);
            return false;
        }

        if (File.Exists(finalOutputPath))
        {
            return true;
        }

        failureSummary = BuildFailureSummary(language, finalOutputPath);
        writeDiagnostic?.Invoke(
            $"Auto compression finished without producing final output '{finalOutputPath}'. Staged path: '{stagedOutputPath}'.");
        return false;
    }

    private static bool WaitForStagedOutput(string stagedOutputPath)
    {
        var deadline = DateTimeOffset.UtcNow + StagedOutputWaitTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(stagedOutputPath))
            {
                return true;
            }

            Thread.Sleep(StagedOutputPollInterval);
        }

        return File.Exists(stagedOutputPath);
    }

    private static string BuildFailureSummary(
        AppLanguage language,
        string finalOutputPath,
        Exception? exception = null)
    {
        var missingOutput = exception is FileNotFoundException
            || exception?.InnerException is FileNotFoundException
            || exception is null;
        var baseMessage = missingOutput
            ? T(
                language,
                $"Auto encode finished but did not produce the output file: {finalOutputPath}",
                $"自动压制已结束，但未生成输出文件：{finalOutputPath}")
            : T(
                language,
                $"Auto encode finished but could not finalize the output file: {finalOutputPath}",
                $"自动压制已结束，但无法完成输出文件落盘：{finalOutputPath}");

        return string.IsNullOrWhiteSpace(exception?.Message)
            ? baseMessage
            : $"{baseMessage} ({exception.Message})";
    }

    private static string T(AppLanguage language, string en, string zh) =>
        language == AppLanguage.English ? en : zh;
}
