using System.Diagnostics;

namespace FlowEncode.Infrastructure;

internal static class ManagedDirectoryInstaller
{
    public static void ReplaceDirectoryContents(
        string sourceDirectory,
        string targetDirectory,
        Action<string>? prepareStagedDirectory = null,
        bool skipReparsePoints = false)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Source directory was not found: {sourceDirectory}");
        }

        ReplaceDirectory(targetDirectory, stagingDirectory =>
        {
            CopyDirectoryContents(sourceDirectory, stagingDirectory, skipReparsePoints);
            prepareStagedDirectory?.Invoke(stagingDirectory);
        });
    }

    public static void ReplaceDirectory(string targetDirectory, Action<string> stageContents)
    {
        var stagingDirectory = CreateSiblingTemporaryDirectory(targetDirectory, "install");
        try
        {
            stageContents(stagingDirectory);
            ReplaceTargetWithStaging(stagingDirectory, targetDirectory);
        }
        finally
        {
            DeleteDirectoryQuietly(stagingDirectory);
        }
    }

    private static void CopyDirectoryContents(string sourceDirectory, string targetDirectory, bool skipReparsePoints)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = skipReparsePoints ? FileAttributes.ReparsePoint : 0
        };
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", options))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", options))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destination = Path.Combine(targetDirectory, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(file, destination, true);
        }
    }

    private static void ReplaceTargetWithStaging(string stagingDirectory, string targetDirectory)
    {
        var normalizedTarget = Path.GetFullPath(targetDirectory);
        var targetParent = Path.GetDirectoryName(normalizedTarget)
            ?? throw new InvalidOperationException("Target directory must have a parent directory.");
        Directory.CreateDirectory(targetParent);

        var backupDirectory = CreateSiblingTemporaryDirectory(targetDirectory, "backup", createDirectory: false);
        var hasBackup = false;

        try
        {
            if (Directory.Exists(normalizedTarget))
            {
                Directory.Move(normalizedTarget, backupDirectory);
                hasBackup = true;
            }

            Directory.Move(stagingDirectory, normalizedTarget);
            DeleteDirectoryQuietly(backupDirectory);
        }
        catch (Exception installError)
        {
            if (hasBackup)
            {
                try
                {
                    Directory.Move(backupDirectory, normalizedTarget);
                }
                catch (Exception ex)
                {
                    throw new IOException(
                        $"Installation failed at '{normalizedTarget}'. The previous version is retained at '{backupDirectory}'. {installError.Message} {ex.Message}",
                        new AggregateException(installError, ex));
                }
            }

            throw;
        }
    }

    private static string CreateSiblingTemporaryDirectory(
        string targetDirectory,
        string purpose,
        bool createDirectory = true)
    {
        var normalizedTarget = Path.GetFullPath(targetDirectory);
        var targetParent = Path.GetDirectoryName(normalizedTarget)
            ?? throw new InvalidOperationException("Target directory must have a parent directory.");
        Directory.CreateDirectory(targetParent);

        var targetName = Path.GetFileName(normalizedTarget.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var temporaryDirectory = Path.Combine(targetParent, $".{targetName}.{purpose}-{Guid.NewGuid():N}");
        if (createDirectory)
        {
            Directory.CreateDirectory(temporaryDirectory);
        }

        return temporaryDirectory;
    }

    private static void DeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to delete temporary directory '{directory}'. {ex}");
        }
    }
}
