using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace TDSQueue.Proxy;

public sealed class TargetConnectionManager
{
    private readonly ProxyOptions _options;
    private readonly ProxyMetrics _metrics;
    private readonly ILogger<TargetConnectionManager> _logger;
    private readonly SemaphoreSlim _activeConnections;
    private readonly Random _jitter = new();
    private readonly TargetCircuitBreaker _circuitBreaker;

    public TargetConnectionManager(
        ProxyOptions options,
        ProxyMetrics metrics,
        ILogger<TargetConnectionManager> logger)
    {
        _options = options;
        _metrics = metrics;
        _logger = logger;
        _activeConnections = new SemaphoreSlim(_options.MaxTargetConnections, _options.MaxTargetConnections);
        _circuitBreaker = new TargetCircuitBreaker(
            _options.CircuitBreakerFailuresThreshold,
            TimeSpan.FromSeconds(_options.CircuitBreakerOpenSeconds));
    }

    public bool IsCircuitOpen => _circuitBreaker.IsOpen;

    public async ValueTask<TargetConnectionLease> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        _circuitBreaker.ThrowIfOpen();
        await _activeConnections.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var client = await ConnectWithRetryAsync(endpoint, cancellationToken).ConfigureAwait(false);
            _circuitBreaker.OnSuccess();
            return new TargetConnectionLease(client, _activeConnections);
        }
        catch
        {
            _circuitBreaker.OnFailure();
            _activeConnections.Release();
            throw;
        }
    }

    private async Task<TcpClient> ConnectWithRetryAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var maxRetries = _options.TargetConnectMaxRetries;
        var baseDelayMs = _options.TargetConnectRetryDelayMs;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var client = new TcpClient();

            try
            {
                await client.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (attempt <= maxRetries)
            {
                client.Dispose();
                _metrics.OnTargetConnectRetry();

                var jitterMs = _jitter.Next(0, 30);
                var delayMs = Math.Min(baseDelayMs * attempt, 5000) + jitterMs;

                _logger.LogWarning(
                    ex,
                    "Target connect retry {Attempt}/{MaxRetries} to {Endpoint} after {DelayMs} ms",
                    attempt,
                    maxRetries,
                    endpoint,
                    delayMs);

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

public readonly struct TargetConnectionLease : IDisposable
{
    private readonly SemaphoreSlim _slots;

    public TargetConnectionLease(TcpClient client, SemaphoreSlim slots)
    {
        Client = client;
        _slots = slots;
    }

    public TcpClient Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        _slots.Release();
    }
}
