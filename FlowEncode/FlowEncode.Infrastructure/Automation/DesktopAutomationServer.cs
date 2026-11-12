using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FlowEncode.Infrastructure.Automation;

public delegate Task<object?> DesktopAutomationHandler(string method, JsonElement? parameters, CancellationToken cancellationToken);

/// <summary>
/// Accepts automation requests on a current-user-only named pipe and answers each with the
/// handler's result. Every connection is served independently so a slow call never blocks status polling.
/// </summary>
public sealed class DesktopAutomationServer : IAsyncDisposable
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(500);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private readonly DesktopAutomationHandler _handler;
    private readonly Action<string, Exception?>? _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _connectionsGate = new();
    private readonly HashSet<Task> _connections = [];
    private Task? _acceptLoop;

    public DesktopAutomationServer(string pipeName, DesktopAutomationHandler handler, Action<string, Exception?>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _log = log;
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        Task[] pending;
        lock (_connectionsGate)
        {
            pending = [.. _connections];
        }

        try
        {
            // DesktopTaskHost also disposes synchronously from the UI thread.
            await Task.WhenAll(pending.Append(_acceptLoop ?? Task.CompletedTask))
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
        {
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                Track(ServeAsync(pipe, cancellationToken));
                pipe = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke("Automation pipe accept failed.", ex);
                try
                {
                    await Task.Delay(RestartDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private void Track(Task connection)
    {
        lock (_connectionsGate)
        {
            _connections.Add(connection);
        }

        _ = connection.ContinueWith(
            completed =>
            {
                lock (_connectionsGate)
                {
                    _connections.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            DesktopAutomationResponse response;
            try
            {
                var line = await DesktopAutomationWire.ReadLineAsync(pipe, cancellationToken);
                var request = line is null
                    ? null
                    : JsonSerializer.Deserialize<DesktopAutomationRequest>(line, DesktopAutomationProtocol.JsonOptions);
                if (request is null || string.IsNullOrWhiteSpace(request.Method))
                {
                    response = new DesktopAutomationResponse(false, null, "Malformed automation request.");
                }
                else
                {
                    response = await InvokeAsync(request, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                response = new DesktopAutomationResponse(false, null, $"Malformed automation request: {ex.Message}");
            }
            catch (IOException)
            {
                return;
            }

            try
            {
                var payload = JsonSerializer.Serialize(response, DesktopAutomationProtocol.JsonOptions);
                await pipe.WriteAsync(Utf8NoBom.GetBytes(payload + "\n"), cancellationToken);
                await pipe.FlushAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client gave up waiting; nothing left to do.
            }
        }
    }

    private async Task<DesktopAutomationResponse> InvokeAsync(DesktopAutomationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _handler(request.Method, request.Params, cancellationToken);
            var element = JsonSerializer.SerializeToElement(result, DesktopAutomationProtocol.JsonOptions);
            return new DesktopAutomationResponse(true, element, null);
        }
        catch (DesktopAutomationException ex)
        {
            return new DesktopAutomationResponse(false, null, ex.Message, ex.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Automation method '{request.Method}' failed.", ex);
            return new DesktopAutomationResponse(false, null, ex.Message, ex switch { KeyNotFoundException => "not_found", ArgumentException => "invalid_argument", IOException => "io_error", _ => "invalid_state" });
        }
    }
}

internal static class DesktopAutomationWire
{
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            buffer.Write(chunk, 0, newline >= 0 ? newline : read);
            if (buffer.Length > DesktopAutomationProtocol.MaxMessageBytes)
            {
                throw new InvalidDataException("Automation message exceeds the size limit.");
            }

            if (newline >= 0)
            {
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
        }
    }
}
