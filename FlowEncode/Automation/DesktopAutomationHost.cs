using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Infrastructure.Automation;
using FlowEncode.ViewModels;

namespace FlowEncode.AutomationBridge;

/// <summary>
/// Serves the local automation pipe for the MCP server. Each request is executed on the UI thread
/// against the same view models the window uses, so automation and the user share one queue,
/// one concurrency limit and one copy of every setting.
/// </summary>
internal sealed class DesktopAutomationHost : IAsyncDisposable
{
    private const int MaxRetainedOperations = 50;

    private readonly MainWindow _window;
    private readonly IAppDiagnostics _diagnostics;
    private readonly string _version;
    private readonly ConcurrentDictionary<Guid, OperationEntry> _operations = new();
    private DesktopAutomationServer? _server;

    public DesktopAutomationHost(MainWindow window, IAppDiagnostics diagnostics)
    {
        _window = window;
        _diagnostics = diagnostics;
        _version = typeof(DesktopAutomationHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private MainWindowViewModel ViewModel => _window.ViewModel;

    public void Start()
    {
        if (_server is not null)
        {
            return;
        }

        _server = new DesktopAutomationServer(
            DesktopAutomationProtocol.GetPipeName(),
            HandleAsync,
            (message, exception) => _diagnostics.Write(
                nameof(DesktopAutomationHost),
                exception is null ? message : $"{message} {exception.GetType().Name}: {exception.Message}",
                AppDiagnosticSeverity.Warning));
        _server.Start();
    }

    public async ValueTask DisposeAsync()
    {
        var server = _server;
        _server = null;
        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }

    private Task<object?> HandleAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (method == DesktopAutomationMethods.AppStatus)
        {
            return OnUi(() => (object?)ViewModel.GetAutomationAppStatus(_version, _window.IsAutomationReady));
        }

        return OnUiAsync(async () =>
        {
            if (!_window.IsAutomationReady)
            {
                throw new DesktopAutomationException("FlowEncode is still starting up. Try again in a moment.");
            }

            return await DispatchAsync(method, parameters);
        });
    }

    private async Task<object?> DispatchAsync(string method, JsonElement? parameters)
    {
        switch (method)
        {
            case DesktopAutomationMethods.QueueList:
                return ViewModel.GetAutomationJobs();
            case DesktopAutomationMethods.QueueGet:
            {
                var request = Read<GetJobParams>(parameters);
                return ViewModel.GetAutomationJob(request.JobId, request.LogLines);
            }
            case DesktopAutomationMethods.QueueEnqueue:
            {
                var request = Read<EnqueueEncodingJobParams>(parameters);
                return ViewModel.EnqueueAutomationJob(request.Request, request.StartImmediately);
            }
            case DesktopAutomationMethods.QueueStart:
                return ViewModel.StartAutomationJobNow(Read<JobIdParams>(parameters).JobId);
            case DesktopAutomationMethods.QueueCancel:
                return await ViewModel.CancelAutomationJobAsync(Read<JobIdParams>(parameters).JobId);
            case DesktopAutomationMethods.QueueRestart:
                return await ViewModel.RestartAutomationJobAsync(Read<JobIdParams>(parameters).JobId);
            case DesktopAutomationMethods.QueueRemove:
                return ViewModel.RemoveAutomationJob(Read<JobIdParams>(parameters).JobId);
            case DesktopAutomationMethods.QueueMove:
            {
                var request = Read<MoveJobParams>(parameters);
                return ViewModel.MoveAutomationJob(request.JobId, request.Position);
            }

            case DesktopAutomationMethods.ModuleStatus:
            {
                var request = Read<ModuleParams>(parameters);
                return ViewModel.GetAutomationModuleStatus(request.Module, request.LogLines);
            }
            case DesktopAutomationMethods.ModuleCancel:
                return ViewModel.CancelAutomationModule(Read<ModuleParams>(parameters).Module);
            case DesktopAutomationMethods.AudioStart:
                return ViewModel.StartAutomationAudio(Read<StartAudioParams>(parameters).Request);
            case DesktopAutomationMethods.BluRayStart:
                return ViewModel.StartAutomationBluRay(Read<StartBluRayParams>(parameters).Request);
            case DesktopAutomationMethods.AutoCompressionStart:
                return ViewModel.StartAutomationAutoCompression(Read<StartAutoCompressionParams>(parameters).Request);

            case DesktopAutomationMethods.SettingsGet:
                return ViewModel.GetAutomationSettings();
            case DesktopAutomationMethods.SettingsUpdate:
                return await _window.UpdateSettingsForAutomationAsync(Read<AutomationSettingsPatch>(parameters));

            case DesktopAutomationMethods.TemplatesReload:
                await ViewModel.ReloadTemplatesFromDiskAsync();
                return null;

            case DesktopAutomationMethods.DependenciesList:
            {
                var setupGuide = ViewModel.SetupGuideModule;
                if (Read<CheckUpdatesParams>(parameters).CheckForUpdates)
                {
                    await setupGuide.CheckSetupDependencyUpdatesAsync();
                }
                else if (!setupGuide.HasAutomationStatusReport)
                {
                    await setupGuide.RefreshSetupGuideAsync();
                }

                return setupGuide.GetAutomationDependencyStatuses();
            }
            case DesktopAutomationMethods.DependencyOperation:
            {
                var request = Read<DependencyOperationParams>(parameters);
                var setupGuide = ViewModel.SetupGuideModule;
                var title = setupGuide.GetSetupDependencyDisplayName(request.Kind);
                return StartOperation(
                    $"dependency.{request.Operation}",
                    $"{request.Operation} {title}",
                    async () =>
                    {
                        ThrowIfError(await setupGuide.RunAutomationDependencyOperationAsync(request.Kind, request.Operation, request.SourcePath));
                        return $"{title}: {request.Operation} completed. Current path: {setupGuide.GetSetupDependencyCurrentPath(request.Kind)}";
                    });
            }
            case DesktopAutomationMethods.EnvironmentRefresh:
                return StartOperation("environment.refresh", "Re-detect tools", async () =>
                {
                    await ViewModel.SetupGuideModule.RefreshSetupGuideAsync();
                    return "Tool detection refreshed.";
                });

            case DesktopAutomationMethods.AppUpdateCheck:
                return await _window.CheckAppUpdateForAutomationAsync();
            case DesktopAutomationMethods.AppUpdateInstall:
                return StartOperation("app.update", "Download and install the latest FlowEncode", _window.InstallAppUpdateForAutomationAsync);

            case DesktopAutomationMethods.OperationGet:
            {
                var id = Read<OperationIdParams>(parameters).OperationId;
                return _operations.TryGetValue(id, out var entry)
                    ? entry.Snapshot()
                    : throw new DesktopAutomationException($"No operation with id {id}.");
            }
            case DesktopAutomationMethods.OperationList:
                return _operations.Values
                    .Select(static entry => entry.Snapshot())
                    .OrderByDescending(static snapshot => snapshot.StartedAt)
                    .ToList();

            default:
                throw new DesktopAutomationException($"Unknown automation method '{method}'.");
        }
    }

    /// <summary>
    /// Starts long work on the UI thread and returns immediately; the client polls operation.get.
    /// </summary>
    private AutomationOperationSnapshot StartOperation(string kind, string description, Func<Task<string>> work)
    {
        var entry = new OperationEntry(Guid.NewGuid(), kind, description);
        _operations[entry.Id] = entry;
        PruneOperations();

        _ = RunOperationAsync(entry, work);
        return entry.Snapshot();
    }

    private async Task RunOperationAsync(OperationEntry entry, Func<Task<string>> work)
    {
        try
        {
            entry.Succeed(await work());
        }
        catch (Exception ex)
        {
            if (ex is not DesktopAutomationException)
            {
                _diagnostics.Write(
                    nameof(DesktopAutomationHost),
                    $"Automation operation '{entry.Kind}' failed. {ex.GetType().Name}: {ex.Message}",
                    AppDiagnosticSeverity.Warning);
            }

            entry.Fail(ex.Message);
        }
    }

    private void PruneOperations()
    {
        var finished = _operations.Values
            .Where(static entry => entry.IsFinished)
            .OrderBy(static entry => entry.StartedAt)
            .ToList();
        for (var index = 0; index < finished.Count && _operations.Count > MaxRetainedOperations; index++)
        {
            _operations.TryRemove(finished[index].Id, out _);
        }
    }

    private Task<T> OnUi<T>(Func<T> action)
    {
        return OnUiAsync(() => Task.FromResult(action()));
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                completion.TrySetResult(await action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!queued)
        {
            completion.TrySetException(new DesktopAutomationException("The FlowEncode window is closing."));
        }

        return completion.Task;
    }

    private static T Read<T>(JsonElement? parameters)
    {
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new DesktopAutomationException($"Missing parameters ({typeof(T).Name}).");
        }

        try
        {
            return parameters.Value.Deserialize<T>(DesktopAutomationProtocol.JsonOptions)
                ?? throw new DesktopAutomationException($"Missing parameters ({typeof(T).Name}).");
        }
        catch (JsonException ex)
        {
            throw new DesktopAutomationException($"Invalid parameters ({typeof(T).Name}): {ex.Message}");
        }
    }

