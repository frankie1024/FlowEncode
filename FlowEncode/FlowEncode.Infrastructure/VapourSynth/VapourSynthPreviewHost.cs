using System.Diagnostics;
using System.Text;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal interface IVapourSynthPreviewHostFactory
{
    Task<IVapourSynthPreviewHostSession> StartAsync(
        string workingDirectory,
        string startupPath,
        Action<string>? stderrLineHandler,
        CancellationToken cancellationToken = default);
}

internal interface IVapourSynthPreviewHostSession : IDisposable
{
    bool HasExited { get; }

    int ProcessId { get; }

    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);

    Task<string?> ReadLineAsync(CancellationToken cancellationToken = default);

    Task WaitForExitAsync(CancellationToken cancellationToken = default);

    void Kill(bool entireProcessTree = true);
}

internal sealed class ProcessVapourSynthPreviewHostFactory : IVapourSynthPreviewHostFactory
{
    private readonly IToolProbeService _toolProbeService;

    public ProcessVapourSynthPreviewHostFactory(IToolProbeService toolProbeService)
    {
        _toolProbeService = toolProbeService;
    }

    public async Task<IVapourSynthPreviewHostSession> StartAsync(
        string workingDirectory,
        string startupPath,
        Action<string>? stderrLineHandler,
        CancellationToken cancellationToken = default)
    {
        var pythonPath = await ResolvePythonPathAsync(cancellationToken);
        var helperPath = GetHelperPath();

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        PythonProcessStartInfoHelper.ApplyUtf8(startInfo);
        startInfo.ArgumentList.Add(helperPath);
        startInfo.ArgumentList.Add(startupPath);
        VapourSynthRuntimePathResolver.EnrichProcessPath(startInfo);

        return ProcessVapourSynthPreviewHostSession.Start(startInfo, stderrLineHandler);
    }

    private async Task<string> ResolvePythonPathAsync(CancellationToken cancellationToken)
    {
        var probe = await _toolProbeService.ProbeAsync(RegisteredToolKind.Python, cancellationToken);
        if (!probe.IsReady || string.IsNullOrWhiteSpace(probe.ExecutablePath))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(probe.FailureReason)
                ? "Python runtime is unavailable."
                : probe.FailureReason);
        }

        return probe.ExecutablePath;
    }

    private static string GetHelperPath()
    {
        var helperPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "VapourSynthEditor",
            "python",
            "preview_host.py");

        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("VapourSynth preview helper was not found.", helperPath);
        }

        return helperPath;
    }
}

internal sealed class ProcessVapourSynthPreviewHostSession : IVapourSynthPreviewHostSession
{
    private readonly Process _process;
    private readonly Action<string>? _stderrLineHandler;
    private int _disposed;

    private ProcessVapourSynthPreviewHostSession(Process process, Action<string>? stderrLineHandler)
    {
        _process = process;
        _stderrLineHandler = stderrLineHandler;
        _process.ErrorDataReceived += Process_ErrorDataReceived;
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }

    public int ProcessId
    {
        get
        {
            try
            {
                return _process.Id;
            }
            catch
            {
                return -1;
            }
        }
    }

    public static ProcessVapourSynthPreviewHostSession Start(
        ProcessStartInfo startInfo,
        Action<string>? stderrLineHandler = null)
    {
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        var session = new ProcessVapourSynthPreviewHostSession(process, stderrLineHandler);

        using var _ = ErrorDialogSuppression.Enter();
        try
        {
            process.Start();
        }
        catch
        {
            session.Dispose();
            throw;
        }

        process.BeginErrorReadLine();
        process.StandardInput.AutoFlush = true;
        return session;
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        return _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        return _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        return _process.WaitForExitAsync(cancellationToken);
    }

    public void Kill(bool entireProcessTree = true)
    {
        _process.Kill(entireProcessTree);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _process.ErrorDataReceived -= Process_ErrorDataReceived;
        _process.Dispose();
    }

    private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            _stderrLineHandler?.Invoke(e.Data);
        }
    }
}
