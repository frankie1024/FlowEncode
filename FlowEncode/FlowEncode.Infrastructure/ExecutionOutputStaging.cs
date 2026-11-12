using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class ExecutionOutputStaging
{
    internal static void EnsureFreshDirectory(string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException($"The task staging directory is not empty / 任务临时目录不是空目录：{directory}");
        Directory.CreateDirectory(directory);
    }

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

    public static void FinalizeFile(string stagedPath, string finalPath, Guid jobId, bool overwrite = true)
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

            // Move without replacement enforces the policy atomically, even if the destination
            // appeared while the job was queued or encoding.
            if (!overwrite)
            {
                File.Move(stagedPath, finalPath, overwrite: false);
                return;
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

    /// <summary>
    /// Finalizes a staged output. If that fails, keeps the encoded result (see <see cref="TryPreserveStagedFile"/>)
    /// and returns a description of the failure instead of throwing, so every runner reports it the same way.
    /// </summary>
    public static StagedOutputFinalizeFailure? FinalizeFileOrPreserve(
        string stagedPath,
        string finalPath,
        Guid jobId,
        bool overwrite,
        Action<string>? writeDiagnostic)
    {
        try
        {
            FinalizeFile(stagedPath, finalPath, jobId, overwrite);
            return null;
        }
        catch (IOException ex)
        {
            writeDiagnostic?.Invoke($"Failed to finalize output '{stagedPath}' -> '{finalPath}'. {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
            var preservedPath = TryPreserveStagedFile(stagedPath, finalPath, jobId, writeDiagnostic);
            return new StagedOutputFinalizeFailure(finalPath, ex.InnerException?.Message ?? ex.Message, preservedPath, ex);
        }
    }

    /// <summary>
    /// Moves a staged output that could not be finalized next to the requested output path so the
    /// completed work survives the temp directory cleanup. If moving fails, returns the original
    /// staged path; callers must retain that file and its directory. Returns null for missing output.
    /// </summary>
    public static string? TryPreserveStagedFile(
        string? stagedPath,
        string finalPath,
        Guid jobId,
        Action<string>? onFailure = null)
    {
        if (string.IsNullOrWhiteSpace(stagedPath) || !File.Exists(stagedPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(finalPath);
        var fileName = Path.GetFileNameWithoutExtension(finalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "output";
        }

        var preservedFileName = $"{fileName}.{jobId:N}.recovered{Path.GetExtension(finalPath)}";
        var preservedPath = string.IsNullOrWhiteSpace(directory)
            ? preservedFileName
            : Path.Combine(directory, preservedFileName);
        try
        {
            File.Move(stagedPath, preservedPath, overwrite: false);
            return preservedPath;
        }
        catch (Exception ex)
        {
            onFailure?.Invoke($"Failed to move staged output '{stagedPath}' to '{preservedPath}'. Keeping the original staged file. {ex.GetType().Name}: {ex.Message}");
            return stagedPath;
        }
    }

    public static void FinalizeDirectory(string stagedDirectory, string finalDirectory, bool overwrite = true)
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

            if (!overwrite)
                foreach (var file in Directory.EnumerateFiles(stagedDirectory, "*", SearchOption.AllDirectories))
                    if (File.Exists(Path.Combine(finalDirectory, Path.GetRelativePath(stagedDirectory, file))))
                        throw new IOException("An output file already exists: " + Path.Combine(finalDirectory, Path.GetRelativePath(stagedDirectory, file)));

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

                if (overwrite && File.Exists(destinationPath))
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

internal sealed record StagedOutputFinalizeFailure(
    string FinalPath,
    string Reason,
    string? PreservedPath,
    IOException Exception)
{
    public string DescribePreservedOutput(AppLanguage language)
    {
        if (PreservedPath is null)
        {
            return string.Empty;
        }

        return language == AppLanguage.Chinese
            ? $" 编码结果已保留在：{PreservedPath}"
            : $" The encoded output was kept at: {PreservedPath}";
    }

    public string Describe(AppLanguage language)
    {
        var summary = language == AppLanguage.Chinese
            ? $"编码已完成，但无法完成输出文件落盘：{FinalPath}。{Reason}"
            : $"Encoding completed but the output file could not be finalized: {FinalPath}. {Reason}";
        return summary + DescribePreservedOutput(language);
    }
}