    private static void ThrowIfError(string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new DesktopAutomationException(error);
        }
    }

    private sealed class OperationEntry
    {
        private readonly object _gate = new();
        private AutomationOperationState _state = AutomationOperationState.Running;
        private string? _result;
        private string? _error;
        private DateTimeOffset? _finishedAt;

        public OperationEntry(Guid id, string kind, string description)
        {
            Id = id;
            Kind = kind;
            Description = description;
            StartedAt = DateTimeOffset.Now;
        }

        public Guid Id { get; }

        public string Kind { get; }

        public string Description { get; }

        public DateTimeOffset StartedAt { get; }

        public bool IsFinished
        {
            get
            {
                lock (_gate)
                {
                    return _state != AutomationOperationState.Running;
                }
            }
        }

        public void Succeed(string result)
        {
            lock (_gate)
            {
                _state = AutomationOperationState.Succeeded;
                _result = result;
                _finishedAt = DateTimeOffset.Now;
            }
        }

        public void Fail(string error)
        {
            lock (_gate)
            {
                _state = AutomationOperationState.Failed;
                _error = error;
                _finishedAt = DateTimeOffset.Now;
            }
        }

        public AutomationOperationSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new AutomationOperationSnapshot(Id, Kind, Description, _state, _result, _error, StartedAt, _finishedAt);
            }
        }
    }
}
