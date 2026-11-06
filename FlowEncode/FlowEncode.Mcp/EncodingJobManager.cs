using System.Collections.Concurrent;
using FlowEncode.Application;
using FlowEncode.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowEncode.Mcp;

/// <summary>
/// Runs encoding jobs in the background so MCP tool calls return immediately, and keeps their
/// progress around for polling. Concurrency follows the MaxConcurrentEncodingJobs app setting.
/// </summary>
public sealed class EncodingJobManager : IHostedService, IDisposable
{
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly IEncodingJobRunner _runner;
    private readonly ILogger<EncodingJobManager> _logger;
    private readonly ConcurrentDictionary<Guid, TrackedEncodingJob> _jobs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _slots;

    public EncodingJobManager(
        IEncodingJobRunner runner,
        IAppSettingsService settingsService,
        ILogger<EncodingJobManager> logger)
    {
        _runner = runner;
        _logger = logger;
        var concurrency = RequestValidation.NormalizeConcurrentEncodingJobs(settingsService.Load().MaxConcurrentEncodingJobs);
        _slots = new SemaphoreSlim(concurrency, concurrency);
    }

    public TrackedEncodingJob Enqueue(EncodingJobRequest request)
    {
        RequestValidation.ValidateEncodingJobRequest(request);
        _shutdown.Token.ThrowIfCancellationRequested();

        var job = new TrackedEncodingJob(request, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        if (!_jobs.TryAdd(request.JobId, job))
        {
            job.Dispose();
            throw new InvalidOperationException($"A job with id {request.JobId} already exists.");
        }

        job.Completion = Task.Run(() => RunAsync(job));
        return job;
    }

    public TrackedEncodingJob? Find(Guid jobId) => _jobs.TryGetValue(jobId, out var job) ? job : null;

    public IReadOnlyList<TrackedEncodingJob> List() =>
        _jobs.Values.OrderBy(static job => job.CreatedAt).ToList();

    public bool Cancel(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job) || job.IsFinished)
        {
            return false;
        }

