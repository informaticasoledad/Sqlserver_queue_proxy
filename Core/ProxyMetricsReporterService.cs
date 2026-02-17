using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq;

namespace TDSQueue.Proxy;

public sealed class ProxyMetricsReporterService : BackgroundService
{
    private readonly ProxyOptions _options;
    private readonly RuntimeTuningState _runtimeTuning;
    private readonly ProxyMetrics _metrics;
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly ILogger<ProxyMetricsReporterService> _logger;

    public ProxyMetricsReporterService(
        IOptions<ProxyOptions> options,
        RuntimeTuningState runtimeTuning,
        ProxyMetrics metrics,
        AdaptiveConcurrencyLimiter limiter,
        ILogger<ProxyMetricsReporterService> logger)
    {
        _options = options.Value;
        _runtimeTuning = runtimeTuning;
        _metrics = metrics;
        _limiter = limiter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.MetricsReportIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            AdjustBackpressure();

            var snapshot = _metrics.GetSnapshot();
            var workerErrors = _metrics.GetWorkerErrorSnapshot();
            var workerErrorsText = workerErrors.Count == 0
                ? "none"
                : string.Join(", ", workerErrors.Select(kvp => $"w{kvp.Key}={kvp.Value}"));

            _logger.LogInformation(
                "Metrics | sessions={ActiveSessions} pending={Pending} inflight={InFlight}/{Limit} queueDepth={QueueDepth} enqueued={Enqueued} completed={Completed} failed={Failed} qPubErr={QueuePublishErrors} qConErr={QueueConsumeErrors} targetEwmaMs={TargetEwmaMs:F1} workerErrors={WorkerErrors}",
                snapshot.ActiveSessions,
                snapshot.PendingRequests,
                snapshot.InflightRequests,
                _limiter.CurrentLimit,
                snapshot.QueueDepth,
                snapshot.TotalEnqueued,
                snapshot.TotalCompleted,
                snapshot.TotalFailed,
                snapshot.TotalQueuePublishErrors,
                snapshot.TotalQueueConsumeErrors,
                snapshot.TargetLatencyEwmaMs,
                workerErrorsText);
        }
    }

    private void AdjustBackpressure()
    {
        var runtime = _runtimeTuning.Snapshot();

        if (!runtime.AdaptiveBackpressureEnabled)
        {
            return;
        }

        var snapshot = _metrics.GetSnapshot();
        var nextLimit = _limiter.CurrentLimit;

        if (snapshot.QueueDepth >= runtime.QueueHighWatermark &&
            snapshot.TargetLatencyEwmaMs <= runtime.TargetLatencyLowMs)
        {
            nextLimit++;
        }
        else if (snapshot.TargetLatencyEwmaMs >= runtime.TargetLatencyHighMs ||
                 snapshot.QueueDepth <= runtime.QueueLowWatermark)
        {
            nextLimit--;
        }

        _limiter.SetLimit(nextLimit);
    }
}
