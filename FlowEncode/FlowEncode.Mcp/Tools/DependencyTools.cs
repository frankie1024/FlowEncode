using System.ComponentModel;
using FlowEncode.Domain;
using FlowEncode.Infrastructure.Automation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FlowEncode.Mcp.Tools;

[McpServerToolType]
public static class DependencyTools
{
    [McpServerTool(Name = "list_dependencies", ReadOnly = true)]
    [Description("Lists the toolchain dependencies the desktop app manages (Python, VapourSynth, vsrepo plugins, FFmpeg, "
        + "x264, x265, SVT-AV1, Av1an, AviSynth pipe, DGDemux, eac3to, deew, DEE, opus tools) with installed / latest "
        + "version, update availability, manual pin and which actions apply. Starts the desktop app if needed.")]
    public static Task<IReadOnlyList<AutomationDependencyStatus>> ListDependencies(
        DesktopBridge desktop,
        [Description("Also query GitHub / vsrepo for the latest versions (slower). Defaults to false.")] bool checkForUpdates = false,
        CancellationToken cancellationToken = default)
    {
        return desktop.SendAsync<IReadOnlyList<AutomationDependencyStatus>>(
            DesktopAutomationMethods.DependenciesList,
            new CheckUpdatesParams(checkForUpdates),
            cancellationToken,
            responseTimeout: TimeSpan.FromMinutes(5));
    }

    [McpServerTool(Name = "manage_dependency", Destructive = true)]
    [Description("Installs, updates, uninstalls, imports or pins a dependency through the desktop app's setup module. "
        + "install also updates an installed dependency. import copies a local binary/archive into FlowEncode's tools "
        + "(DGDemux, eac3to, deew, DEE, opus tools, avs2pipemod). pin makes FlowEncode use an existing .exe in place; "
        + "clear_pin returns to the managed copy. Returns an operationId; poll get_operation.")]
    public static Task<AutomationOperationSnapshot> ManageDependency(
        DesktopBridge desktop,
        [Description("Dependency kind, e.g. Python312, VapourSynth, Vsrepo, VsPluginBundle, Awsmfunc, Vsjetpack, FfmpegBundle, "
            + "X264, X265, SvtAv1, Av1an, Avs2PipeMod, DgDemux, Eac3To, Deew, Dee, OpusExt.")] string dependency,
        [Description("install, uninstall, import, pin or clear_pin.")] string operation,
        [Description("Absolute path of the file to import or the .exe to pin (import / pin only).")] string? sourcePath = null,
        CancellationToken cancellationToken = default)
    {
        var kind = McpEnum.Parse<SetupDependencyKind>(dependency, nameof(dependency));
        var normalizedOperation = operation?.Trim().ToLowerInvariant().Replace('-', '_') ?? string.Empty;
        if (!DesktopDependencyOperations.All.Contains(normalizedOperation))
        {
            throw new McpException($"Unknown operation '{operation}'. Use {string.Join(", ", DesktopDependencyOperations.All)}.");
        }

        string? fullSourcePath = null;
        if (normalizedOperation is DesktopDependencyOperations.Import or DesktopDependencyOperations.Pin)
        {
            fullSourcePath = McpPaths.RequireExistingFile(sourcePath, nameof(sourcePath));
        }

        return desktop.SendAsync<AutomationOperationSnapshot>(
            DesktopAutomationMethods.DependencyOperation,
            new DependencyOperationParams(kind, normalizedOperation, fullSourcePath),
            cancellationToken);
    }

    [McpServerTool(Name = "refresh_environment", Destructive = false)]
    [Description("Makes the desktop app re-detect every tool (after installing something outside FlowEncode). "
        + "Returns an operationId; poll get_operation.")]
    public static Task<AutomationOperationSnapshot> RefreshEnvironment(
        DesktopBridge desktop,
        CancellationToken cancellationToken = default)
    {
        return desktop.SendAsync<AutomationOperationSnapshot>(DesktopAutomationMethods.EnvironmentRefresh, null, cancellationToken);
    }
}
