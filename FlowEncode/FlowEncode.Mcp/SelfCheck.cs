using System.Text.Json;
using FlowEncode.Infrastructure.Automation;

namespace FlowEncode.Mcp;

/// <summary>
/// <c>FlowEncode.Mcp.exe --check</c>: verifies the installation and prints a ready-to-paste client configuration.
/// </summary>
internal static class SelfCheck
{
    public static async Task<int> RunAsync()
    {
        var serverPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FlowEncode.Mcp.exe");
        var desktopPath = DesktopAppLocator.Find();
        var client = new DesktopAutomationClient();

        Console.WriteLine($"FlowEncode MCP server {typeof(SelfCheck).Assembly.GetName().Version?.ToString(3)}");
        Console.WriteLine($"  Server executable : {serverPath}");
        Console.WriteLine($"  Desktop app       : {desktopPath ?? "NOT FOUND (set " + DesktopAppLocator.OverrideEnvironmentVariable + ")"}");
        Console.WriteLine($"  Automation pipe   : {client.PipeName}");

        AutomationAppStatus? status = null;
        try
        {
            status = await client.SendAsync<AutomationAppStatus>(
                DesktopAutomationMethods.AppStatus,
                connectTimeout: TimeSpan.FromSeconds(1));
        }
        catch (DesktopUnavailableException)
        {
        }
        catch (Exception ex) when (ex is DesktopAutomationException or TimeoutException)
        {
            Console.WriteLine($"  Desktop status    : error - {ex.Message}");
        }

        Console.WriteLine(status is null
            ? "  Desktop status    : not running (it is started automatically when a task needs it)"
            : $"  Desktop status    : running v{status.Version} (pid {status.ProcessId}), {status.Queue.Total} job(s) in queue");

        var configuration = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["flowencode"] = new Dictionary<string, object> { ["command"] = serverPath }
            }
        };

        Console.WriteLine();
        Console.WriteLine("Claude Code:");
        Console.WriteLine($"  claude mcp add flowencode -- \"{serverPath}\"");
        Console.WriteLine("Other MCP clients (mcpServers JSON):");
        Console.WriteLine(JsonSerializer.Serialize(configuration, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));

        return desktopPath is null ? 1 : 0;
    }
}
