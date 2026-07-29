namespace FlowEncode.Infrastructure;

public static class ManagedFileInstaller
{
    public static async Task ReplaceFileAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The source file was not found.", sourcePath);
        }

        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath))
            ?? throw new InvalidOperationException("The target file must have a parent directory.");
        Directory.CreateDirectory(targetDirectory);
        var targetName = Path.GetFileName(targetPath);
        var temporaryPath = Path.Combine(targetDirectory, $".{targetName}.{Guid.NewGuid():N}.tmp");
        var backupPath = Path.Combine(targetDirectory, $".{targetName}.{Guid.NewGuid():N}.backup");
        try
        {
            await using (var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var temporary = File.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(temporary, cancellationToken);
                await temporary.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(targetPath))
            {
                File.Replace(temporaryPath, targetPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, targetPath);
            }
        }
        finally
        {
            DeleteQuietly(temporaryPath);
            DeleteQuietly(backupPath);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Installation already succeeded or failed atomically; stale cleanup is best effort.
        }
    }
}
