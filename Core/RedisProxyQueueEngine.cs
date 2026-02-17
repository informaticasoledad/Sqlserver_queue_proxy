using StackExchange.Redis;

namespace TDSQueue.Proxy;

public sealed class RedisProxyQueueEngine : IProxyQueueEngine, IDisposable
{
    private readonly ProxyOptions _options;
    private readonly ProxyMetrics _metrics;
    private readonly ILogger<RedisProxyQueueEngine> _logger;
    private readonly ConnectionMultiplexer _mux;
    private readonly IDatabase _db;

    public RedisProxyQueueEngine(
        ProxyOptions options,
        ProxyMetrics metrics,
        ILogger<RedisProxyQueueEngine> logger)
    {
        _options = options;
        _metrics = metrics;
        _logger = logger;

        var redisOptions = ConfigurationOptions.Parse(SecretResolver.Resolve(_options.Redis.Configuration));
        redisOptions.AbortOnConnectFail = _options.Redis.AbortOnConnectFail;
        redisOptions.Ssl = _options.Redis.UseTls;
        if (_options.Redis.UseTls && !string.IsNullOrWhiteSpace(_options.Redis.SslHost))
        {
            redisOptions.SslHost = _options.Redis.SslHost;
        }

        _mux = ConnectionMultiplexer.Connect(redisOptions);
        _db = _mux.GetDatabase();
        RequeueStaleProcessingMessages();

        _logger.LogInformation(
            "Redis queue engine enabled. QueueKey={QueueKey}, ProcessingKey={ProcessingKey}, Endpoint={Config}",
            _options.Redis.QueueKey,
            _options.Redis.ProcessingQueueKey,
            _options.Redis.Configuration);
    }

    public async ValueTask EnqueueAsync(QueuedRequest request, CancellationToken cancellationToken)
    {
        var payload = request.RequestId.ToString("N");
        var maxRetries = _options.Redis.EnqueueMaxRetries;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _db.ListLeftPushAsync(_options.Redis.QueueKey, payload).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt <= maxRetries)
            {
                _metrics.OnQueuePublishError();
                var delay = Math.Min(_options.Redis.EnqueueRetryDelayMs * attempt, 5000);
                _logger.LogWarning(
                    ex,
                    "Redis enqueue retry {Attempt}/{MaxRetries} after {DelayMs} ms",
                    attempt,
                    maxRetries,
                    delay);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _metrics.OnQueuePublishError();
                throw;
            }
        }
    }

    public async IAsyncEnumerable<QueueDelivery> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var maxRetries = _options.Redis.ConsumeMaxRetries;
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            RedisValue result;
            try
            {
                result = await ExecutePopWithPollingAsync(cancellationToken).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception ex)
            {
                _metrics.OnQueueConsumeError();
                consecutiveFailures++;

                if (consecutiveFailures > maxRetries)
                {
                    throw;
                }

                var delay = Math.Min(_options.Redis.ConsumeRetryDelayMs * consecutiveFailures, 5000);
                _logger.LogWarning(
                    ex,
                    "Redis consume retry {Attempt}/{MaxRetries} after {DelayMs} ms",
                    consecutiveFailures,
                    maxRetries,
                    delay);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (result.IsNullOrEmpty)
            {
                continue;
            }

            var payload = (string?)result;
            if (string.IsNullOrWhiteSpace(payload) || !Guid.TryParseExact(payload, "N", out var requestId))
            {
                _metrics.OnQueueConsumeError();
                _logger.LogWarning("Redis queue message ignored due to invalid payload: '{Payload}'", payload);
                continue;
            }

            yield return new QueueDelivery(
                requestId,
                ack: ct => AckAsync(payload, ct),
                nack: (requeue, ct) => NackAsync(payload, requeue, ct));
        }
    }

    public void TryComplete()
    {
        // No-op for Redis external queue.
    }

    public ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(_mux.IsConnected);

    public void Dispose()
    {
        _mux.Dispose();
    }

    private async Task<RedisValue> ExecutePopWithPollingAsync(CancellationToken cancellationToken)
    {
        var delayMs = 100;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _options.Redis.PopBlockTimeoutSeconds));

        while (!cancellationToken.IsCancellationRequested)
        {
            var payload = await _db.ListRightPopLeftPushAsync(
                    _options.Redis.QueueKey,
                    _options.Redis.ProcessingQueueKey)
                .ConfigureAwait(false);

            if (!payload.IsNullOrEmpty)
            {
                return payload;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return RedisValue.Null;
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        return RedisValue.Null;
    }

    private async ValueTask AckAsync(string payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _db.ListRemoveAsync(_options.Redis.ProcessingQueueKey, payload, 1).ConfigureAwait(false);
    }

    private async ValueTask NackAsync(string payload, bool requeue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var queueTarget = requeue ? _options.Redis.QueueKey : string.Empty;
        await _db.ScriptEvaluateAsync(
                @"
local removed = redis.call('LREM', KEYS[1], 1, ARGV[1])
if removed == 1 and ARGV[2] == '1' then
    redis.call('LPUSH', KEYS[2], ARGV[1])
end
return removed
",
                new RedisKey[] { _options.Redis.ProcessingQueueKey, queueTarget },
                new RedisValue[] { payload, requeue ? "1" : "0" })
            .ConfigureAwait(false);
    }

    private void RequeueStaleProcessingMessages()
    {
        var moved = 0;
        while (true)
        {
            var payload = _db.ListRightPopLeftPush(_options.Redis.ProcessingQueueKey, _options.Redis.QueueKey);
            if (payload.IsNull)
            {
                break;
            }

            moved++;
        }

        if (moved > 0)
        {
            _logger.LogWarning(
                "Redis queue engine recovered {Count} stale messages from processing list to queue list.",
                moved);
        }
    }
}
