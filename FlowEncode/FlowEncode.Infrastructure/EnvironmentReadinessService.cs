using FlowEncode.Application;
using FlowEncode.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlowEncode.Infrastructure;

public sealed class EnvironmentReadinessService : IEnvironmentReadinessService
{
    private readonly IToolRegistryService _toolRegistryService;
    private readonly IToolProbeService _toolProbeService;
    private readonly ISetupGuideCacheService? _cacheService;
    private readonly IAppSettingsService? _settingsService;
    private readonly LocalAppPaths? _paths;

    public EnvironmentReadinessService(
        IToolRegistryService toolRegistryService,
        IToolProbeService toolProbeService,
        ISetupGuideCacheService? cacheService = null,
        IAppSettingsService? settingsService = null,
        LocalAppPaths? paths = null)
    {
        _toolRegistryService = toolRegistryService;
        _toolProbeService = toolProbeService;
        _cacheService = cacheService;
        _settingsService = settingsService;
        _paths = paths;
    }

    // Restore only on startup. Explicit refreshes and dependency mutations still use CheckAsync.
    public EnvironmentReadinessReport? TryGetCachedReport()
    {
        try
        {
            var snapshot = _cacheService?.Load();
            if (snapshot?.EnvironmentTools is not { Length: > 0 } tools
                || !snapshot.EnvironmentCheckedAt.HasValue
                || !tools.Select(static tool => tool.Kind).Order().SequenceEqual(
                    _toolRegistryService.GetTools().Select(static tool => tool.Kind).Order())
                || snapshot.EnvironmentSignature != BuildCacheSignature(tools))
            {
                return null;
            }

            return BuildReport(tools, snapshot.EnvironmentCheckedAt.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<EnvironmentReadinessReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var toolResults = await _toolProbeService.ProbeAllAsync(cancellationToken);
        var report = BuildReport(toolResults, DateTimeOffset.Now);
        if (_cacheService is not null)
        {
            try
            {
                var snapshot = _cacheService.Load() ?? new SetupGuideCacheSnapshot(
                    SetupGuideCacheSnapshot.CurrentSchemaVersion, report.CheckedAt, null, null, null);
                _cacheService.Save(snapshot with
                {
                    SavedAt = report.CheckedAt,
                    EnvironmentTools = toolResults.ToArray(),
                    EnvironmentSignature = BuildCacheSignature(toolResults),
                    EnvironmentCheckedAt = report.CheckedAt
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_paths is not null)
                    AppDiagnosticsLog.Write(_paths, nameof(EnvironmentReadinessService),
                        $"Failed to cache environment status: {ex.Message}", AppDiagnosticSeverity.Warning);
            }
        }

        return report;
    }

    private string BuildCacheSignature(IReadOnlyList<ToolProbeResult> tools)
    {
        var settings = _settingsService?.Load();
        var inputs = new StringBuilder("environment-cache-v1\n");
        inputs.AppendLine(_paths?.RootPath);
        inputs.AppendLine(settings?.PreferSystemEncoders.ToString());
        inputs.AppendLine(JsonSerializer.Serialize(settings?.EffectiveManualToolPaths.OrderBy(static pair => pair.Key)));
        inputs.AppendLine(JsonSerializer.Serialize(_toolRegistryService.GetTools()));
        inputs.AppendLine(string.Join('|', Enum.GetValues<SetupDependencyKind>().Select(ToolchainBaseline.VersionFor)));
        foreach (var variable in _toolRegistryService.GetTools().SelectMany(static tool => tool.EnvironmentVariableNames)
            .Append("PATH").Distinct().Order())
        {
            foreach (var target in Enum.GetValues<EnvironmentVariableTarget>())
                inputs.AppendLine(Environment.GetEnvironmentVariable(variable, target));
        }

        foreach (var path in tools.Select(static tool => tool.ExecutablePath).Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = new FileInfo(path);
            inputs.Append(path).Append('|').Append(file.Exists ? file.Length : -1)
                .Append('|').Append(file.Exists ? file.LastWriteTimeUtc.Ticks : -1).AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs.ToString())));
    }

    private EnvironmentReadinessReport BuildReport(IReadOnlyList<ToolProbeResult> toolResults, DateTimeOffset checkedAt)
    {
        var toolMap = toolResults.ToDictionary(static result => result.Kind, static result => result);

        var capabilityResults = _toolRegistryService
            .GetCapabilities()
            .Select(definition => BuildCapabilityReadiness(definition, toolMap))
            .ToList();

        return new EnvironmentReadinessReport(checkedAt, toolResults, capabilityResults);
    }

    private static CapabilityReadiness BuildCapabilityReadiness(
        CapabilityDefinition definition,
        IReadOnlyDictionary<RegisteredToolKind, ToolProbeResult> toolMap)
    {
        var requirements = definition.Requirements
            .Select(requirement => new CapabilityRequirementReadiness(
                requirement,
                requirement.CandidateTools.Select(toolKind => toolMap[toolKind]).ToList()))
            .ToList();

        return new CapabilityReadiness(
            definition.Kind,
            ReadinessStateResolver.ResolveFromRequirements(requirements),
            requirements);
    }
}
