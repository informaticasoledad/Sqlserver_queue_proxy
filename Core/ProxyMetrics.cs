using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;

namespace TDSQueue.Proxy;

public sealed class ProxyMetrics : IDisposable
{
    private readonly Meter _meter = new("TDSQueue.Proxy", "2.0.0");
    private readonly Counter<long> _requestsEnqueued;
    private readonly Counter<long> _requestsCompleted;
    private readonly Counter<long> _requestsFailed;
    private readonly Counter<long> _workerErrors;
    private readonly Counter<long> _queuePublishErrors;
    private readonly Counter<long> _queueConsumeErrors;
    private readonly Counter<long> _targetConnectRetries;
    private readonly Histogram<double> _requestLatencyMs;
    private readonly Histogram<double> _stageLatencyMs;
    private readonly ConcurrentDictionary<int, long> _workerErrorCounts = new();

    private readonly object _latencySync = new();
    private double _targetLatencyEwmaMs;

    private long _queueDepth;
    private long _activeSessions;
    private long _pendingRequests;
    private long _inflightRequests;
    private long _totalEnqueued;
    private long _totalCompleted;
    private long _totalFailed;
    private long _totalQueuePublishErrors;
    private long _totalQueueConsumeErrors;

    public ProxyMetrics()
    {
        _requestsEnqueued = _meter.CreateCounter<long>("proxy.requests.enqueued");
        _requestsCompleted = _meter.CreateCounter<long>("proxy.requests.completed");
        _requestsFailed = _meter.CreateCounter<long>("proxy.requests.failed");
        _workerErrors = _meter.CreateCounter<long>("proxy.worker.errors");
        _queuePublishErrors = _meter.CreateCounter<long>("proxy.queue.publish.errors");
        _queueConsumeErrors = _meter.CreateCounter<long>("proxy.queue.consume.errors");
        _targetConnectRetries = _meter.CreateCounter<long>("proxy.target.connect.retries");
        _requestLatencyMs = _meter.CreateHistogram<double>("proxy.request.latency.ms");
        _stageLatencyMs = _meter.CreateHistogram<double>("proxy.stage.latency.ms");

        _meter.CreateObservableGauge("proxy.queue.depth", () => Volatile.Read(ref _queueDepth));
        _meter.CreateObservableGauge("proxy.sessions.active", () => Volatile.Read(ref _activeSessions));
        _meter.CreateObservableGauge("proxy.pending.requests", () => Volatile.Read(ref _pendingRequests));
        _meter.CreateObservableGauge("proxy.inflight.requests", () => Volatile.Read(ref _inflightRequests));
        _meter.CreateObservableGauge("proxy.target.latency.ewma.ms", () => ReadTargetLatencyEwma());
    }

    public long QueueDepth => Volatile.Read(ref _queueDepth);
    public long ActiveSessions => Volatile.Read(ref _activeSessions);
    public long PendingRequests => Volatile.Read(ref _pendingRequests);
    public long InflightRequests => Volatile.Read(ref _inflightRequests);
    public long TotalEnqueued => Volatile.Read(ref _totalEnqueued);
    public long TotalCompleted => Volatile.Read(ref _totalCompleted);
    public long TotalFailed => Volatile.Read(ref _totalFailed);
    public long TotalQueuePublishErrors => Volatile.Read(ref _totalQueuePublishErrors);
    public long TotalQueueConsumeErrors => Volatile.Read(ref _totalQueueConsumeErrors);

    public void OnSessionOpened() => Interlocked.Increment(ref _activeSessions);

    public void OnSessionClosed() => Interlocked.Decrement(ref _activeSessions);

    public void OnPendingAdded() => Interlocked.Increment(ref _pendingRequests);

    public void OnPendingRemoved() => Interlocked.Decrement(ref _pendingRequests);

    public void OnInflightStarted() => Interlocked.Increment(ref _inflightRequests);

    public void OnInflightFinished() => Interlocked.Decrement(ref _inflightRequests);

    public void OnRequestEnqueued()
    {
        Interlocked.Increment(ref _queueDepth);
        Interlocked.Increment(ref _totalEnqueued);
        _requestsEnqueued.Add(1);
    }

    public void OnRequestDequeued() => Interlocked.Decrement(ref _queueDepth);

    public void OnRequestCompleted(double latencyMs)
    {
        Interlocked.Increment(ref _totalCompleted);
        _requestsCompleted.Add(1);
        _requestLatencyMs.Record(latencyMs);
    }

    public void OnRequestFailed(int workerId, double latencyMs)
    {
        Interlocked.Increment(ref _totalFailed);
        _requestsFailed.Add(1);
        _workerErrors.Add(1, new KeyValuePair<string, object?>("worker.id", workerId));
        _requestLatencyMs.Record(latencyMs);
        _workerErrorCounts.AddOrUpdate(workerId, 1, (_, current) => current + 1);
    }

    public void OnQueuePublishError()
    {
        Interlocked.Increment(ref _totalQueuePublishErrors);
        _queuePublishErrors.Add(1);
    }

    public void OnQueueConsumeError()
    {
        Interlocked.Increment(ref _totalQueueConsumeErrors);
        _queueConsumeErrors.Add(1);
    }

    public void OnTargetConnectRetry() => _targetConnectRetries.Add(1);

    public void RecordStageLatency(string stage, double elapsedMs)
    {
        _stageLatencyMs.Record(elapsedMs, new KeyValuePair<string, object?>("stage", stage));

        if (stage.Equals("target_roundtrip", StringComparison.Ordinal))
        {
            lock (_latencySync)
            {
                _targetLatencyEwmaMs = _targetLatencyEwmaMs == 0
                    ? elapsedMs
                    : (_targetLatencyEwmaMs * 0.85) + (elapsedMs * 0.15);
            }
        }
    }

    public double ReadTargetLatencyEwma()
    {
        lock (_latencySync)
        {
            return _targetLatencyEwmaMs;
        }
    }

    public IReadOnlyDictionary<int, long> GetWorkerErrorSnapshot() =>
        _workerErrorCounts.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value);

    public ProxyMetricsSnapshot GetSnapshot() => new(
        QueueDepth,
        ActiveSessions,
        PendingRequests,
        InflightRequests,
        TotalEnqueued,
        TotalCompleted,
        TotalFailed,
        TotalQueuePublishErrors,
        TotalQueueConsumeErrors,
        ReadTargetLatencyEwma());

    public void Dispose() => _meter.Dispose();
}

public readonly record struct ProxyMetricsSnapshot(
    long QueueDepth,
    long ActiveSessions,
    long PendingRequests,
    long InflightRequests,
    long TotalEnqueued,
    long TotalCompleted,
    long TotalFailed,
    long TotalQueuePublishErrors,
    long TotalQueueConsumeErrors,
    double TargetLatencyEwmaMs);
