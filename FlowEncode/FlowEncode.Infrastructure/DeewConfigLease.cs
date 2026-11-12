using System.Diagnostics;
using System.Text.Json;

namespace FlowEncode.Infrastructure;

internal sealed class DeewConfigLease : IDisposable
{
    private readonly FileStream _lock;
    private readonly string _config;
    private readonly string _backup;
    public DeewConfigEditor Snapshot { get; }
    public string? RestorationError { get; private set; }
    private bool _disposed;

    private DeewConfigLease(FileStream fileLock, string config, DeewConfigEditor snapshot)
    { _lock = fileLock; _config = config; _backup = config + ".flowencode-backup.json"; Snapshot = snapshot; }

    public static async Task<DeewConfigLease> AcquireAsync(string executable, string? managedTemp,
        Func<CancellationToken, Task> initialize, CancellationToken token)
    {
        var config = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "config.toml");
        var lockPath = config + ".flowencode.lock";
        FileStream fileLock;
        var deadline = DateTime.UtcNow.AddMinutes(30);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { fileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(150, token); }
            catch (UnauthorizedAccessException ex) { throw new InvalidOperationException("deew must be in a writable portable directory. Move or reimport the complete directory.", ex); }
        }
        DeewConfigLease? lease = null;
        try
        {
            Recover(config, config + ".flowencode-backup.json");
            if (!File.Exists(config))
            {
                token.ThrowIfCancellationRequested();
                await initialize(token);
                if (!File.Exists(config)) throw new InvalidDataException("deew -gc did not generate config.toml.");
            }
            var original = await File.ReadAllBytesAsync(config, token);
            var snapshot = new DeewConfigEditor(original);
            lease = new(fileLock, config, snapshot);
            var temporary = snapshot.Edit(managedTemp);
            if ((File.GetAttributes(config) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("deew config.toml is read-only. Move deew to a writable portable directory.");
            // Verify replacement permissions before recording a pending change.
            var probe = config + "." + Guid.NewGuid().ToString("N") + ".probe";
            try { File.WriteAllBytes(probe, original); File.Move(probe, probe + ".replace"); }
            finally { File.Delete(probe); File.Delete(probe + ".replace"); }
            WriteAtomic(lease._backup, JsonSerializer.SerializeToUtf8Bytes(new Backup(Convert.ToBase64String(original), Convert.ToBase64String(temporary))));
            WriteAtomic(config, temporary);
            return lease;
        }
        catch
        {
            try { if (lease is not null) Recover(config, lease._backup); }
            finally { fileLock.Dispose(); }
            throw;
        }
    }

    internal static void Recover(string config, string backupPath)
    {
        if (!File.Exists(backupPath)) return;
        var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllBytes(backupPath)) ?? throw new InvalidDataException("Invalid deew recovery backup: " + backupPath);
        var current = File.Exists(config) ? File.ReadAllBytes(config) : [];
        var original = Convert.FromBase64String(backup.Original);
        if (current.AsSpan().SequenceEqual(Convert.FromBase64String(backup.Temporary))) WriteAtomic(config, original);
        else if (!current.AsSpan().SequenceEqual(original))
            throw new InvalidOperationException($"deew config was edited externally. Current config and recovery backup preserved: {backupPath}. Resolve the conflict before retrying.");
        File.Delete(backupPath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Recover(_config, _backup); }
        catch (Exception ex) { RestorationError = ex.Message; Debug.WriteLine(ex); }
        finally { _lock.Dispose(); }
        // Keep the lock file: unlinking it lets a third waiter bypass a second lease on Unix.
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }
    private sealed record Backup(string Original, string Temporary);
}
