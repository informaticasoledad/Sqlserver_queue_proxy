using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TDSQueue.Proxy;

public sealed class TdsProxyWorkerService : BackgroundService
{
    private readonly ProxyOptions _options;
    private readonly IProxyQueueEngine _queue;
    private readonly PendingRequestStore _pending;
    private readonly ReplayGuard _replayGuard;
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly FaultInjector _faultInjector;
    private readonly ProxyMetrics _metrics;
    private readonly ILogger<TdsProxyWorkerService> _logger;

    public TdsProxyWorkerService(
        IOptions<ProxyOptions> options,
        IProxyQueueEngine queue,
        PendingRequestStore pending,
        ReplayGuard replayGuard,
        AdaptiveConcurrencyLimiter limiter,
        FaultInjector faultInjector,
        ProxyMetrics metrics,
        ILogger<TdsProxyWorkerService> logger)
    {
        _options = options.Value;
        _queue = queue;
        _pending = pending;
        _replayGuard = replayGuard;
        _limiter = limiter;
        _faultInjector = faultInjector;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _options.WorkerCount)
            .Select(workerId => RunWorkerAsync(workerId, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker {WorkerId} started.", workerId);

        try
        {
            await foreach (var delivery in _queue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using var lease = await _limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
                _metrics.OnInflightStarted();

                try
                {
                    _metrics.OnRequestDequeued();

                    if (!_pending.TryGet(delivery.RequestId, out var context) || context is null)
                    {
                        _logger.LogWarning(
                            "Worker {WorkerId}: request {RequestId} not found in pending store.",
                            workerId,
                            delivery.RequestId);

                        await delivery.AckAsync(cancellationToken).ConfigureAwait(false);
                        _replayGuard.Clear(delivery.RequestId);
                        continue;
                    }

                    var replayAttempt = _replayGuard.Increment(context.RequestId);
                    var processed = await ProcessWithRetryAsync(workerId, context, cancellationToken)
                        .ConfigureAwait(false);

                    if (processed)
                    {
                        await delivery.AckAsync(cancellationToken).ConfigureAwait(false);
                        FinalizeRequest(context);
                        continue;
                    }

                    var canRequeue = ShouldRequeueOnFailure() && replayAttempt < _options.MaxReplayAttempts;
                    if (canRequeue)
                    {
                        _logger.LogWarning(
                            "Worker {WorkerId}: request {RequestId} requeued attempt {Attempt}/{MaxAttempts}",
                            workerId,
                            context.RequestId,
                            replayAttempt,
                            _options.MaxReplayAttempts);

                        await delivery.NackAsync(true, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await delivery.NackAsync(false, cancellationToken).ConfigureAwait(false);
                        context.ResponseSource.TrySetException(new IOException(
                            $"Request {context.RequestId} failed after {replayAttempt} replay attempts."));
                        FinalizeRequest(context);
                    }
                }
                catch (Exception ex)
                {
                    _metrics.OnQueueConsumeError();
                    _logger.LogError(ex, "Worker {WorkerId}: delivery processing failure.", workerId);
                    await delivery.NackAsync(ShouldRequeueOnFailure(), cancellationToken)
                        .ConfigureAwait(false);

                    if (_pending.TryGet(delivery.RequestId, out var pendingContext) && pendingContext is not null)
                    {
                        pendingContext.ResponseSource.TrySetException(ex);
                        FinalizeRequest(pendingContext);
                    }
                }
                finally
                {
                    _metrics.OnInflightFinished();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker {WorkerId} canceled.", workerId);
        }
    }

    private async Task<bool> ProcessWithRetryAsync(int workerId, ProxyContext context, CancellationToken cancellationToken)
    {
        var retries = _options.RequestProcessingRetries;

        for (var attempt = 1; ; attempt++)
        {
            var (ok, _) = await ProcessAsync(workerId, context, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                return true;
            }

            if (attempt > retries)
            {
                return false;
            }

            var delay = Math.Min(_options.RequestRetryDelayMs * attempt, 1000);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(bool Success, Exception? Error)> ProcessAsync(
        int workerId,
        ProxyContext context,
        CancellationToken cancellationToken)
    {
        using var activity = ProxyDiagnostics.ActivitySource.StartActivity("proxy.worker.process");
        activity?.SetTag("worker.id", workerId);
        activity?.SetTag("request.id", context.RequestId);
        activity?.SetTag("session.id", context.Session.SessionId);

        try
        {
            await context.Session.TargetRequestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _faultInjector.InjectBeforeTargetCallAsync(cancellationToken).ConfigureAwait(false);

                var connectStarted = Stopwatch.GetTimestamp();
                await context.Session.EnsureTargetConnectedAsync(cancellationToken).ConfigureAwait(false);
                _metrics.RecordStageLatency("target_connect", Stopwatch.GetElapsedTime(connectStarted).TotalMilliseconds);

                var targetWriter = context.Session.TargetWriter
                    ?? throw new InvalidOperationException("Target writer not initialized.");

                var writeTargetStarted = Stopwatch.GetTimestamp();
                await targetWriter.WriteAsync(context.RequestBuffer.Memory, cancellationToken).ConfigureAwait(false);
                await targetWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                _metrics.RecordStageLatency("write_target", Stopwatch.GetElapsedTime(writeTargetStarted).TotalMilliseconds);

                if (context.Session.OpaqueEncryptedQueueMode)
                {
                    _metrics.OnRequestCompleted(context.GetElapsedMilliseconds());
                    context.ResponseSource.TrySetResult(Array.Empty<byte>());
                    return (true, null);
                }

                context.Session.EnqueueAwaitingResponse(context);

                var roundtripStarted = Stopwatch.GetTimestamp();
                await context.ResponseSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                _metrics.RecordStageLatency("target_roundtrip", Stopwatch.GetElapsedTime(roundtripStarted).TotalMilliseconds);

                _metrics.OnRequestCompleted(context.GetElapsedMilliseconds());
                return (true, null);
            }
            finally
            {
                context.Session.TargetRequestLock.Release();
            }
        }
        catch (Exception ex)
        {
            if (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                context.Session.InvalidateTargetConnection();
            }

            _logger.LogError(
                ex,
                "Worker {WorkerId}: failed to process session {SessionId}",
                workerId,
                context.Session.SessionId);

            _metrics.OnRequestFailed(workerId, context.GetElapsedMilliseconds());
            return (false, ex);
        }
    }

    private void FinalizeRequest(ProxyContext context)
    {
        if (_pending.TryRemove(context.RequestId))
        {
            _metrics.OnPendingRemoved();
        }

        _replayGuard.Clear(context.RequestId);
    }

    private bool ShouldRequeueOnFailure()
    {
        if (_options.QueueEngine.Equals("Redis", StringComparison.OrdinalIgnoreCase))
        {
            return _options.Redis.RequeueOnFailure;
        }

        if (_options.QueueEngine.Equals("RabbitMq", StringComparison.OrdinalIgnoreCase))
        {
            return _options.RabbitMq.RequeueOnFailure;
        }

        // Channel engine has no built-in requeue semantics in QueueDelivery.Nack.
        // Returning false avoids dropping requests silently and leaving callers waiting.
        return false;
    }

}
