using System.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure.Automation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FlowEncode.Mcp.Tools;

[McpServerToolType]
public static class AppTools
{
    [McpServerTool(Name = "get_app_status", ReadOnly = true)]
    [Description("Reports whether the FlowEncode desktop app is running and, if so, its queue counts, the state of the "
        + "audio / Blu-ray / auto-compression modules, the concurrency limit and the queue-completion action.")]
    public static async Task<AppStatusResult> GetAppStatus(
        DesktopBridge desktop,
        CancellationToken cancellationToken = default)
    {
        var status = await desktop.TryGetStatusAsync(cancellationToken);
        return new AppStatusResult(status is not null, status, DesktopAppLocator.Find());
    }

    [McpServerTool(Name = "launch_desktop_app", Destructive = false, Idempotent = true)]
    [Description("Starts the FlowEncode desktop app if it is not running and waits until it accepts automation requests. "
        + "Task tools do this automatically; use it to open the app for the user.")]
    public static Task<AutomationAppStatus> LaunchDesktopApp(
        DesktopBridge desktop,
        CancellationToken cancellationToken = default)
    {
        return desktop.EnsureRunningAsync(cancellationToken);
    }

    [McpServerTool(Name = "get_task_status", ReadOnly = true)]
    [Description("Returns the current or last task of a desktop module: audio, bluray or autocompression "
        + "(state, progress, status text, command line and recent log lines). Each module runs one task at a time.")]
    public static Task<AutomationModuleStatus> GetTaskStatus(
        DesktopBridge desktop,
        [Description("audio, bluray or autocompression.")] string module,
        [Description("How many recent log lines to include (0-200). Defaults to 20.")] int logLines = 20,
        CancellationToken cancellationToken = default)
    {
        return desktop.SendIfRunningAsync<AutomationModuleStatus>(
            DesktopAutomationMethods.ModuleStatus,
            new ModuleParams(ParseModule(module), Math.Clamp(logLines, 0, 200)),
            McpServerText.DesktopNotRunning,
            cancellationToken);
    }

    [McpServerTool(Name = "cancel_task", Destructive = true, Idempotent = true)]
    [Description("Cancels the running task of a desktop module (audio, bluray or autocompression) and stops its processes.")]
    public static Task<AutomationModuleStatus> CancelTask(
        DesktopBridge desktop,
        [Description("audio, bluray or autocompression.")] string module,
        CancellationToken cancellationToken = default)
    {
        return desktop.SendIfRunningAsync<AutomationModuleStatus>(
            DesktopAutomationMethods.ModuleCancel,
            new ModuleParams(ParseModule(module)),
            McpServerText.DesktopNotRunning,
            cancellationToken);
    }

    [McpServerTool(Name = "get_settings", ReadOnly = true)]
    [Description("Returns FlowEncode's settings: encoder preference, update check, theme, language, workspace folder, "
        + "concurrent encoding jobs, queue-completion action and manually pinned tool paths.")]
    public static async Task<AutomationSettings> GetSettings(
        DesktopBridge desktop,
        IAppSettingsService settingsService,
        CancellationToken cancellationToken = default)
    {
        if (await desktop.TryGetStatusAsync(cancellationToken) is not null)
        {
            return await desktop.SendAsync<AutomationSettings>(DesktopAutomationMethods.SettingsGet, null, cancellationToken);
        }

        return AutomationSettings.From(settingsService.Load());
    }

    [McpServerTool(Name = "update_settings", Destructive = false)]
    [Description("Changes FlowEncode settings through the desktop app (started if needed), which applies them immediately "
        + "(e.g. a higher concurrency limit starts queued jobs) and saves them. Omitted fields stay unchanged. "
        + "Tool paths are managed with manage_dependency (pin / clear_pin).")]
    public static Task<AutomationSettings> UpdateSettings(
        DesktopBridge desktop,
        [Description("Prefer encoders found on PATH over FlowEncode-managed ones.")] bool? preferSystemEncoders = null,
        [Description("Check for app updates at startup.")] bool? autoCheckUpdatesOnStartup = null,
        [Description("Theme: Default (follow system), Light or Dark.")] string? theme = null,
        [Description("Language: Chinese or English.")] string? language = null,
        [Description("Absolute path of the workspace folder (tools, templates, downloads). Must not be inside Program Files.")] string? workspaceRootPath = null,
        [Description("How many video encodes may run at once (1-5).")] int? maxConcurrentEncodingJobs = null,
        [Description("What to do when the queue drains: None, Sleep or Shutdown (runs once the queue drains and the PC has been idle).")] string? queueCompletionAction = null,
        CancellationToken cancellationToken = default)
    {
        var patch = new AutomationSettingsPatch(
            preferSystemEncoders,
            autoCheckUpdatesOnStartup,
            McpEnum.ParseOptional<AppThemePreference>(theme, nameof(theme)),
            McpEnum.ParseOptional<AppLanguage>(language, nameof(language)),
            string.IsNullOrWhiteSpace(workspaceRootPath) ? null : McpPaths.RequireAbsolute(workspaceRootPath, nameof(workspaceRootPath)),
            maxConcurrentEncodingJobs,
            McpEnum.ParseOptional<QueueCompletionAction>(queueCompletionAction, nameof(queueCompletionAction)));
        if (patch.IsEmpty)
        {
            throw new McpException("Pass at least one setting to change.");
        }

        return desktop.SendAsync<AutomationSettings>(
            DesktopAutomationMethods.SettingsUpdate,
            patch,
            cancellationToken,
            responseTimeout: TimeSpan.FromMinutes(5));
    }

