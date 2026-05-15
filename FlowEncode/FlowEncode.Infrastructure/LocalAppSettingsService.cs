using System.Text;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class LocalAppSettingsService : IAppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly LocalAppPaths _paths;
    private readonly object _gate = new();
    private AppSettings? _cache;
    private SettingsLoadRecoveryInfo? _lastLoadRecoveryInfo;

    public LocalAppSettingsService(LocalAppPaths paths)
    {
        _paths = paths;
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            if (!File.Exists(_paths.SettingsPath))
            {
                _cache = AppSettings.Default;
                _lastLoadRecoveryInfo = null;
                return _cache;
            }

            try
            {
                var json = File.ReadAllText(_paths.SettingsPath);
                _cache = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.Default;
                _lastLoadRecoveryInfo = null;
            }
            catch (Exception ex)
            {
                _lastLoadRecoveryInfo = RecoverBrokenSettingsFileUnsafe(ex);
                AppDiagnosticsLog.Write(
                    _paths,
                    nameof(LocalAppSettingsService),
                    $"Failed to load settings from '{_paths.SettingsPath}'. {ex.GetType().Name}: {ex.Message}");
                _cache = AppSettings.Default;
            }

            return _cache;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            PersistentFileWriter.WriteAllText(
                _paths.SettingsPath,
                JsonSerializer.Serialize(settings, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                message => AppDiagnosticsLog.Write(_paths, nameof(LocalAppSettingsService), message));

            _cache = settings;
        }
    }

    public SettingsLoadRecoveryInfo? ConsumeLastLoadRecoveryInfo()
    {
        lock (_gate)
        {
            var info = _lastLoadRecoveryInfo;
            _lastLoadRecoveryInfo = null;
            return info;
        }
    }

    private SettingsLoadRecoveryInfo RecoverBrokenSettingsFileUnsafe(Exception exception)
    {
        var brokenPath = BuildBrokenSettingsBackupPath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.SettingsPath) ?? _paths.SettingsRootPath);
            File.Move(_paths.SettingsPath, brokenPath, overwrite: false);
        }
        catch (Exception moveException)
        {
            AppDiagnosticsLog.Write(
                _paths,
                nameof(LocalAppSettingsService),
                $"Failed to back up broken settings file '{_paths.SettingsPath}' to '{brokenPath}'. {moveException.GetType().Name}: {moveException.Message}");
            return new SettingsLoadRecoveryInfo(
                _paths.SettingsPath,
                null,
                exception.Message,
                moveException.Message);
        }

        return new SettingsLoadRecoveryInfo(
            _paths.SettingsPath,
            brokenPath,
            exception.Message,
            null);
    }

    private string BuildBrokenSettingsBackupPath()
    {
        var directory = Path.GetDirectoryName(_paths.SettingsPath) ?? _paths.SettingsRootPath;
        var fileName = Path.GetFileName(_paths.SettingsPath);

        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var suffix = attempt == 0
                ? DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : $"{DateTime.Now:yyyyMMdd_HHmmss}_{attempt + 1}";
            var candidate = Path.Combine(directory, $"{fileName}.broken-{suffix}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{fileName}.broken-{Guid.NewGuid():N}");
    }

}

public sealed record SettingsLoadRecoveryInfo(
    string SettingsPath,
    string? BackupPath,
    string LoadError,
    string? BackupError);
