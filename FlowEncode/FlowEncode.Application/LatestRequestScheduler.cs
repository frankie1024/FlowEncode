using System.Collections.Generic;

namespace FlowEncode.Application;

public sealed class LatestRequestScheduler<TRequest> : IDisposable
    where TRequest : notnull
{
    private readonly object _gate = new();
    private readonly Func<ScheduledRequest, Task> _executeAsync;
    private readonly IEqualityComparer<TRequest> _requestComparer;
    private QueuedRequest? _latestRequest;
    private QueuedRequest? _pendingRequest;
    private bool _isDisposed;
    private bool _isProcessing;
    private long _nextRequestId;

    public LatestRequestScheduler(
        Func<ScheduledRequest, Task> executeAsync,
        IEqualityComparer<TRequest>? requestComparer = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _requestComparer = requestComparer ?? EqualityComparer<TRequest>.Default;
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _isProcessing;
            }
        }
    }

    public Task ScheduleAsync(TRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ScheduledRequest? scheduledRequest = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            var queuedRequest = new QueuedRequest(++_nextRequestId, request);
            _latestRequest = queuedRequest;
            _pendingRequest = queuedRequest;

            if (_isProcessing)
            {
                return Task.CompletedTask;
            }

            _isProcessing = true;
            scheduledRequest = DequeueScheduledRequestUnsafe();
        }

        return ProcessLoopAsync(scheduledRequest);
    }

    public void ClearPending()
    {
        lock (_gate)
        {
            _pendingRequest = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _isDisposed = true;
            _pendingRequest = null;
            _latestRequest = null;
        }
    }

    private async Task ProcessLoopAsync(ScheduledRequest scheduledRequest)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? capturedFailure = null;

        while (true)
        {
            try
            {
                await _executeAsync(scheduledRequest);
            }
            catch (Exception ex)
            {
                capturedFailure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }

            var shouldStopProcessing = false;
            lock (_gate)
            {
                if (_isDisposed)
                {
                    _isProcessing = false;
                    shouldStopProcessing = true;
                }
                else if (_pendingRequest is null)
                {
                    _isProcessing = false;
                    shouldStopProcessing = true;
                }
                else if (capturedFailure is null
                    && _requestComparer.Equals(_pendingRequest.Request, scheduledRequest.Request))
                {
                    _pendingRequest = null;
                    _isProcessing = false;
                    shouldStopProcessing = true;
                }
                else
                {
                    scheduledRequest = DequeueScheduledRequestUnsafe();
                }
            }

            if (!shouldStopProcessing)
            {
                continue;
            }

            capturedFailure?.Throw();
            return;
        }
    }

    private ScheduledRequest DequeueScheduledRequestUnsafe()
    {
        var queuedRequest = _pendingRequest ?? throw new InvalidOperationException("No request is available for execution.");
        _pendingRequest = null;
        return new ScheduledRequest(this, queuedRequest.RequestId, queuedRequest.Request);
    }

    private bool IsLatestRequest(long requestId)
    {
        lock (_gate)
        {
            return !_isDisposed
                && _latestRequest is { RequestId: var latestRequestId }
                && latestRequestId == requestId;
        }
    }

    private bool MatchesLatestRequest(TRequest request)
    {
        lock (_gate)
        {
            return !_isDisposed
                && _latestRequest is { Request: var latestRequest }
                && _requestComparer.Equals(latestRequest, request);
        }
    }

    private sealed record QueuedRequest(
        long RequestId,
        TRequest Request);

    public sealed class ScheduledRequest
    {
        private readonly LatestRequestScheduler<TRequest> _owner;

        internal ScheduledRequest(
            LatestRequestScheduler<TRequest> owner,
            long requestId,
            TRequest request)
        {
            _owner = owner;
            RequestId = requestId;
            Request = request;
        }

        public long RequestId { get; }

        public TRequest Request { get; }

        public bool IsLatest => _owner.IsLatestRequest(RequestId);

        public bool MatchesLatestRequest => _owner.MatchesLatestRequest(Request);
    }
}
