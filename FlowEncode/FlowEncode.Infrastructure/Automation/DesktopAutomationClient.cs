using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FlowEncode.Infrastructure.Automation;

/// <summary>
/// Sends one automation request per connection to the running FlowEncode desktop app.
/// </summary>
public sealed class DesktopAutomationClient
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(60);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public DesktopAutomationClient(string? pipeName = null)
    {
        PipeName = string.IsNullOrWhiteSpace(pipeName) ? DesktopAutomationProtocol.GetPipeName() : pipeName;
    }

    public string PipeName { get; }

    public async Task<T> SendAsync<T>(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default,
        TimeSpan? connectTimeout = null,
        TimeSpan? responseTimeout = null)
    {
        var result = await SendRawAsync(method, parameters, cancellationToken, connectTimeout, responseTimeout);
        if (result is null || result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default!;
        }

        return result.Value.Deserialize<T>(DesktopAutomationProtocol.JsonOptions)!;
    }

    public async Task<JsonElement?> SendRawAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default,
        TimeSpan? connectTimeout = null,
        TimeSpan? responseTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        await using var pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using (var connectTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeoutSource.CancelAfter(connectTimeout ?? DefaultConnectTimeout);
            try
            {
                await pipe.ConnectAsync(connectTimeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DesktopUnavailableException("The FlowEncode desktop app is not running.");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                throw new DesktopUnavailableException("The FlowEncode desktop app is not reachable.", ex);
            }
        }

        using var responseTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeoutSource.CancelAfter(responseTimeout ?? DefaultResponseTimeout);
        string? line;
        try
        {
            var request = new DesktopAutomationRequest(
                method,
                parameters is null ? null : JsonSerializer.SerializeToElement(parameters, DesktopAutomationProtocol.JsonOptions));
            var payload = JsonSerializer.Serialize(request, DesktopAutomationProtocol.JsonOptions);
            await pipe.WriteAsync(Utf8NoBom.GetBytes(payload + "\n"), responseTimeoutSource.Token);
            await pipe.FlushAsync(responseTimeoutSource.Token);
            line = await DesktopAutomationWire.ReadLineAsync(pipe, responseTimeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The FlowEncode desktop app did not answer '{method}' in time.");
        }
        catch (IOException ex)
        {
            throw new DesktopUnavailableException("The connection to the FlowEncode desktop app was lost.", ex);
        }

        if (line is null)
        {
            throw new DesktopUnavailableException("The FlowEncode desktop app closed the connection without answering.");
        }

        var response = JsonSerializer.Deserialize<DesktopAutomationResponse>(line, DesktopAutomationProtocol.JsonOptions)
            ?? throw new InvalidDataException("Empty automation response.");
        if (!response.Ok)
        {
            throw new DesktopAutomationException(response.Error ?? $"'{method}' failed.");
        }

        return response.Result;
    }
}
