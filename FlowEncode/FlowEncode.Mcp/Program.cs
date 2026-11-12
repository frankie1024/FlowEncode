using FlowEncode.Mcp;
using FlowEncode.Infrastructure.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Any(arg => arg.Equals("--check", StringComparison.OrdinalIgnoreCase))) return await SelfCheck.RunAsync();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<DesktopAutomationClient>();
builder.Services.AddSingleton<DesktopBridge>();
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new() { Name = "flowencode", Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "3.0.0" };
    options.ServerInstructions = "FlowEncode runs local media tasks in its desktop task center. Enable local MCP automation in Settings. "
        + "Probe media, inspect capabilities/templates, create an execution plan, inspect its command/output/warnings, then submit that plan with a stable idempotency key. "
        + "Reuse the same key and planId if a submission response is lost. Track/cancel by taskId. Accepted tasks continue if the client disconnects. "
        + "Overwrite defaults to false. DDP/deew outputPath is a directory; other audio/video outputs are files. Measure-only deew returns loudness without media. "
        + "Video encoding produces a video elementary stream; audio/subtitles are not automatically muxed. Use source audio-stream indexes from probe_media for deew trackIndex. "
        + "Use only available tools and checked parameter combinations. Plans expire after 15 minutes and become invalid when inputs, encoding settings or tools change.";
}).WithStdioServerTransport().WithToolsFromAssembly(serializerOptions: McpJson.Options);
await builder.Build().RunAsync();
return 0;
