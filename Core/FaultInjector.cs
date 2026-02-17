namespace TDSQueue.Proxy;

public sealed class FaultInjector
{
    private readonly ProxyOptions _options;
    private readonly Random _random = new();

    public FaultInjector(ProxyOptions options)
    {
        _options = options;
    }

    public async ValueTask InjectBeforeTargetCallAsync(CancellationToken cancellationToken)
    {
        var fault = _options.FaultInjection;
        if (!fault.Enabled)
        {
            return;
        }

        if (fault.DelayProbability > 0 && _random.NextDouble() <= fault.DelayProbability)
        {
            await Task.Delay(fault.DelayMs, cancellationToken).ConfigureAwait(false);
        }

        if (fault.ThrowProbability > 0 && _random.NextDouble() <= fault.ThrowProbability)
        {
            throw new IOException("Fault injection: synthetic target failure.");
        }
    }
}
