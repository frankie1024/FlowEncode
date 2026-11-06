using FlowEncode.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Any(static arg => string.Equals(arg, "--check", StringComparison.OrdinalIgnoreCase)))
{
    // Installation check for users wiring up an MCP client: prints where the desktop app and
    // the automation channel are, without speaking the MCP protocol.
    return await SelfCheck.RunAsync();
}

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP JSON-RPC stream, so every log line must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddFlowEncodeServices();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "flowencode",
            Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
        };
        options.ServerInstructions = McpServerText.Instructions;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(serializerOptions: McpJson.Options)
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
    {
        context.Services?.GetService<WorkspaceRootSync>()?.Synchronize();
        return await next(context, cancellationToken);
    }));

await builder.Build().RunAsync();
return 0;
