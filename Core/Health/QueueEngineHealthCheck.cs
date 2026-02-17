using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TDSQueue.Proxy.Health;

public sealed class QueueEngineHealthCheck : IHealthCheck
{
    private readonly IProxyQueueEngine _queue;

    public QueueEngineHealthCheck(IProxyQueueEngine queue)
    {
        _queue = queue;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var healthy = await _queue.IsHealthyAsync(cancellationToken).ConfigureAwait(false);
        return healthy
            ? HealthCheckResult.Healthy("Queue engine is healthy.")
            : HealthCheckResult.Unhealthy("Queue engine is unavailable.");
    }
}
