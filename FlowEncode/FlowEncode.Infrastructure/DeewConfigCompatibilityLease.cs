using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlowEncode.Infrastructure;

internal sealed class DeewConfigCompatibilityLease : IDisposable
{
    // Frozen deew 3.2.2 ignores PYTHONIOENCODING and its block-character logo crashes CP936 output.
    private const string BackupSuffix = ".flowencode-backup.json";
    private const string LockSuffix = ".flowencode.lock";
    private static readonly Regex LogoSettingRegex = new(
        @"(?m)^(?<prefix>[ \t]*logo[ \t]*=[ \t]*)(?<value>\d+)(?<suffix>[ \t]*(?:#[^\r\n]*)?)(?=\r?$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FileStream? _lockStream;
    private readonly string? _lockPath;
    private readonly string? _userConfigPath;
    private readonly string? _backupPath;
    private int _disposed;

    private DeewConfigCompatibilityLease(
        FileStream? lockStream = null,
        string? lockPath = null,
        string? userConfigPath = null,
        string? backupPath = null)
    {
        _lockStream = lockStream;
        _lockPath = lockPath;
        _userConfigPath = userConfigPath;
        _backupPath = backupPath;
    }

    public static async Task<DeewConfigCompatibilityLease> AcquireAsync(
        string deewPath,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DeewConfigCompatibilityLease();
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return new DeewConfigCompatibilityLease();
        }

        var configDirectory = Path.Combine(localAppData, "deew");
        Directory.CreateDirectory(configDirectory);
        var userConfigPath = Path.Combine(configDirectory, "config.toml");
        var sidecarConfigPath = Path.Combine(
            Path.GetDirectoryName(deewPath) ?? Environment.CurrentDirectory,
            "config.toml");
        var backupPath = userConfigPath + BackupSuffix;
        var lockPath = userConfigPath + LockSuffix;
        var lockStream = await AcquireLockAsync(lockPath, cancellationToken);

        try
        {
            RecoverPendingChange(userConfigPath, backupPath);

            var userConfigExists = File.Exists(userConfigPath);
            var sourceConfigPath = userConfigExists ? userConfigPath : sidecarConfigPath;
            if (!File.Exists(sourceConfigPath))
            {
                lockStream.Dispose();
                TryDelete(lockPath);
                return new DeewConfigCompatibilityLease();
            }

            var sourceBytes = await File.ReadAllBytesAsync(sourceConfigPath, cancellationToken);
            var modifiedBytes = DisableLogo(sourceBytes);
            if (modifiedBytes is null)
            {
                lockStream.Dispose();
                TryDelete(lockPath);
                return new DeewConfigCompatibilityLease();
            }

            var originalBytes = userConfigExists
                ? sourceBytes
                : Array.Empty<byte>();
            var backup = new ConfigBackup(
                userConfigExists,
                Convert.ToBase64String(originalBytes),
                Convert.ToBase64String(modifiedBytes));
            await WriteAtomicallyAsync(
                backupPath,
                JsonSerializer.SerializeToUtf8Bytes(backup),
                cancellationToken);
            await WriteAtomicallyAsync(userConfigPath, modifiedBytes, cancellationToken);

            return new DeewConfigCompatibilityLease(lockStream, lockPath, userConfigPath, backupPath);
        }
        catch
        {
            try
            {
                RecoverPendingChange(userConfigPath, backupPath);
            }
            finally
            {
                lockStream.Dispose();
                TryDelete(lockPath);
            }

            throw;
        }
    }

    internal static byte[]? DisableLogoForTesting(byte[] content) => DisableLogo(content);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_userConfigPath is not null && _backupPath is not null)
            {
                RecoverPendingChange(_userConfigPath, _backupPath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to restore the deew config after audio processing. {ex}");
        }
        finally
        {
            _lockStream?.Dispose();
            if (_lockPath is not null)
            {
                TryDelete(_lockPath);
            }
        }
    }

    private static byte[]? DisableLogo(byte[] content)
    {
        var text = Encoding.Latin1.GetString(content);
        var match = LogoSettingRegex.Match(text);
        if (!match.Success || string.Equals(match.Groups["value"].Value, "0", StringComparison.Ordinal))
        {
            return null;
        }

        var modified = string.Concat(
            text.AsSpan(0, match.Groups["value"].Index),
            "0",
            text.AsSpan(match.Groups["value"].Index + match.Groups["value"].Length));
        return Encoding.Latin1.GetBytes(modified);
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private static void RecoverPendingChange(string userConfigPath, string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            return;
        }

        var backup = JsonSerializer.Deserialize<ConfigBackup>(File.ReadAllBytes(backupPath));
        if (backup is null)
        {
            throw new InvalidDataException($"Invalid deew config backup: {backupPath}");
        }

        var modifiedBytes = Convert.FromBase64String(backup.ModifiedBase64);
        var currentMatchesTemporaryConfig = File.Exists(userConfigPath)
            && File.ReadAllBytes(userConfigPath).AsSpan().SequenceEqual(modifiedBytes);
        if (currentMatchesTemporaryConfig)
        {
            if (backup.OriginalExisted)
            {
                WriteAtomically(userConfigPath, Convert.FromBase64String(backup.OriginalBase64));
            }
            else
            {
                File.Delete(userConfigPath);
            }
        }

        File.Delete(backupPath);
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = GetTemporaryPath(path);
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken);
            ReplaceFile(temporaryPath, path);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        var temporaryPath = GetTemporaryPath(path);
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            ReplaceFile(temporaryPath, path);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void ReplaceFile(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            try
            {
                File.Replace(temporaryPath, destinationPath, null, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (IOException)
            {
            }
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static string GetTemporaryPath(string path)
        => $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record ConfigBackup(
        bool OriginalExisted,
        string OriginalBase64,
        string ModifiedBase64);
}
