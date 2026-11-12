using System.Diagnostics;
using FlowEncode.Infrastructure.Automation;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ModelContextProtocol;

namespace FlowEncode.Mcp;

/// <summary>
/// Talks to the FlowEncode desktop app, which owns the task queue, module runners, settings and
/// dependency management. Starting work through the desktop keeps AI-initiated tasks visible in
/// its UI and under one concurrency limit instead of a private per-MCP-process scheduler.
/// </summary>
public sealed class DesktopBridge
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LaunchPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProbeConnectTimeout = TimeSpan.FromMilliseconds(500);

    private readonly DesktopAutomationClient _client;
    private readonly ILogger<DesktopBridge> _logger;
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public DesktopBridge(DesktopAutomationClient client, ILogger<DesktopBridge> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Returns the desktop status, or null when the app is not running.</summary>
    public async Task<DesktopTaskStatus?> TryGetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _client.SendAsync<DesktopTaskStatus>(
                DesktopTaskMethods.Status,
                cancellationToken: cancellationToken,
                connectTimeout: ProbeConnectTimeout);
        }
        catch (DesktopUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends a request to a desktop app that is already running; never launches it.
    /// </summary>
    public async Task<T> SendIfRunningAsync<T>(string method, object? parameters, string notRunningMessage, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.SendAsync<T>(method, parameters, cancellationToken, ProbeConnectTimeout);
        }
        catch (DesktopUnavailableException)
        {
            throw new McpException(notRunningMessage);
        }
        catch (DesktopAutomationException ex)
        {
            throw new McpException($"{ex.Code}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Sends a request, starting the desktop app first when it is not running.
    /// </summary>
    public async Task<T> SendAsync<T>(string method, object? parameters, CancellationToken cancellationToken, TimeSpan? responseTimeout = null)
    {
        await EnsureRunningAsync(cancellationToken);
        try
        {
            return await _client.SendAsync<T>(method, parameters, cancellationToken, responseTimeout: responseTimeout);
        }
        catch (DesktopUnavailableException ex)
        {
            throw new McpException($"Lost the connection to the FlowEncode desktop app: {ex.Message}", ex);
        }
        catch (DesktopAutomationException ex)
        {
            throw new McpException($"{ex.Code}: {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Best-effort notification (e.g. "templates changed"); ignored when the app is not running.
    /// </summary>
    public async Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        try
        {
            await _client.SendRawAsync(method, parameters, cancellationToken, ProbeConnectTimeout);
        }
        catch (Exception ex) when (ex is DesktopUnavailableException or DesktopAutomationException or TimeoutException)
        {
            _logger.LogDebug(ex, "Desktop notification {Method} was not delivered.", method);
        }
    }

    public async Task<DesktopTaskStatus> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        var status = await TryGetStatusAsync(cancellationToken);
        if (status is { IsReady: true })
        {
            return status;
        }

        await _launchGate.WaitAsync(cancellationToken);
        try
        {
            status = await TryGetStatusAsync(cancellationToken);
            if (status is null)
            {
                Launch();
            }

            var deadline = DateTime.UtcNow + LaunchTimeout;
            while (DateTime.UtcNow < deadline)
            {
                status = await TryGetStatusAsync(cancellationToken);
                if (status is { IsReady: true })
                {
                    return status;
                }

                await Task.Delay(LaunchPollInterval, cancellationToken);
            }

            throw new McpException(status is null
                ? "The FlowEncode desktop app was started but did not open its automation channel in time."
                : "The FlowEncode desktop app is still initializing. Try again in a moment.");
        }
        finally
        {
            _launchGate.Release();
        }
    }

    private void Launch()
    {
        var executablePath = DesktopAppLocator.Find()
            ?? throw new McpException(
                "The FlowEncode desktop app is not running and FlowEncode.exe could not be found. "
                + "Start FlowEncode, or set FLOWENCODE_DESKTOP_EXE to the full path of FlowEncode.exe.");

        _logger.LogInformation("Starting the FlowEncode desktop app: {Path}", executablePath);
        try
        {
            // Shell execute so the desktop app does not inherit this process's stdio pipes,
            // which carry the MCP stream and must close when the client disconnects.
            using var process = Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)
            });
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to start the FlowEncode desktop app ({executablePath}): {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Finds FlowEncode.exe: an explicit override, the install layout ({app}\mcp\FlowEncode.Mcp.exe next to
/// {app}\FlowEncode.exe), then the installer's registered location.
/// </summary>
public static class DesktopAppLocator
{
    public const string OverrideEnvironmentVariable = "FLOWENCODE_DESKTOP_EXE";
    private const string ExecutableName = "FlowEncode.exe";
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{679B3447-8856-4CA5-99F4-291221F43274}_is1";

    public static string? Find()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public static IEnumerable<string> EnumerateCandidates()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return overridePath.Trim().Trim('"');
        }

        var baseDirectory = AppContext.BaseDirectory;
        var parent = Directory.GetParent(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is not null)
        {
            yield return Path.Combine(parent.FullName, ExecutableName);
        }

        yield return Path.Combine(baseDirectory, ExecutableName);

        var installLocation = ReadInstallLocation();
        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            yield return Path.Combine(installLocation, ExecutableName);
        }
    }

    /// <summary>
    /// The installer root when running from the packaged layout, so LocalAppPaths applies the same
    /// "workspace must not live inside the install folder" rule as the desktop app.
    /// </summary>
    public static string? ResolveInstallRoot()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(baseDirectory);
        return parent is not null
            && string.Equals(Path.GetFileName(baseDirectory), "mcp", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(parent.FullName, ExecutableName))
                ? parent.FullName
                : null;
    }

    private static string? ReadInstallLocation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(UninstallKeyPath);
                if (key?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                {
                    return location;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return null;
    }
}
