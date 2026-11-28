using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class InterruptedVideoStaging
{
    internal static bool Cleanup(EncodingJobRequest request, Action<string> writeDiagnostic)
    {
        var succeeded = true;
        try
        {
            // The output location may be on a disconnected drive. Try again on a later launch.
            File.GetAttributes(Path.GetDirectoryName(request.OutputPath)!);
            CleanupTask(request, message =>
            {
                succeeded = false;
                writeDiagnostic(message);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            writeDiagnostic($"Could not clean temporary files for interrupted video task {request.JobId}: {ex.Message}");
            return false;
        }
        return succeeded;
    }

    private static void CleanupTask(EncodingJobRequest request, Action<string> writeDiagnostic)
    {
        if (!request.UseAv1anParallelVideoEncoding)
        {
            var localDirectory = LocalEncodingJobRunner.GetJobTempDirectory(request);
            if (IsDirectoryWithoutLink(Path.GetDirectoryName(localDirectory)!, writeDiagnostic)
                && IsDirectoryWithoutLink(localDirectory, writeDiagnostic))
                ExecutionOutputStaging.CleanupStagedDirectory(localDirectory, writeDiagnostic, emptyParentLevels: 1);
            return;
        }

        var directory = ParallelVideoEncodingAv1anRunner.GetTempDirectory(request);
        var parent = Path.GetDirectoryName(directory)!;
        if (!IsDirectoryWithoutLink(Path.GetDirectoryName(parent)!, writeDiagnostic)
            || !IsDirectoryWithoutLink(parent, writeDiagnostic))
            return;

        var logs = ParallelVideoEncodingAv1anRunner.GetTemporaryLogDirectory(request, directory);
        if (IsDirectoryWithoutLink(Path.GetDirectoryName(logs)!, writeDiagnostic) && IsDirectoryWithoutLink(logs, writeDiagnostic))
            ExecutionOutputStaging.CleanupStagedDirectory(logs, writeDiagnostic, emptyParentLevels: 3);

        if (IsDirectoryWithoutLink(directory, writeDiagnostic))
            ExecutionOutputStaging.CleanupStagedDirectory(directory, writeDiagnostic, emptyParentLevels: 2);
    }

    private static bool IsDirectoryWithoutLink(string path, Action<string> writeDiagnostic)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                writeDiagnostic($"Skipped interrupted video cleanup at reparse point '{path}'.");
                return false;
            }

            return (attributes & FileAttributes.Directory) != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
