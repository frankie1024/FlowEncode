using System.Text.Json;
using FlowEncode.Domain;

namespace FlowEncode.Application;

// Desktop UI and MCP runners enter this one scheduler. The transport never owns accepted work.
public sealed class TaskCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly Dictionary<string, Guid> _keys = new(StringComparer.Ordinal);
    private readonly IAppSettingsService _settings;
    private readonly ITaskHistoryStore? _history;
    private int _running;
    public event EventHandler? Changed;

    public TaskCoordinator(IAppSettingsService settings, ITaskHistoryStore? history = null)
    {
        _settings = settings;
        _history = history;
        foreach (var saved in history?.Load() ?? [])
        {
            var snapshot = saved.Snapshot;
            if (snapshot.State is EncodingJobState.Queued or EncodingJobState.Running)
                snapshot = snapshot with { State = EncodingJobState.Failed, FinishedAt = DateTimeOffset.UtcNow,
                    Summary = "The desktop closed before the task finished. Inspect retained output before retrying.", Error = "Desktop interrupted" };
            var entry = new Entry(saved.Workload, snapshot, saved.IdempotencyKey);
            _entries[snapshot.TaskId] = entry;
            if (saved.IdempotencyKey is not null) _keys[saved.IdempotencyKey] = snapshot.TaskId;
        }
    }

    public bool HasActiveTasks { get { lock (_gate) return _entries.Values.Any(e => IsActive(e.Snapshot.State)); } }
    public IReadOnlyList<TaskSnapshot> List() { lock (_gate) return _entries.Values.Select(e => e.Snapshot).OrderByDescending(s => s.CreatedAt).ToArray(); }
    public TaskSnapshot Get(Guid id) { lock (_gate) return Find(id).Snapshot; }
    public TaskWorkload GetRequest(Guid id) { lock (_gate) return Clone(Find(id).Workload); }

    public TaskSubmission? FindSubmission(string key, Guid? expectedPlanId = null)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(key, out var id)) return null;
            var entry = Find(id);
            if (expectedPlanId.HasValue && entry.Snapshot.PlanId != expectedPlanId)
                throw new InvalidOperationException("This idempotency key already belongs to a different plan.");
            return new(id, entry.Snapshot.PlanId, true);
        }
    }

    public TaskSubmission Reserve(TaskWorkload workload, TaskOrigin origin = TaskOrigin.Manual, Guid? planId = null, string? key = null, Guid? retryOfTaskId = null)
    {
        workload.Validate();
        TaskSubmission receipt;
        Entry? created = null;
        lock (_gate)
        {
            if (key is not null && FindSubmission(key, planId) is { } duplicate) return duplicate;
            if (_entries.TryGetValue(workload.TaskId, out var prior))
            {
                if (Serialize(prior.Workload) != Serialize(workload)) throw new InvalidOperationException("The task ID is already bound to another request.");
                return new(workload.TaskId, prior.Snapshot.PlanId, true);
            }
            EnsureOutputAvailable(workload);
            var snapshot = new TaskSnapshot(workload.TaskId, workload.Kind, origin, planId, EncodingJobState.Queued,
                DateTimeOffset.UtcNow, null, null, workload.SourcePath, workload.OutputPath, null, "Queued", "queued", "", [], RetryOfTaskId: retryOfTaskId);
            created = new(Clone(workload), snapshot, key);
            // Save the acceptance receipt before exposing it to a caller.
            _history?.Save(new(snapshot, created.Workload, key));
            _entries.Add(workload.TaskId, created);
            if (key is not null) _keys.Add(key, workload.TaskId);
            receipt = new(workload.TaskId, planId, false);
        }
        Notify();
        return receipt;
    }

    public async Task<T> RunAsync<T>(TaskWorkload workload, Func<IProgress<TaskUpdate>, CancellationToken, Task<T>> run,
        Func<T, TaskCompletion> complete, CancellationToken cancellationToken = default)
    {
        Reserve(workload);
        Entry entry;
        lock (_gate)
        {
            entry = Find(workload.TaskId);
            if (entry.Work is null && entry.Snapshot.State == EncodingJobState.Queued)
            {
                entry.Work = async () =>
                {
                    var result = await run(new InlineProgress(update => Update(entry, update)), entry.Cancellation.Token).ConfigureAwait(false);
                    return (result!, complete(result));
                };
            }
            else if (!IsActive(entry.Snapshot.State) && !entry.Completion.Task.IsCompleted)
                throw new InvalidOperationException("This task is already terminal. Retry using a new task ID.");
        }
        using var registration = cancellationToken.Register(() => Cancel(workload.TaskId));
        Pump();
        return (T)await entry.Completion.Task.ConfigureAwait(false);
    }

    public bool Cancel(Guid id)
    {
        Entry entry;
        lock (_gate)
        {
            entry = Find(id);
            if (!IsActive(entry.Snapshot.State)) return false;
            if (entry.Snapshot.State == EncodingJobState.Queued)
            {
                entry.Snapshot = entry.Snapshot with { State = EncodingJobState.Cancelled, FinishedAt = DateTimeOffset.UtcNow, Summary = "Cancelled before execution", Stage = "cancelled" };
                entry.Completion.TrySetCanceled();
            }
            else entry.Snapshot = entry.Snapshot with { Summary = "Cancelling; waiting for the process tree to exit" };
            Persist(entry);
        }
        entry.Cancellation.Cancel();
        Notify();
        Pump();
        return true;
    }

    public void RefreshConcurrency() => Pump();
    public void SetPreflight(Guid id, Func<CancellationToken, Task> preflight)
    {
        lock (_gate)
        {
            var entry = Find(id);
            if (entry.Work is not null || entry.Snapshot.State != EncodingJobState.Queued) throw new InvalidOperationException("This task has already started.");
            entry.Preflight = preflight;
        }
    }

    private void Pump()
    {
        List<Entry> start = [];
        lock (_gate)
        {
            var maximum = RequestValidation.NormalizeConcurrentEncodingJobs(_settings.Load().MaxConcurrentEncodingJobs);
            foreach (var entry in _entries.Values.OrderBy(e => e.Snapshot.CreatedAt))
            {
                if (_running >= maximum) break;
                if (entry.Snapshot.State != EncodingJobState.Queued || entry.Work is null) continue;
                // The existing module runners each support one foreground module operation.
                if (entry.Workload.Kind != TaskKind.Video && _entries.Values.Any(e => e.Snapshot.State == EncodingJobState.Running && e.Workload.Kind == entry.Workload.Kind)) continue;
                entry.Snapshot = entry.Snapshot with { State = EncodingJobState.Running, StartedAt = DateTimeOffset.UtcNow, Summary = "Starting", Stage = "starting" };
                _running++;
                Persist(entry);
                start.Add(entry);
            }
        }
        foreach (var entry in start) _ = Task.Run(() => ExecuteAsync(entry));
        if (start.Count > 0) Notify();
    }

    private async Task ExecuteAsync(Entry entry)
    {
        object? result = null;
        Exception? failure = null;
        TaskCompletion outcome;
        try
        {
            entry.Cancellation.Token.ThrowIfCancellationRequested();
            if (entry.Preflight is not null) await entry.Preflight(entry.Cancellation.Token).ConfigureAwait(false);
            (result, outcome) = await entry.Work!().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        { failure = new OperationCanceledException(entry.Cancellation.Token); outcome = new(EncodingJobState.Cancelled, "Cancelled", "", []); }
        catch (Exception ex) { failure = ex; outcome = new(EncodingJobState.Failed, ex.Message, ex.ToString(), []); }
        lock (_gate)
        {
            entry.Snapshot = entry.Snapshot with { State = outcome.State, FinishedAt = DateTimeOffset.UtcNow,
                Summary = outcome.Summary, Stage = outcome.State.ToString(), ProgressFraction = outcome.State == EncodingJobState.Completed ? 1 : entry.Snapshot.ProgressFraction,
                Log = Tail(outcome.Log), Artifacts = outcome.Artifacts.ToArray(), Error = outcome.State == EncodingJobState.Failed ? outcome.Summary : null };
            _running--;
            Persist(entry);
        }
        if (failure is OperationCanceledException) entry.Completion.TrySetCanceled(entry.Cancellation.Token);
        else if (failure is not null) entry.Completion.TrySetException(failure);
        else entry.Completion.TrySetResult(result!);
        Notify();
        Pump();
    }

    private void Update(Entry entry, TaskUpdate update)
    {
        lock (_gate)
        {
            if (entry.Snapshot.State != EncodingJobState.Running) return;
            entry.Snapshot = entry.Snapshot with { ProgressFraction = update.ProgressFraction, Summary = update.Summary,
                Stage = update.Stage, Log = string.IsNullOrWhiteSpace(update.Detail) ? entry.Snapshot.Log : Tail(entry.Snapshot.Log + update.Detail + Environment.NewLine) };
        }
        Notify();
    }

    private void EnsureOutputAvailable(TaskWorkload request)
    {
        var output = Canonical(request.OutputPath);
        foreach (var entry in _entries.Values.Where(e => IsActive(e.Snapshot.State)))
        {
            var other = Canonical(entry.Workload.OutputPath);
            if (string.Equals(output, other, StringComparison.OrdinalIgnoreCase)
                || request.OutputIsDirectory && IsChild(other, output) || entry.Workload.OutputIsDirectory && IsChild(output, other))
                throw new InvalidOperationException("Another task already owns this output: " + entry.Snapshot.TaskId);
        }
        if (request.ProducesMedia && !request.OverwriteOutput && (File.Exists(output) || request.OutputIsDirectory && Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()))
            throw new IOException("The output already exists. Choose another output or explicitly enable overwrite.");
    }

    private static bool IsChild(string path, string directory) => path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool IsActive(EncodingJobState state) => state is EncodingJobState.Queued or EncodingJobState.Running;
    private Entry Find(Guid id) => _entries.GetValueOrDefault(id) ?? throw new KeyNotFoundException("Unknown task ID: " + id);
    private void Persist(Entry e) { try { _history?.Save(new(e.Snapshot, e.Workload, e.Key)); } catch { /* Accepted work still finishes; the next state save retries. */ } }
    private void Notify() { try { Changed?.Invoke(this, EventArgs.Empty); } catch { /* A UI observer cannot break scheduling. */ } }
    private static string Tail(string text) => text.Length <= 65536 ? text : text[^65536..];
    private static string Serialize(TaskWorkload value) => JsonSerializer.Serialize(value);
    private static TaskWorkload Clone(TaskWorkload value) => JsonSerializer.Deserialize<TaskWorkload>(Serialize(value))!;

    private sealed class Entry(TaskWorkload workload, TaskSnapshot snapshot, string? key)
    {
        public TaskWorkload Workload { get; } = workload;
        public TaskSnapshot Snapshot { get; set; } = snapshot;
        public string? Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Task<(object Result, TaskCompletion Completion)>>? Work { get; set; }
        public Func<CancellationToken, Task>? Preflight { get; set; }
    }
    private sealed class InlineProgress(Action<TaskUpdate> update) : IProgress<TaskUpdate> { public void Report(TaskUpdate value) => update(value); }
}
