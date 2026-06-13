using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class VapourSynthWorkspaceLanguageService : IVapourSynthWorkspaceLanguageService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IToolProbeService _toolProbeService;
    private readonly SemaphoreSlim _pythonPathLock = new(1, 1);
    private readonly SemaphoreSlim _languageFeaturesLock = new(1, 1);
    private readonly SemaphoreSlim _pythonLspLock = new(1, 1);
    private readonly string _pythonLspRootPath;
    private readonly PythonLspBootstrapper _pythonLspBootstrapper;
    private string? _pythonPath;
    private VapourSynthLanguageFeaturesSnapshot? _cachedLanguageFeatures;
    private DateTimeOffset _cachedLanguageFeaturesAt;
    private PythonLspClient? _pythonLspClient;
    private bool _disposed;

    public VapourSynthWorkspaceLanguageService(IToolProbeService toolProbeService)
    {
        _toolProbeService = toolProbeService;
        _pythonLspRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowEncode",
            "VapourSynthEditor",
            "python-lsp");
        _pythonLspBootstrapper = new PythonLspBootstrapper(_pythonLspRootPath);
    }

    public async Task<VapourSynthLanguageFeaturesSnapshot> GetLanguageFeaturesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh
            && _cachedLanguageFeatures is not null
            && DateTimeOffset.UtcNow - _cachedLanguageFeaturesAt < TimeSpan.FromMinutes(5))
        {
            return _cachedLanguageFeatures;
        }

        await _languageFeaturesLock.WaitAsync(cancellationToken);

        try
        {
            if (!forceRefresh
                && _cachedLanguageFeatures is not null
                && DateTimeOffset.UtcNow - _cachedLanguageFeaturesAt < TimeSpan.FromMinutes(5))
            {
                return _cachedLanguageFeatures;
            }

            var helperPath = GetHelperPath("introspect_runtime.py");
            var snapshot = await RunHelperAsync<VapourSynthLanguageFeaturesSnapshot>(
                helperPath,
                null,
                cancellationToken);

            _cachedLanguageFeatures = snapshot;
            _cachedLanguageFeaturesAt = DateTimeOffset.UtcNow;
            return snapshot;
        }
        catch (Exception ex)
        {
            var unavailable = CreateUnavailableFeatures(ex.Message);
            _cachedLanguageFeatures = unavailable;
            _cachedLanguageFeaturesAt = DateTimeOffset.UtcNow;
            return unavailable;
        }
        finally
        {
            _languageFeaturesLock.Release();
        }
    }

    public async Task<VapourSynthScriptDiagnosticResult> DiagnoseScriptAsync(
        string? filePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var helperPath = GetHelperPath("diagnose_script.py");
            var payload = new ScriptDiagnosticRequest(
                string.IsNullOrWhiteSpace(filePath) ? "<untitled.vpy>" : filePath,
                content ?? string.Empty);

            return await RunHelperAsync<VapourSynthScriptDiagnosticResult>(
                helperPath,
                payload,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return new VapourSynthScriptDiagnosticResult(
                false,
                ex.Message,
                []);
        }
    }

    public async Task WarmupPythonLanguageServerAsync(CancellationToken cancellationToken = default)
    {
        await _pythonLspLock.WaitAsync(cancellationToken);

        try
        {
            _ = await EnsurePythonLanguageServerClientAsync(cancellationToken);
        }
        finally
        {
            _pythonLspLock.Release();
        }
    }

    public async Task<IReadOnlyList<VapourSynthPythonCompletionItem>> GetPythonCompletionsAsync(
        VapourSynthTextDocumentContext document,
        VapourSynthTextDocumentPosition position,
        string? triggerCharacter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(position);

        await _pythonLspLock.WaitAsync(cancellationToken);

        try
        {
            return await ExecutePythonLanguageServerRequestAsync(
                client => client.GetCompletionsAsync(document, position, triggerCharacter, cancellationToken),
                cancellationToken);
        }
        finally
        {
            _pythonLspLock.Release();
        }
    }

    private async Task<T> RunHelperAsync<T>(
        string helperPath,
        object? inputPayload,
        CancellationToken cancellationToken)
    {
        var pythonPath = await ResolvePythonPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            throw new InvalidOperationException("Python runtime is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            RedirectStandardInput = inputPayload is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(helperPath) ?? AppContext.BaseDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        PythonProcessStartInfoHelper.ApplyUtf8(startInfo);
        startInfo.ArgumentList.Add(helperPath);
        VapourSynthRuntimePathResolver.EnrichProcessPath(startInfo);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        using var _ = ErrorDialogSuppression.Enter();
        process.Start();

        if (inputPayload is not null)
        {
            var inputJson = JsonSerializer.Serialize(inputPayload, JsonOptions);
            await process.StandardInput.WriteAsync(inputJson.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "Language helper returned no output."
                : stderr.Trim());
        }

        var result = JsonSerializer.Deserialize<T>(stdout, JsonOptions);
        if (result is null)
        {
            throw new InvalidOperationException("Language helper returned invalid JSON.");
        }

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            throw new InvalidOperationException(stderr.Trim());
        }

        return result;
    }

    private async Task<string> ResolvePythonPathAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_pythonPath))
        {
            return _pythonPath;
        }

        await _pythonPathLock.WaitAsync(cancellationToken);

        try
        {
            if (!string.IsNullOrWhiteSpace(_pythonPath))
            {
                return _pythonPath;
            }

            var probe = await _toolProbeService.ProbeAsync(RegisteredToolKind.Python, cancellationToken);
            if (!probe.IsReady || string.IsNullOrWhiteSpace(probe.ExecutablePath))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(probe.FailureReason)
                    ? "Python runtime is unavailable."
                    : probe.FailureReason);
            }

            _pythonPath = probe.ExecutablePath;
            return _pythonPath;
        }
        finally
        {
            _pythonPathLock.Release();
        }
    }

    private async Task<T> ExecutePythonLanguageServerRequestAsync<T>(
        Func<PythonLspClient, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var client = await EnsurePythonLanguageServerClientAsync(cancellationToken);

        try
        {
            return await action(client);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            DisposePythonLanguageServerClient();
            client = await EnsurePythonLanguageServerClientAsync(cancellationToken);
            return await action(client);
        }
    }

    private async Task<PythonLspClient> EnsurePythonLanguageServerClientAsync(CancellationToken cancellationToken)
    {
        if (_pythonLspClient is { IsRunning: true })
        {
            return _pythonLspClient;
        }

        DisposePythonLanguageServerClient();

        var pythonPath = await ResolvePythonPathAsync(cancellationToken);
        var additionalPythonPath = await _pythonLspBootstrapper.EnsurePackagePathAsync(pythonPath, cancellationToken);
        var workspaceRootPath = Path.Combine(_pythonLspRootPath, "workspace");
        var client = new PythonLspClient(pythonPath, additionalPythonPath, workspaceRootPath);

        try
        {
            await client.EnsureStartedAsync(cancellationToken);
            _pythonLspClient = client;
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void DisposePythonLanguageServerClient()
    {
        if (_pythonLspClient is null)
        {
            return;
        }

        _pythonLspClient.Dispose();
        _pythonLspClient = null;
    }

    private static string GetHelperPath(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "VapourSynthEditor",
            "python",
            fileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("VapourSynth language helper was not found.", path);
        }

        return path;
    }

    private static VapourSynthLanguageFeaturesSnapshot CreateUnavailableFeatures(string detail)
    {
        return new VapourSynthLanguageFeaturesSnapshot(
            false,
            detail,
            [],
            []);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposePythonLanguageServerClient();
        _pythonLspBootstrapper.Dispose();
        _pythonPathLock.Dispose();
        _languageFeaturesLock.Dispose();
        _pythonLspLock.Dispose();
    }

    private sealed record ScriptDiagnosticRequest(string FilePath, string Content);
}
