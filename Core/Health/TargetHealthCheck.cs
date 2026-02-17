using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TDSQueue.Proxy.Health;

public sealed class TargetHealthCheck : IHealthCheck
{
    private readonly TargetConnectionManager _targetConnectionManager;

    public TargetHealthCheck(TargetConnectionManager targetConnectionManager)
    {
        _targetConnectionManager = targetConnectionManager;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_targetConnectionManager.IsCircuitOpen)
        {
            return Task.FromResult(
                HealthCheckResult.Degraded("Target circuit breaker is open; traffic may be throttled."));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Target circuit breaker is closed."));
    }
}
