using FlowEncode.Domain;

namespace FlowEncode.Application;

/// <summary>Bounds installs while protecting the shared Python environment.</summary>
public sealed class SetupDependencyInstallCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<SetupDependencyKind, Task> _operations = [];
    private readonly SemaphoreSlim _installSlots = new(3, 3);
    private readonly SemaphoreSlim _pythonEnvironment = new(1, 1);

    public Task RunAsync(
        SetupDependencyKind kind,
        Func<CancellationToken, Task> install,
        Action? reportWaiting = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] prerequisites;
        lock (_gate)
        {
            if (_operations.ContainsKey(kind))
            {
                throw new InvalidOperationException("This dependency already has an installation in progress.");
            }

            prerequisites = GetPrerequisites(kind)
                .Where(_operations.ContainsKey)
                .Select(dependency => _operations[dependency])
                .ToArray();
            _operations.Add(kind, completion.Task);
        }

        _ = ExecuteAsync();
        return completion.Task;

        async Task ExecuteAsync()
        {
            var hasPythonEnvironment = false;
            var hasInstallSlot = false;
            Exception? failure = null;
            try
            {
                reportWaiting?.Invoke();
                // A failed prerequisite must also stop the dependent install.
                await Task.WhenAll(prerequisites).WaitAsync(cancellationToken);
                if (UsesPythonEnvironment(kind))
                {
                    await _pythonEnvironment.WaitAsync(cancellationToken);
                    hasPythonEnvironment = true;
                }

                // Python waiters must not occupy slots needed by independent tools.
                await _installSlots.WaitAsync(cancellationToken);
                hasInstallSlot = true;
                await install(cancellationToken);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (hasInstallSlot) _installSlots.Release();
                if (hasPythonEnvironment) _pythonEnvironment.Release();
                lock (_gate)
                {
                    _operations.Remove(kind);
                    if (failure is OperationCanceledException canceled)
                        completion.SetCanceled(canceled.CancellationToken);
                    else if (failure is not null)
                        completion.SetException(failure);
                    else
                        completion.SetResult();
                }
            }
        }
    }

    private static bool UsesPythonEnvironment(SetupDependencyKind kind) => kind is
        SetupDependencyKind.Python312 or SetupDependencyKind.VapourSynth or
        SetupDependencyKind.Vsrepo or SetupDependencyKind.VsPluginBundle or
        SetupDependencyKind.Awsmfunc or SetupDependencyKind.Vsjetpack;

    private static IReadOnlyList<SetupDependencyKind> GetPrerequisites(SetupDependencyKind kind) => kind switch
    {
        SetupDependencyKind.VsPluginBundle =>
            [SetupDependencyKind.Python312, SetupDependencyKind.VapourSynth, SetupDependencyKind.Vsrepo],
        SetupDependencyKind.Awsmfunc or SetupDependencyKind.Vsjetpack =>
            [SetupDependencyKind.Python312, SetupDependencyKind.VapourSynth],
        SetupDependencyKind.VapourSynth or SetupDependencyKind.Vsrepo => [SetupDependencyKind.Python312],
        _ => []
    };
}
