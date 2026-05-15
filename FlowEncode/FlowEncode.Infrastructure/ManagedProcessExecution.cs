using System.Diagnostics;

namespace FlowEncode.Infrastructure;

internal sealed class ManagedProcessExecution : IDisposable
{
    private readonly IReadOnlyList<Process> _processes;
    private readonly IReadOnlyList<ProcessJobObject> _jobObjects;
    private readonly Action<string>? _onDiagnostic;
    private int _disposed;

    public ManagedProcessExecution(params Process[] processes)
        : this(null, processes)
    {
    }

    public ManagedProcessExecution(Action<string>? onDiagnostic, params Process[] processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (processes.Length == 0)
        {
            throw new ArgumentException("At least one process is required.", nameof(processes));
        }

        _onDiagnostic = onDiagnostic;
        _processes = processes.ToArray();
        _jobObjects = _processes
            .Select(process => ProcessJobObject.TryAttach(process, onDiagnostic))
            .Where(static jobObject => jobObject is not null)
            .Select(static jobObject => jobObject!)
            .ToArray();
    }

    public void Terminate()
    {
        foreach (var jobObject in _jobObjects)
        {
            jobObject.Terminate();
        }

        foreach (var process in _processes)
        {
            TryTerminate(process);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var jobObject in _jobObjects)
        {
            jobObject.Dispose();
        }

        foreach (var process in _processes)
        {
            process.Dispose();
        }
    }

    private void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            ReportTerminationFailure(process, ex);
        }
    }

    private void ReportTerminationFailure(Process process, Exception ex)
    {
        if (_onDiagnostic is null)
        {
            return;
        }

        try
        {
            var processId = TryGetProcessId(process);
            var executablePath = string.IsNullOrWhiteSpace(process.StartInfo.FileName)
                ? "unknown"
                : process.StartInfo.FileName;
            _onDiagnostic(
                $"Failed to terminate PID {processId} ({executablePath}). {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception diagnosticException)
        {
            Debug.WriteLine($"Failed to report process termination failure. {diagnosticException}");
        }
    }

    private static int TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }
}
