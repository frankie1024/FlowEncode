using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowEncode.Infrastructure.Automation;

/// <summary>
/// Wire format of the local automation channel between the FlowEncode desktop app (server) and
/// automation clients such as the MCP server. One UTF-8 JSON line per request, one per response,
/// one request per connection. The pipe only accepts clients running as the same Windows user.
/// </summary>
public static class DesktopAutomationProtocol
{
    public const int Version = 1;

    /// <summary>Upper bound for a single request or response line.</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>
    /// Scoped to the Windows user and logon session so two users on one machine never reach each
    /// other's desktop app.
    /// </summary>
    public static string GetPipeName()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}".ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        return $"FlowEncode.Automation.v{Version}.{hash}.{sessionId}";
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record DesktopAutomationRequest(string Method, JsonElement? Params);

public sealed record DesktopAutomationResponse(bool Ok, JsonElement? Result, string? Error, string? ErrorCode = null);

/// <summary>
/// An expected, user-facing failure of an automation call (validation, busy module, unknown job...).
/// Only its message crosses the pipe.
/// </summary>
public sealed class DesktopAutomationException : Exception
{
    public string Code { get; }
    public DesktopAutomationException(string message, string code = "invalid_request")
        : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// The desktop app is not running (or not accepting automation requests yet).
/// </summary>
public sealed class DesktopUnavailableException : Exception
{
    public DesktopUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
