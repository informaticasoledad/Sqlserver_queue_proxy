using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TDSQueue.Proxy;

public sealed class PendingRequestJanitorService : BackgroundService
{
    private readonly ProxyOptions _options;
    private readonly PendingRequestStore _pending;
    private readonly ReplayGuard _replayGuard;
    private readonly ProxyMetrics _metrics;
    private readonly ILogger<PendingRequestJanitorService> _logger;

    public PendingRequestJanitorService(
        IOptions<ProxyOptions> options,
        PendingRequestStore pending,
        ReplayGuard replayGuard,
        ProxyMetrics metrics,
        ILogger<PendingRequestJanitorService> logger)
    {
        _options = options.Value;
        _pending = pending;
        _replayGuard = replayGuard;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.PendingRequestTtlSeconds / 4)));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var ttl = ComputeDynamicTtl();
            var swept = _pending.SweepExpired(ttl, context =>
            {
                context.ResponseSource.TrySetException(new TimeoutException(
                    $"Request {context.RequestId} expired in pending store after {ttl}."));

                _replayGuard.Clear(context.RequestId);
                context.Dispose();
                _metrics.OnPendingRemoved();
            });

            if (swept > 0)
            {
                _logger.LogWarning("Pending request janitor removed {Count} expired items.", swept);
            }
        }
    }

    private TimeSpan ComputeDynamicTtl()
    {
        var baseTtl = TimeSpan.FromSeconds(_options.PendingRequestTtlSeconds);
        var ewma = _metrics.ReadTargetLatencyEwma();
        if (ewma <= 0)
        {
            return baseTtl;
        }

        var adaptive = TimeSpan.FromMilliseconds(ewma * 4);
        return adaptive > baseTtl ? adaptive : baseTtl;
    }
}
