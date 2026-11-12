using FlowEncode.Application;
using FlowEncode.Infrastructure;
using Microsoft.Extensions.Logging;

namespace FlowEncode.Mcp;

/// <summary>
/// The desktop app can move the workspace folder (tools, templates, downloads) while this server
/// runs. Before each tool call, follow the saved setting so probes and template operations use the
/// same folder as the app.
/// </summary>
public sealed class WorkspaceRootSync
{
    private readonly LocalAppPaths _paths;
    private readonly IAppSettingsService _settingsService;
    private readonly IToolProbeService _toolProbeService;
    private readonly IEncoderDiscoveryService _encoderDiscoveryService;
    private readonly ILogger<WorkspaceRootSync> _logger;
    private readonly object _gate = new();

    public WorkspaceRootSync(
        LocalAppPaths paths,
        IAppSettingsService settingsService,
        IToolProbeService toolProbeService,
        IEncoderDiscoveryService encoderDiscoveryService,
        ILogger<WorkspaceRootSync> logger)
    {
        _paths = paths;
        _settingsService = settingsService;
        _toolProbeService = toolProbeService;
        _encoderDiscoveryService = encoderDiscoveryService;
        _logger = logger;
    }

    public void Synchronize()
    {
        var configured = _settingsService.Load().WorkspaceRootPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        lock (_gate)
        {
            string target;
            try
            {
                target = Path.GetFullPath(configured);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (string.Equals(
                    target.TrimEnd(Path.DirectorySeparatorChar),
                    _paths.RootPath.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                _paths.ActivateWorkspaceRootPath(target);
                _toolProbeService.InvalidateCache();
                _encoderDiscoveryService.InvalidateCache();
                _logger.LogInformation("Workspace folder changed to {Path}.", target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "Could not switch to the workspace folder {Path}.", target);
            }
        }
    }
}
