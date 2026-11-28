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
    private bool _idleActionRunning;
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

    public async Task<bool> TryRunWhenIdleAsync(Func<Task> action)
    {
        lock (_gate)
        {
            if (_idleActionRunning || _entries.Values.Any(entry => IsActive(entry.Snapshot.State)))
                return false;
            _idleActionRunning = true;
        }

        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        finally
        {
            lock (_gate) _idleActionRunning = false;
        }
    }

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

    public TaskSubmission Reserve(TaskWorkload workload, TaskOrigin origin = TaskOrigin.Manual, Guid? planId = null, string? key = null,
        Guid? retryOfTaskId = null, Func<CancellationToken, Task>? preflight = null)
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
            if (_idleActionRunning)
                throw new InvalidOperationException("The queue completion action is in progress. Try again after it finishes.");
            EnsureOutputAvailable(workload);
            var snapshot = new TaskSnapshot(workload.TaskId, workload.Kind, origin, planId, EncodingJobState.Queued,
                DateTimeOffset.UtcNow, null, null, workload.SourcePath, workload.OutputPath, null, "Queued", "queued", "", [], RetryOfTaskId: retryOfTaskId);
            created = new(Clone(workload), snapshot, key) { Preflight = preflight };
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
                entry.Preflight = null;
                entry.Work = null;
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
            ValidateExecutionInput(entry.Workload);
            if (entry.Preflight is not null) await entry.Preflight(entry.Cancellation.Token).ConfigureAwait(false);
            (result, outcome) = await entry.Work!().ConfigureAwait(false);
            if (IsActive(outcome.State))
                throw new InvalidOperationException("The task runner returned without a terminal result. / 任务执行器结束时未返回终态。");
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        { failure = new OperationCanceledException(entry.Cancellation.Token); outcome = new(EncodingJobState.Cancelled, "Cancelled", "", []); }
        catch (Exception ex) { failure = ex; outcome = new(EncodingJobState.Failed, ex.Message, ex.ToString(), []); }
        lock (_gate)
        {
            entry.Snapshot = entry.Snapshot with { State = outcome.State, FinishedAt = DateTimeOffset.UtcNow,
                Summary = outcome.Summary, Stage = outcome.State.ToString(), ProgressFraction = outcome.State == EncodingJobState.Completed ? 1 : entry.Snapshot.ProgressFraction,
                Log = Tail(outcome.Log), Artifacts = outcome.Artifacts.ToArray(), Error = outcome.State == EncodingJobState.Failed ? outcome.Summary : null,
                AudioTelemetry = outcome.AudioTelemetry };
            entry.Preflight = null;
            entry.Work = null;
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
            var progressFraction = update.ProgressFraction.HasValue
                ? Math.Max(entry.Snapshot.ProgressFraction ?? 0, update.ProgressFraction.Value)
                : entry.Snapshot.ProgressFraction;
            entry.Snapshot = entry.Snapshot with { ProgressFraction = progressFraction, Summary = update.Summary,
                Stage = update.Stage, Log = string.IsNullOrWhiteSpace(update.Detail) ? entry.Snapshot.Log : Tail(entry.Snapshot.Log + update.Detail + Environment.NewLine),
                AudioTelemetry = update.AudioTelemetry };
        }
        Notify();
    }

    private void EnsureOutputAvailable(TaskWorkload request)
    {
        var output = Canonical(request.OutputPath);
        var requestedClaim = OutputClaim.From(request);
        foreach (var entry in _entries.Values.Where(e => IsActive(e.Snapshot.State)))
        {
            if (requestedClaim.Overlaps(OutputClaim.From(entry.Workload)))
                throw new InvalidOperationException("Another task already owns this output: " + entry.Snapshot.TaskId);
        }
        if (request.OutputIsDirectory ? File.Exists(output) : Directory.Exists(output))
            throw new IOException("The output path is occupied. Choose another output path.");
        if (request.ProducesMedia && !request.OverwriteOutput && !request.OutputIsDirectory && File.Exists(output))
            throw new IOException("The output already exists. Choose another output or explicitly enable overwrite.");
    }

    private sealed class OutputClaim
    {
        private static readonly OutputClaim EmptyClaim = new(null, null, null);
        private static readonly HashSet<string> AllDeewExtensions = new(
            DeewOutputNaming.OutputExtensions.Select(static ext => "." + ext),
            StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string>? _files;
        private readonly string? _directory;
        private readonly HashSet<string>? _directoryExtensions;

        private OutputClaim(HashSet<string>? files, string? directory, HashSet<string>? directoryExtensions)
        {
            _files = files;
            _directory = directory;
            _directoryExtensions = directoryExtensions;
        }

        private bool IsEmpty => _files is null && _directory is null;

        public static OutputClaim From(TaskWorkload workload)
        {
            if (workload.Kind == TaskKind.Audio && workload.Audio!.Mode == AudioProcessingMode.Ddp)
            {
                if (!workload.ProducesMedia)
                    return EmptyClaim;

                return new OutputClaim(
                    files: null,
                    directory: Canonical(workload.OutputPath),
                    directoryExtensions: GetDeewExtensions(workload.Audio.Deew));
            }

            if (workload.Kind == TaskKind.BluRay)
            {
                var bluray = workload.BluRay!;
                var outputPaths = bluray.Backend == BluRayDemuxBackend.DgDemux
                    ? BluRayDemuxSourceSupport.ResolveDgDemuxOutputPatterns(bluray)
                    : bluray.Selections.Where(selection => !string.IsNullOrWhiteSpace(selection.OutputPath))
                        .Select(selection => Path.Combine(bluray.OutputDirectory, Path.GetFileName(selection.OutputPath)));
                var files = outputPaths.Select(Canonical).ToHashSet(StringComparer.OrdinalIgnoreCase);

                return files.Count > 0
                    ? new OutputClaim(files, directory: null, directoryExtensions: null)
                    : new OutputClaim(files: null, directory: Canonical(workload.OutputPath), directoryExtensions: null);
            }

            return new OutputClaim(
                files: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Canonical(workload.OutputPath) },
                directory: null,
                directoryExtensions: null);
        }

        public bool Overlaps(OutputClaim other)
        {
            if (IsEmpty || other.IsEmpty)
                return false;

            if (_files is not null && other._files is not null)
                return FilesOverlapFiles(_files, other._files);

            if (_files is not null && other._directory is not null)
                return FilesOverlapDirectory(_files, other._directory, other._directoryExtensions);

            if (_directory is not null && other._files is not null)
                return FilesOverlapDirectory(other._files, _directory, _directoryExtensions);

            if (_directory is not null && other._directory is not null)
            {
                var dirsOverlap = string.Equals(_directory, other._directory, StringComparison.OrdinalIgnoreCase)
                    || IsChild(_directory, other._directory)
                    || IsChild(other._directory, _directory);
                if (!dirsOverlap)
                    return false;

                if (_directoryExtensions is null || other._directoryExtensions is null)
                    return true;

                return _directoryExtensions.Overlaps(other._directoryExtensions);
            }

            return false;
        }

        private static bool FilesOverlapFiles(HashSet<string> left, HashSet<string> right)
        {
            if (left.Overlaps(right))
                return true;

            foreach (var a in left)
            {
                var aWildcard = ContainsWildcard(a);
                foreach (var b in right)
                {
                    var bWildcard = ContainsWildcard(b);
                    if (!aWildcard && !bWildcard)
                        continue;

                    var dirA = Path.GetDirectoryName(a);
                    var dirB = Path.GetDirectoryName(b);
                    if (!string.Equals(dirA, dirB, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var nameA = Path.GetFileName(a);
                    var nameB = Path.GetFileName(b);
                    if (aWildcard && !bWildcard)
                    {
                        if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(nameA, nameB, ignoreCase: true))
                            return true;
                    }
                    else if (!aWildcard && bWildcard)
                    {
                        if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(nameB, nameA, ignoreCase: true))
                            return true;
                    }
                    else
                    {
                        var prefixA = nameA.Split('*', '?')[0];
                        var prefixB = nameB.Split('*', '?')[0];
                        if (prefixA.StartsWith(prefixB, StringComparison.OrdinalIgnoreCase)
                            || prefixB.StartsWith(prefixA, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }

            return false;
        }

        private static bool ContainsWildcard(string path) => path.IndexOfAny(['*', '?']) >= 0;

        private static bool FilesOverlapDirectory(HashSet<string> files, string directory, HashSet<string>? allowedExtensions)
        {
            foreach (var file in files)
            {
                if (!string.Equals(file, directory, StringComparison.OrdinalIgnoreCase) && !IsChild(file, directory))
                    continue;

                if (allowedExtensions is null)
                    return true;

                var ext = Path.GetExtension(file);
                if (!string.IsNullOrEmpty(ext) && (ContainsWildcard(ext) || allowedExtensions.Contains(ext)))
                    return true;
            }

            return false;
        }

        private static HashSet<string> GetDeewExtensions(DeewOptions? rawOptions)
        {
            var format = (rawOptions?.Format ?? "ddp").Trim().ToLowerInvariant();
            return format switch
            {
                "dd" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ac3" },
                "ddp" or "ddp-atmos" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ec3", ".eb3" },
                "thd" or "thd-atmos" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".thd" },
                "ac4" or "ac4-atmos" or "ac4-ims" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ac4" },
                "aac" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".aac" },
                _ => AllDeewExtensions
            };
        }
    }

    private static void ValidateExecutionInput(TaskWorkload request)
    {
        if (request.Kind == TaskKind.BluRay)
        {
            BluRayDemuxSourceSupport.ValidateExecutionSource(request.BluRay!);
        }
        else if (!File.Exists(request.SourcePath))
            throw new FileNotFoundException($"Source file not found / 未找到输入文件：{request.SourcePath}", request.SourcePath);
    }

    private static bool IsChild(string path, string directory)
    {
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
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