        job.RequestCancellation();
        _runner.AbortJob(jobId);
        return true;
    }

    private async Task RunAsync(TrackedEncodingJob job)
    {
        var token = job.CancellationToken;
        var slotAcquired = false;

        try
        {
            await _slots.WaitAsync(token);
            slotAcquired = true;
            job.MarkRunning();

            var result = await _runner.RunAsync(job.Request, new ImmediateProgress(job.ApplyProgress), token);
            job.Complete(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            job.MarkCancelled();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Encoding job {JobId} failed.", job.Request.JobId);
            job.MarkFailed(ex.Message);
        }
        finally
        {
            if (slotAcquired)
            {
                _slots.Release();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The MCP client went away: stop every encoder instead of leaving orphaned processes.
        // Snapshot first: cancelling can finish jobs synchronously before they would be aborted.
        var activeJobs = _jobs.Values.Where(static job => !job.IsFinished).ToList();
        _shutdown.Cancel();
        foreach (var job in activeJobs)
        {
            _runner.AbortJob(job.Request.JobId);
        }

        var pending = activeJobs
            .Select(static job => job.Completion)
            .Where(static completion => !completion.IsCompleted)
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(ShutdownDrainTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("Timed out waiting for {Count} encoding job(s) to stop.", pending.Length);
        }
    }

    public void Dispose()
    {
        foreach (var job in _jobs.Values)
        {
            job.Dispose();
        }

        _shutdown.Dispose();
        _slots.Dispose();
    }

    /// <summary>
    /// Progress&lt;T&gt; posts to the thread pool and can reorder updates; job state needs them in order.
    /// </summary>
    private sealed class ImmediateProgress(Action<EncodingJobProgress> onReport) : IProgress<EncodingJobProgress>
    {
        public void Report(EncodingJobProgress value) => onReport(value);
    }
}

public sealed class TrackedEncodingJob : IDisposable
{
    private const int MaxRetainedLogLines = 200;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly Queue<string> _recentLogLines = new();
    private EncodingJobState _state = EncodingJobState.Queued;
    private double? _progressFraction;
    private string _summary = "Queued";
    private string _detailLine = string.Empty;
    private EncodingProgressSnapshot? _snapshot;
    private bool _isSourcePreparation;
    private EncodingJobResult? _result;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;

    public TrackedEncodingJob(EncodingJobRequest request, CancellationTokenSource cancellation)
    {
        Request = request;
        _cancellation = cancellation;
        CreatedAt = DateTimeOffset.Now;
    }

    public EncodingJobRequest Request { get; }

    public DateTimeOffset CreatedAt { get; }

    public Task Completion { get; set; } = Task.CompletedTask;

    public CancellationToken CancellationToken => _cancellation.Token;

    public bool IsFinished
    {
        get
        {
            lock (_gate)
            {
                return _state is EncodingJobState.Completed or EncodingJobState.Failed or EncodingJobState.Cancelled;
            }
        }
    }

    public void RequestCancellation()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void MarkRunning()
    {
        lock (_gate)
        {
            _state = EncodingJobState.Running;
            _summary = "Encoder starting";
            _startedAt = DateTimeOffset.Now;
        }
    }

    public void ApplyProgress(EncodingJobProgress progress)
    {
        lock (_gate)
        {
            if (_result is not null)
            {
                return;
            }

            _state = progress.State;
            _isSourcePreparation = progress.IsSourcePreparation;
            _summary = progress.Summary;
            _detailLine = progress.DetailLine;
            if (!progress.IsSourcePreparation)
            {
                _progressFraction = progress.ProgressFraction ?? _progressFraction;
                _snapshot = MergeSnapshot(_snapshot, progress.Snapshot);
            }

            if (progress.ShouldShowInLog && !string.IsNullOrWhiteSpace(progress.DetailLine))
            {
                AppendLogLine(progress.DetailLine);
            }
        }
    }

    public void Complete(EncodingJobResult result)
    {
        lock (_gate)
        {
            _result = result;
            _state = result.State;
            _summary = result.Summary;
            if (result.State == EncodingJobState.Completed)
            {
                _progressFraction = 1.0;
            }

            _finishedAt = DateTimeOffset.Now;
        }
    }

    public void MarkCancelled() => Finish(EncodingJobState.Cancelled, "Encoding cancelled");

    public void MarkFailed(string message) => Finish(EncodingJobState.Failed, $"Encoding failed: {message}");

    public EncodingJobStatus GetStatus(int logLineCount)
    {
        lock (_gate)
        {
            var take = Math.Clamp(logLineCount, 0, MaxRetainedLogLines);
            return new EncodingJobStatus(
                Request.JobId,
                _state.ToString(),
                _progressFraction is { } fraction ? Math.Round(fraction * 100, 2) : null,
                _summary,
                _detailLine,
                _isSourcePreparation,
                _snapshot?.CurrentFrame,
                _snapshot?.TotalFrames,
                _snapshot?.FramesPerSecond,
                _snapshot?.BitrateKbps,
                _snapshot?.Eta?.TotalSeconds is { } eta ? Math.Round(eta) : null,
                _snapshot?.EstimatedFileSizeBytes,
                Request.Profile.Kind.ToDisplayName(),
                Request.SourcePath,
                Request.OutputPath,
                CreatedAt,
                _startedAt,
                _finishedAt,
                _result?.ExitCode,
                string.IsNullOrWhiteSpace(_result?.LogFilePath) ? null : _result.LogFilePath,
                _recentLogLines.Skip(Math.Max(0, _recentLogLines.Count - take)).ToList());
        }
    }

    public void Dispose() => _cancellation.Dispose();

    private void Finish(EncodingJobState state, string summary)
    {
        lock (_gate)
        {
            if (_result is not null)
            {
                return;
            }

            _state = state;
            _summary = summary;
            _finishedAt = DateTimeOffset.Now;
            AppendLogLine(summary);
        }
    }

    private void AppendLogLine(string line)
    {
        _recentLogLines.Enqueue(line.TrimEnd());
        while (_recentLogLines.Count > MaxRetainedLogLines)
        {
            _recentLogLines.Dequeue();
        }
    }

    private static EncodingProgressSnapshot? MergeSnapshot(EncodingProgressSnapshot? current, EncodingProgressSnapshot? update)
    {
        if (update is null)
        {
            return current;
        }

        if (current is null)
        {
            return update;
        }

        return new EncodingProgressSnapshot(
            update.CurrentFrame ?? current.CurrentFrame,
            update.TotalFrames ?? current.TotalFrames,
            update.FramesPerSecond ?? current.FramesPerSecond,
            update.BitrateKbps ?? current.BitrateKbps,
            update.Eta ?? current.Eta,
            update.EstimatedFileSizeBytes ?? current.EstimatedFileSizeBytes);
    }
}

public sealed record EncodingJobStatus(
    Guid JobId,
    string State,
    double? ProgressPercent,
    string Summary,
    string DetailLine,
    bool IsPreparingSource,
    long? CurrentFrame,
    long? TotalFrames,
    double? FramesPerSecond,
    double? BitrateKbps,
    double? EtaSeconds,
    long? EstimatedFileSizeBytes,
    string Encoder,
    string SourcePath,
    string OutputPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? ExitCode,
    string? LogFilePath,
    IReadOnlyList<string> RecentLogLines);
