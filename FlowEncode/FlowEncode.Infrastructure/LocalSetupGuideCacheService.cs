using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class LocalSetupGuideCacheService : ISetupGuideCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)
        }
    };

    private readonly LocalAppPaths _paths;
    private readonly object _gate = new();
    private SetupGuideCacheSnapshot? _cache;
    private bool _isLoaded;

    public LocalSetupGuideCacheService(LocalAppPaths paths)
    {
        _paths = paths;
    }

    public SetupGuideCacheSnapshot? Load()
    {
        lock (_gate)
        {
            if (_isLoaded)
            {
                return _cache;
            }

            _isLoaded = true;
            if (!File.Exists(_paths.SetupGuideCachePath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(_paths.SetupGuideCachePath);
                var snapshot = JsonSerializer.Deserialize<SetupGuideCacheSnapshot>(json, JsonOptions);
                if (snapshot is null || snapshot.SchemaVersion != SetupGuideCacheSnapshot.CurrentSchemaVersion)
                {
                    DeleteCacheFileUnsafe();
                    return null;
                }

                _cache = snapshot;
            }
            catch (Exception ex)
            {
                _cache = null;
                AppDiagnosticsLog.Write(
                    _paths,
                    nameof(LocalSetupGuideCacheService),
                    $"Failed to load setup guide cache '{_paths.SetupGuideCachePath}'. {ex.GetType().Name}: {ex.Message}",
                    AppDiagnosticSeverity.Warning,
                    exception: ex);
                DeleteCacheFileUnsafe();
            }

            return _cache;
        }
    }

    public void Save(SetupGuideCacheSnapshot snapshot)
    {
        lock (_gate)
        {
            PersistentFileWriter.WriteAllText(
                _paths.SetupGuideCachePath,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                message => AppDiagnosticsLog.Write(
                    _paths,
                    nameof(LocalSetupGuideCacheService),
                    message,
                    AppDiagnosticSeverity.Warning));

            _cache = snapshot;
            _isLoaded = true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cache = null;
            _isLoaded = true;

            try
            {
                DeleteCacheFileUnsafe();
            }
            catch (Exception ex)
            {
                AppDiagnosticsLog.Write(
                    _paths,
                    nameof(LocalSetupGuideCacheService),
                    $"Failed to clear setup guide cache '{_paths.SetupGuideCachePath}'. {ex.GetType().Name}: {ex.Message}",
                    AppDiagnosticSeverity.Warning,
                    exception: ex);
            }
        }
    }

    private void DeleteCacheFileUnsafe()
    {
        if (File.Exists(_paths.SetupGuideCachePath))
        {
            File.Delete(_paths.SetupGuideCachePath);
        }
    }
}
