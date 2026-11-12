using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace FlowEncode.Mcp;

internal static class McpJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
