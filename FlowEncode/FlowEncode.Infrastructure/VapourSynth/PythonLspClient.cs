using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowEncode.Application;

namespace FlowEncode.Infrastructure;

internal sealed class PythonLspClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _pythonPath;
    private readonly string? _additionalPythonPath;
    private readonly string _workspaceRootPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellationTokenSource = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonElement?>> _pendingRequests = [];
    private readonly object _stateLock = new();
    private readonly StringBuilder _stderrBuffer = new();

    private Process? _process;
    private Task? _readerTask;
    private Task? _stderrTask;
    private Task? _exitTask;
    private bool _isInitialized;
    private bool _disposed;
    private long _nextRequestId;
    private string? _activeDocumentUri;
    private string? _activeDocumentText;
    private int _activeDocumentVersion;

    public PythonLspClient(string pythonPath, string? additionalPythonPath, string workspaceRootPath)
    {
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            throw new ArgumentException("Python path cannot be empty.", nameof(pythonPath));
        }

        if (string.IsNullOrWhiteSpace(workspaceRootPath))
        {
            throw new ArgumentException("Workspace root path cannot be empty.", nameof(workspaceRootPath));
        }

        _pythonPath = pythonPath;
        _additionalPythonPath = additionalPythonPath;
        _workspaceRootPath = workspaceRootPath;

        Directory.CreateDirectory(_workspaceRootPath);
    }

    public bool IsRunning => !_disposed
        && _isInitialized
        && _process is { HasExited: false };

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (IsRunning)
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _workspaceRootPath,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("jedi_language_server.cli");
        PythonProcessStartInfoHelper.ApplyUtf8(startInfo);
        ApplyAdditionalPythonPath(startInfo, _additionalPythonPath);
        VapourSynthRuntimePathResolver.EnrichProcessPath(startInfo);

        var process = new Process
        {
            StartInfo = startInfo
        };

        using var _ = ErrorDialogSuppression.Enter();
        process.Start();

        _process = process;
        _readerTask = Task.Run(() => ReadLoopAsync(process.StandardOutput.BaseStream, _shutdownCancellationTokenSource.Token));
        _stderrTask = Task.Run(() => ReadErrorLoopAsync(process.StandardError, _shutdownCancellationTokenSource.Token));
        _exitTask = Task.Run(() => ObserveProcessExitAsync(process));

        try
        {
            await SendRequestAsync(
                "initialize",
                new
                {
                    processId = Environment.ProcessId,
                    rootUri = new Uri(_workspaceRootPath).AbsoluteUri,
                    capabilities = new
                    {
                        textDocument = new
                        {
                            completion = new
                            {
                                completionItem = new
                                {
                                    snippetSupport = true,
                                    documentationFormat = new[] { "markdown", "plaintext" }
                                }
                            },
                            hover = new
                            {
                                contentFormat = new[] { "markdown", "plaintext" }
                            },
                            signatureHelp = new
                            {
                                signatureInformation = new
                                {
                                    documentationFormat = new[] { "markdown", "plaintext" },
                                    parameterInformation = new
                                    {
                                        labelOffsetSupport = true
                                    }
                                }
                            }
                        }
                    },
                    clientInfo = new
                    {
                        name = "FlowEncode VapourSynth Workspace",
                        version = "1.0"
                    }
                },
                cancellationToken,
                allowBeforeInitialization: true);

            _isInitialized = true;
            await SendNotificationAsync("initialized", new { }, cancellationToken);
        }
        catch (Exception ex)
        {
            Dispose();
            throw new InvalidOperationException(BuildFailureMessage(
                "Python language server failed to start.",
                ex.Message));
        }
    }

    public async Task<IReadOnlyList<VapourSynthPythonCompletionItem>> GetCompletionsAsync(
        VapourSynthTextDocumentContext document,
        VapourSynthTextDocumentPosition position,
        string? triggerCharacter,
        CancellationToken cancellationToken)
    {
        object completionContext = string.IsNullOrWhiteSpace(triggerCharacter)
            ? new { triggerKind = 1 }
            : new { triggerKind = 2, triggerCharacter };
        var documentUri = await EnsureDocumentSynchronizedAsync(document, cancellationToken);
        var result = await SendRequestAsync(
            "textDocument/completion",
            new
            {
                textDocument = new { uri = documentUri },
                position = CreatePosition(position),
                context = completionContext
            },
            cancellationToken);

        return MapCompletions(result);
    }

    public async Task<VapourSynthPythonHover?> GetHoverAsync(
        VapourSynthTextDocumentContext document,
        VapourSynthTextDocumentPosition position,
        CancellationToken cancellationToken)
    {
        var documentUri = await EnsureDocumentSynchronizedAsync(document, cancellationToken);
        var result = await SendRequestAsync(
            "textDocument/hover",
            new
            {
                textDocument = new { uri = documentUri },
                position = CreatePosition(position)
            },
            cancellationToken);

        return MapHover(result);
    }

    public async Task<VapourSynthPythonSignatureHelp?> GetSignatureHelpAsync(
        VapourSynthTextDocumentContext document,
        VapourSynthTextDocumentPosition position,
        CancellationToken cancellationToken)
    {
        var documentUri = await EnsureDocumentSynchronizedAsync(document, cancellationToken);
        var result = await SendRequestAsync(
            "textDocument/signatureHelp",
            new
            {
                textDocument = new { uri = documentUri },
                position = CreatePosition(position)
            },
            cancellationToken);

        return MapSignatureHelp(result);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isInitialized = false;
        _shutdownCancellationTokenSource.Cancel();
        FailPendingRequests(new ObjectDisposedException(nameof(PythonLspClient)));

        try
        {
            if (_process is not null)
            {
                try
                {
                    _process.StandardInput.Close();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to close Python LSP stdin. {ex}");
                }

                if (!_process.HasExited)
                {
                    if (!_process.WaitForExit(1500))
                    {
                        _process.Kill(true);
                    }
                }

                _process.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to dispose Python LSP process. {ex}");
        }

        _writeLock.Dispose();
        _shutdownCancellationTokenSource.Dispose();
    }

    private async Task<string> EnsureDocumentSynchronizedAsync(
        VapourSynthTextDocumentContext document,
        CancellationToken cancellationToken)
    {
        var content = document.Content ?? string.Empty;
        var path = ResolveDocumentPath(document.FilePath);
        var documentUri = new Uri(path).AbsoluteUri;

        if (string.IsNullOrWhiteSpace(document.FilePath))
        {
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
        }

        if (!string.Equals(_activeDocumentUri, documentUri, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(_activeDocumentUri))
            {
                await SendNotificationAsync(
                    "textDocument/didClose",
                    new
                    {
                        textDocument = new
                        {
                            uri = _activeDocumentUri
                        }
                    },
                    cancellationToken);
            }

            _activeDocumentUri = documentUri;
            _activeDocumentText = content;
            _activeDocumentVersion = 1;

            await SendNotificationAsync(
                "textDocument/didOpen",
                new
                {
                    textDocument = new
                    {
                        uri = documentUri,
                        languageId = "python",
                        version = _activeDocumentVersion,
                        text = content
                    }
                },
                cancellationToken);

            return documentUri;
        }

        if (!string.Equals(_activeDocumentText, content, StringComparison.Ordinal))
        {
            _activeDocumentText = content;
            _activeDocumentVersion += 1;

            await SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new
                    {
                        uri = documentUri,
                        version = _activeDocumentVersion
                    },
                    contentChanges = new[]
                    {
                        new
                        {
                            text = content
                        }
                    }
                },
                cancellationToken);
        }

        return documentUri;
    }

    private string ResolveDocumentPath(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return Path.GetFullPath(filePath);
        }

        var shadowPath = Path.Combine(_workspaceRootPath, "untitled.vpy");
        Directory.CreateDirectory(Path.GetDirectoryName(shadowPath)!);
        return shadowPath;
    }

    private async Task<JsonElement?> SendRequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken,
        bool allowBeforeInitialization = false)
    {
        ThrowIfDisposed();

        if (!allowBeforeInitialization && !_isInitialized)
        {
            throw new InvalidOperationException("Python language server is not initialized.");
        }

        if (_process is null || _process.HasExited)
        {
            throw new InvalidOperationException(BuildFailureMessage("Python language server is not running.", null));
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completionSource = new TaskCompletionSource<JsonElement?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingRequests)
        {
            _pendingRequests[requestId] = completionSource;
        }

        using var cancellationRegistration = cancellationToken.Register(() => CancelPendingRequest(requestId, cancellationToken));

        try
        {
            await WriteMessageAsync(
                new
                {
                    jsonrpc = "2.0",
                    id = requestId,
                    method,
                    @params = parameters
                },
                cancellationToken);

            return await completionSource.Task.WaitAsync(cancellationToken);
        }
        catch
        {
            lock (_pendingRequests)
            {
                _pendingRequests.Remove(requestId);
            }

            throw;
        }
    }

    private async Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await WriteMessageAsync(
            new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters
            },
            cancellationToken);
    }

    private async Task WriteMessageAsync(object payload, CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited)
        {
            throw new InvalidOperationException(BuildFailureMessage("Python language server process is unavailable.", null));
        }

        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");

        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            var input = _process.StandardInput.BaseStream;
            await input.WriteAsync(headerBytes, cancellationToken);
            await input.WriteAsync(bodyBytes, cancellationToken);
            await input.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(Stream outputStream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(outputStream, cancellationToken);
                if (message is null)
                {
                    break;
                }

                using var document = JsonDocument.Parse(message);
                HandleIncomingMessage(document.RootElement);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during Python LSP shutdown.
        }
        catch (Exception ex)
        {
            if (!_shutdownCancellationTokenSource.IsCancellationRequested)
            {
                FailPendingRequests(new InvalidOperationException(BuildFailureMessage(
                    "Python language server connection failed.",
                    ex.Message)));
            }
        }
    }

    private async Task ReadErrorLoopAsync(StreamReader errorReader, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await errorReader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                AppendErrorText(new string(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during Python LSP shutdown.
        }
    }

    private async Task ObserveProcessExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(_shutdownCancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_shutdownCancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        _isInitialized = false;
        FailPendingRequests(new InvalidOperationException(BuildFailureMessage(
            $"Python language server exited with code {process.ExitCode}.",
            null)));
    }

    private void HandleIncomingMessage(JsonElement payload)
    {
        if (!payload.TryGetProperty("id", out var idProperty))
        {
            return;
        }

        var requestId = ReadRequestId(idProperty);
        if (requestId < 0)
        {
            return;
        }

        TaskCompletionSource<JsonElement?>? pendingRequest;
        lock (_pendingRequests)
        {
            if (!_pendingRequests.Remove(requestId, out pendingRequest))
            {
                return;
            }
        }

        if (payload.TryGetProperty("error", out var errorProperty)
            && errorProperty.ValueKind == JsonValueKind.Object)
        {
            pendingRequest.TrySetException(new InvalidOperationException(FormatError(errorProperty)));
            return;
        }

        if (payload.TryGetProperty("result", out var resultProperty))
        {
            pendingRequest.TrySetResult(resultProperty.Clone());
            return;
        }

        pendingRequest.TrySetResult(null);
    }

    private void CancelPendingRequest(long requestId, CancellationToken cancellationToken)
    {
        TaskCompletionSource<JsonElement?>? pendingRequest;
        lock (_pendingRequests)
        {
            if (!_pendingRequests.Remove(requestId, out pendingRequest))
            {
                return;
            }
        }

        pendingRequest.TrySetCanceled(cancellationToken);
    }

    private void FailPendingRequests(Exception exception)
    {
        List<TaskCompletionSource<JsonElement?>> pendingRequests;
        lock (_pendingRequests)
        {
            pendingRequests = _pendingRequests.Values.ToList();
            _pendingRequests.Clear();
        }

        foreach (var pendingRequest in pendingRequests)
        {
            pendingRequest.TrySetException(exception);
        }
    }

    private void AppendErrorText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_stateLock)
        {
            _stderrBuffer.Append(text);

            if (_stderrBuffer.Length > 8192)
            {
                _stderrBuffer.Remove(0, _stderrBuffer.Length - 8192);
            }
        }
    }

    private string BuildFailureMessage(string prefix, string? extraDetail)
    {
        var stderr = ReadErrorTail();
        var parts = new[]
        {
            prefix,
            extraDetail,
            stderr
        }.Where(static value => !string.IsNullOrWhiteSpace(value));

        return string.Join(Environment.NewLine, parts);
    }

    private string? ReadErrorTail()
    {
        lock (_stateLock)
        {
            return _stderrBuffer.Length == 0
                ? null
                : _stderrBuffer.ToString().Trim();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PythonLspClient));
        }
    }

    private static object CreatePosition(VapourSynthTextDocumentPosition position)
    {
        return new
        {
            line = Math.Max(0, position.Line - 1),
            character = Math.Max(0, position.Column - 1)
        };
    }

    private static void ApplyAdditionalPythonPath(ProcessStartInfo startInfo, string? additionalPythonPath)
    {
        if (string.IsNullOrWhiteSpace(additionalPythonPath))
        {
            return;
        }

        var existingValue = startInfo.Environment.TryGetValue("PYTHONPATH", out var currentValue)
            ? currentValue ?? string.Empty
            : Environment.GetEnvironmentVariable("PYTHONPATH") ?? string.Empty;

        var segments = existingValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!segments.Any(path => string.Equals(path, additionalPythonPath, StringComparison.OrdinalIgnoreCase)))
        {
            segments.Insert(0, additionalPythonPath);
        }

        startInfo.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, segments);
    }

    private static long ReadRequestId(JsonElement idProperty)
    {
        if (idProperty.ValueKind == JsonValueKind.Number && idProperty.TryGetInt64(out var numericId))
        {
            return numericId;
        }

        if (idProperty.ValueKind == JsonValueKind.String
            && long.TryParse(idProperty.GetString(), out var textId))
        {
            return textId;
        }

        return -1;
    }

    private static string FormatError(JsonElement errorProperty)
    {
        var message = TryGetString(errorProperty, "message") ?? "Unknown language server error.";
        var errorCode = errorProperty.TryGetProperty("code", out var codeProperty) && codeProperty.TryGetInt32(out var code)
            ? code
            : 0;

        return errorCode == 0
            ? message
            : $"[{errorCode}] {message}";
    }

    private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>(256);
        var singleByteBuffer = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(singleByteBuffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return headerBytes.Count == 0
                    ? null
                    : throw new EndOfStreamException("Unexpected end of stream while reading LSP headers.");
            }

            headerBytes.Add(singleByteBuffer[0]);

            if (headerBytes.Count >= 4
                && headerBytes[^4] == '\r'
                && headerBytes[^3] == '\n'
                && headerBytes[^2] == '\r'
                && headerBytes[^1] == '\n')
            {
                break;
            }

            if (headerBytes.Count > 32 * 1024)
            {
                throw new InvalidOperationException("The language server returned an oversized header.");
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var contentLength = ParseContentLength(headerText);
        var contentBytes = new byte[contentLength];
        await ReadExactlyAsync(stream, contentBytes, cancellationToken);
        return Encoding.UTF8.GetString(contentBytes);
    }

    private static int ParseContentLength(string headerText)
    {
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["Content-Length:".Length..].Trim();
            if (int.TryParse(value, out var contentLength) && contentLength >= 0)
            {
                return contentLength;
            }
        }

        throw new InvalidOperationException("The language server response did not include a valid Content-Length header.");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading the language server payload.");
            }

            offset += read;
        }
    }

    private static IReadOnlyList<VapourSynthPythonCompletionItem> MapCompletions(JsonElement? result)
    {
        if (!TryGetCompletionItems(result, out var itemsElement))
        {
            return [];
        }

        var items = new List<VapourSynthPythonCompletionItem>();

        foreach (var itemElement in itemsElement.EnumerateArray())
        {
            var label = TryGetString(itemElement, "label");
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var detail = TryGetString(itemElement, "detail") ?? string.Empty;
            var documentation = TryGetProperty(itemElement, "documentation", out var documentationElement)
                ? ConvertMarkupToMarkdown(documentationElement)
                : string.Empty;
            var insertText = TryGetString(itemElement, "insertText");

            if (string.IsNullOrWhiteSpace(insertText)
                && TryGetProperty(itemElement, "textEdit", out var textEditElement))
            {
                insertText = TryGetString(textEditElement, "newText")
                    ?? (TryGetProperty(textEditElement, "textEdit", out var nestedTextEdit)
                        ? TryGetString(nestedTextEdit, "newText")
                        : null);
            }

            items.Add(new VapourSynthPythonCompletionItem(
                label,
                MapCompletionKind(ReadInt(itemElement, "kind")),
                detail,
                documentation,
                string.IsNullOrWhiteSpace(insertText) ? label : insertText,
                TryGetString(itemElement, "sortText") ?? label,
                TryGetString(itemElement, "filterText") ?? label,
                ReadInt(itemElement, "insertTextFormat") == 2));
        }

        return items;
    }

    private static VapourSynthPythonHover? MapHover(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetProperty(result.Value, "contents", out var contentsElement))
        {
            return null;
        }

        var markdown = ConvertMarkupToMarkdown(contentsElement);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        VapourSynthTextRange? range = null;
        if (TryGetProperty(result.Value, "range", out var rangeElement))
        {
            range = MapRange(rangeElement);
        }

        return new VapourSynthPythonHover(range, markdown);
    }

    private static VapourSynthPythonSignatureHelp? MapSignatureHelp(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetProperty(result.Value, "signatures", out var signaturesElement)
            || signaturesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var signatures = new List<VapourSynthPythonSignature>();

        foreach (var signatureElement in signaturesElement.EnumerateArray())
        {
            var label = TryGetString(signatureElement, "label");
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var documentation = TryGetProperty(signatureElement, "documentation", out var documentationElement)
                ? ConvertMarkupToMarkdown(documentationElement)
                : string.Empty;
            var parameters = new List<VapourSynthPythonSignatureParameter>();

            if (TryGetProperty(signatureElement, "parameters", out var parameterElements)
                && parameterElements.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameterElement in parameterElements.EnumerateArray())
                {
                    parameters.Add(new VapourSynthPythonSignatureParameter(
                        ExtractParameterLabel(parameterElement, label),
                        TryGetProperty(parameterElement, "documentation", out var parameterDocumentation)
                            ? ConvertMarkupToMarkdown(parameterDocumentation)
                            : string.Empty));
                }
            }

            signatures.Add(new VapourSynthPythonSignature(label, documentation, parameters));
        }

        return signatures.Count == 0
            ? null
            : new VapourSynthPythonSignatureHelp(
                Math.Max(0, ReadInt(result.Value, "activeSignature")),
                Math.Max(0, ReadInt(result.Value, "activeParameter")),
                signatures);
    }

    private static bool TryGetCompletionItems(JsonElement? result, out JsonElement itemsElement)
    {
        itemsElement = default;

        if (result is null)
        {
            return false;
        }

        if (result.Value.ValueKind == JsonValueKind.Array)
        {
            itemsElement = result.Value;
            return true;
        }

        if (result.Value.ValueKind == JsonValueKind.Object
            && TryGetProperty(result.Value, "items", out itemsElement)
            && itemsElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        return false;
    }

    private static VapourSynthTextRange? MapRange(JsonElement rangeElement)
    {
        if (rangeElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetProperty(rangeElement, "start", out var start)
            || !TryGetProperty(rangeElement, "end", out var end))
        {
            return null;
        }

        return new VapourSynthTextRange(
            ReadInt(start, "line") + 1,
            ReadInt(start, "character") + 1,
            ReadInt(end, "line") + 1,
            ReadInt(end, "character") + 1);
    }

    private static string ExtractParameterLabel(JsonElement parameterElement, string signatureLabel)
    {
        if (!TryGetProperty(parameterElement, "label", out var labelElement))
        {
            return string.Empty;
        }

        return labelElement.ValueKind switch
        {
            JsonValueKind.String => labelElement.GetString() ?? string.Empty,
            JsonValueKind.Array => ExtractOffsetLabel(labelElement, signatureLabel),
            _ => string.Empty
        };
    }

    private static string ExtractOffsetLabel(JsonElement labelElement, string signatureLabel)
    {
        var offsets = labelElement.EnumerateArray().ToArray();
        if (offsets.Length != 2
            || !offsets[0].TryGetInt32(out var start)
            || !offsets[1].TryGetInt32(out var end)
            || start < 0
            || end <= start
            || start >= signatureLabel.Length)
        {
            return string.Empty;
        }

        var length = Math.Min(signatureLabel.Length, end) - start;
        return length > 0 ? signatureLabel.Substring(start, length) : string.Empty;
    }

    private static string ConvertMarkupToMarkdown(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                Environment.NewLine + Environment.NewLine,
                element.EnumerateArray()
                    .Select(ConvertMarkupToMarkdown)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))),
            JsonValueKind.Object when TryGetString(element, "kind") is { } kind && TryGetString(element, "value") is { } value
                => string.Equals(kind, "markdown", StringComparison.OrdinalIgnoreCase) ? value : value,
            JsonValueKind.Object when TryGetString(element, "language") is { } language && TryGetString(element, "value") is { } code
                => $"```{language}{Environment.NewLine}{code}{Environment.NewLine}```",
            _ => string.Empty
        };
    }

    private static string MapCompletionKind(int kind)
    {
        return kind switch
        {
            2 => "method",
            3 => "function",
            4 => "constructor",
            5 => "field",
            6 => "variable",
            7 => "class",
            8 => "interface",
            9 => "module",
            10 => "property",
            11 => "unit",
            12 => "value",
            13 => "enum",
            14 => "keyword",
            15 => "snippet",
            16 => "color",
            17 => "file",
            18 => "reference",
            19 => "folder",
            20 => "enumMember",
            21 => "constant",
            22 => "struct",
            23 => "event",
            24 => "operator",
            25 => "typeParameter",
            _ => "text"
        };
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        property = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out property);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;
    }
}
