using System.Text;

namespace FlowEncode.Infrastructure;

internal static class PersistentFileWriter
{
    public static void WriteAllText(
        string targetPath,
        string content,
        Encoding encoding,
        Action<string>? logCleanupFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);

        var tempPath = PrepareTemporaryPath(targetPath);
        var shouldDeleteTemp = true;

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            ReplaceTemporaryFile(tempPath, targetPath);
            shouldDeleteTemp = false;
        }
        finally
        {
            if (shouldDeleteTemp)
            {
                TryDeleteTemporaryFile(tempPath, logCleanupFailure);
            }
        }
    }

    public static async Task WriteAllTextAsync(
        string targetPath,
        string content,
        Encoding encoding,
        CancellationToken cancellationToken,
        Action<string>? logCleanupFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);

        var tempPath = PrepareTemporaryPath(targetPath);
        var shouldDeleteTemp = true;

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, encoding))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReplaceTemporaryFile(tempPath, targetPath);
            shouldDeleteTemp = false;
        }
        finally
        {
            if (shouldDeleteTemp)
            {
                TryDeleteTemporaryFile(tempPath, logCleanupFailure);
            }
        }
    }

    private static string PrepareTemporaryPath(string targetPath)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var directory = string.IsNullOrWhiteSpace(targetDirectory)
            ? Directory.GetCurrentDirectory()
            : targetDirectory;
        return Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
    }

    private static void ReplaceTemporaryFile(string tempPath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            try
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (FileNotFoundException) when (File.Exists(tempPath) && !File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath);
                return;
            }
        }

        File.Move(tempPath, targetPath);
    }

    private static void TryDeleteTemporaryFile(string path, Action<string>? logCleanupFailure)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logCleanupFailure?.Invoke($"Failed to delete temporary persistence file '{path}'. {ex.GetType().Name}: {ex.Message}");
        }
    }
}