    [McpServerTool(Name = "check_app_update", ReadOnly = true)]
    [Description("Checks GitHub Releases for a newer FlowEncode version.")]
    public static async Task<AppUpdateCheckResultView> CheckAppUpdate(
        DesktopBridge desktop,
        IAppUpdateService appUpdateService,
        CancellationToken cancellationToken = default)
    {
        if (await desktop.TryGetStatusAsync(cancellationToken) is not null)
        {
            return await desktop.SendAsync<AppUpdateCheckResultView>(
                DesktopAutomationMethods.AppUpdateCheck,
                null,
                cancellationToken,
                responseTimeout: TimeSpan.FromMinutes(2));
        }

        var result = await McpToolGuard.RunAsync(() => appUpdateService.CheckForUpdatesAsync(cancellationToken));
        return new AppUpdateCheckResultView(
            result.CurrentVersion,
            string.IsNullOrWhiteSpace(result.LatestVersion) ? null : result.LatestVersion,
            result.UpdateAvailable,
            string.IsNullOrWhiteSpace(result.ReleaseUrl) ? null : result.ReleaseUrl,
            null);
    }

    [McpServerTool(Name = "install_app_update", Destructive = true)]
    [Description("Downloads the latest FlowEncode installer, verifies it and, once no task is running, launches it and "
        + "closes the desktop app. The installer also replaces this MCP server, so the client will need to reconnect. "
        + "Returns an operationId; poll get_operation.")]
    public static Task<AutomationOperationSnapshot> InstallAppUpdate(
        DesktopBridge desktop,
        CancellationToken cancellationToken = default)
    {
        return desktop.SendAsync<AutomationOperationSnapshot>(DesktopAutomationMethods.AppUpdateInstall, null, cancellationToken);
    }

    [McpServerTool(Name = "get_operation", ReadOnly = true)]
    [Description("Returns the state (Running/Succeeded/Failed), result and error of a long desktop operation such as a "
        + "dependency install or app update.")]
    public static Task<AutomationOperationSnapshot> GetOperation(
        DesktopBridge desktop,
        [Description("The operationId returned by the tool that started it.")] string operationId,
        CancellationToken cancellationToken = default)
    {
        return desktop.SendIfRunningAsync<AutomationOperationSnapshot>(
            DesktopAutomationMethods.OperationGet,
            new OperationIdParams(McpJobIds.Parse(operationId, nameof(operationId))),
            McpServerText.DesktopNotRunning,
            cancellationToken);
    }

    [McpServerTool(Name = "list_operations", ReadOnly = true)]
    [Description("Lists the long desktop operations started through automation since the desktop app started.")]
    public static Task<IReadOnlyList<AutomationOperationSnapshot>> ListOperations(
        DesktopBridge desktop,
        CancellationToken cancellationToken = default)
    {
        return desktop.SendIfRunningAsync<IReadOnlyList<AutomationOperationSnapshot>>(
            DesktopAutomationMethods.OperationList,
            null,
            McpServerText.DesktopNotRunning,
            cancellationToken);
    }

    internal static string ParseModule(string? module)
    {
        var normalized = new string((module ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized switch
        {
            "audio" => DesktopAutomationModules.Audio,
            "bluray" or "bd" or "demux" => DesktopAutomationModules.BluRay,
            "autocompression" or "autocompress" or "targetquality" => DesktopAutomationModules.AutoCompression,
            _ => throw new McpException($"Unknown module '{module}'. Use audio, bluray or autocompression.")
        };
    }
}

public sealed record AppStatusResult(
    bool DesktopRunning,
    AutomationAppStatus? Desktop,
    string? DesktopExecutablePath);
