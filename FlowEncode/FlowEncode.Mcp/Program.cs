using System.Text.Encodings.Web;
using System.Text.Json;
using FlowEncode.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP JSON-RPC stream, so every log line must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Tool results carry Chinese paths and messages; keep them readable instead of \uXXXX-escaped.
var toolSerializerOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

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
    .WithToolsFromAssembly(serializerOptions: toolSerializerOptions);

await builder.Build().RunAsync();
