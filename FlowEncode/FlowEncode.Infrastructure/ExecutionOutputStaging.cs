namespace FlowEncode.Infrastructure;

internal static class ExecutionOutputStaging
{
    public static string CreateStagedFilePath(
        string stagingDirectory,
        string finalPath,
        Guid jobId,
        string suffix = "staging")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);

        Directory.CreateDirectory(stagingDirectory);

        var fileName = Path.GetFileNameWithoutExtension(finalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "output";
        }

        var extension = Path.GetExtension(finalPath);
        var stagedFileName = $"{fileName}.{jobId:N}.{suffix}.tmp{extension}";
        return Path.Combine(stagingDirectory, stagedFileName);
    }

    public static void FinalizeFile(string stagedPath, string finalPath, Guid jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);

        if (!File.Exists(stagedPath))
        {
            throw new FileNotFoundException("Temporary output was not produced.", stagedPath);
        }

        try
        {
            var outputDirectory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            if (File.Exists(finalPath))
            {
                var backupPath = CreateSiblingTemporaryFilePath(finalPath, jobId, "backup");
                BestEffortCleanup.DeleteFile(backupPath, $"staged output backup '{backupPath}'");
                File.Replace(stagedPath, finalPath, backupPath, ignoreMetadataErrors: true);
                BestEffortCleanup.DeleteFile(backupPath, $"staged output backup '{backupPath}'");
                return;
            }

            File.Move(stagedPath, finalPath);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to finalize output file: {finalPath}", ex);
        }
    }

    public static void FinalizeDirectory(string stagedDirectory, string finalDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalDirectory);

        if (!Directory.Exists(stagedDirectory))
        {
            throw new DirectoryNotFoundException($"Temporary output directory was not produced: {stagedDirectory}");
        }

        try
        {
            Directory.CreateDirectory(finalDirectory);

            foreach (var sourceDirectory in Directory.EnumerateDirectories(stagedDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(stagedDirectory, sourceDirectory);
                Directory.CreateDirectory(Path.Combine(finalDirectory, relativePath));
            }

            foreach (var sourceFile in Directory.EnumerateFiles(stagedDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(stagedDirectory, sourceFile);
                var destinationPath = Path.Combine(finalDirectory, relativePath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                if (File.Exists(destinationPath))
                {
                    var backupPath = CreateUniqueBackupPath(destinationPath);
                    BestEffortCleanup.DeleteFile(backupPath, $"staged output backup '{backupPath}'");
                    File.Replace(sourceFile, destinationPath, backupPath, ignoreMetadataErrors: true);
                    BestEffortCleanup.DeleteFile(backupPath, $"staged output backup '{backupPath}'");
                }
                else
                {
                    File.Move(sourceFile, destinationPath);
                }
            }

            Directory.Delete(stagedDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to finalize output directory: {finalDirectory}", ex);
        }
    }

    public static void CleanupStagedFile(
        string? stagedPath,
        string? finalPath,
        Guid jobId,
        Action<string>? onFailure = null)
    {
        if (!string.IsNullOrWhiteSpace(stagedPath))
        {
            BestEffortCleanup.DeleteFile(stagedPath, $"temporary output '{stagedPath}'", onFailure);
        }

        if (!string.IsNullOrWhiteSpace(finalPath))
        {
            var backupPath = CreateSiblingTemporaryFilePath(finalPath, jobId, "backup");
            BestEffortCleanup.DeleteFile(backupPath, $"temporary backup '{backupPath}'", onFailure);
        }
    }

    public static void CleanupStagedDirectory(
        string? stagedDirectory,
        Action<string>? onFailure = null,
        int emptyParentLevels = 0)
    {
        if (string.IsNullOrWhiteSpace(stagedDirectory))
        {
            return;
        }

        BestEffortCleanup.DeleteDirectoryRecursively(
            stagedDirectory,
            $"temporary output directory '{stagedDirectory}'",
            onFailure);

        var current = Path.GetDirectoryName(stagedDirectory);
        for (var level = 0; level < emptyParentLevels && !string.IsNullOrWhiteSpace(current); level++)
        {
            BestEffortCleanup.DeleteDirectoryIfEmpty(current, onFailure);
            current = Path.GetDirectoryName(current);
        }
    }

    private static string CreateSiblingTemporaryFilePath(string finalPath, Guid jobId, string suffix)
    {
        var directory = Path.GetDirectoryName(finalPath);
        var fileName = Path.GetFileNameWithoutExtension(finalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "output";
        }

        var extension = Path.GetExtension(finalPath);
        var temporaryFileName = $"{fileName}.{jobId:N}.{suffix}.tmp{extension}";
        return string.IsNullOrWhiteSpace(directory)
            ? temporaryFileName
            : Path.Combine(directory, temporaryFileName);
    }

    private static string CreateUniqueBackupPath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        var fileName = Path.GetFileNameWithoutExtension(destinationPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "output";
        }

        var extension = Path.GetExtension(destinationPath);
        var backupFileName = $"{fileName}.{Guid.NewGuid():N}.backup.tmp{extension}";
        return string.IsNullOrWhiteSpace(directory)
            ? backupFileName
            : Path.Combine(directory, backupFileName);
    }
}
