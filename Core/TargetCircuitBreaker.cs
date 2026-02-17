namespace TDSQueue.Proxy;

public sealed class TargetCircuitBreaker
{
    private readonly object _sync = new();
    private readonly int _failuresThreshold;
    private readonly TimeSpan _openDuration;

    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;

    public TargetCircuitBreaker(int failuresThreshold, TimeSpan openDuration)
    {
        _failuresThreshold = failuresThreshold;
        _openDuration = openDuration;
    }

    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                if (_openUntil is null)
                {
                    return false;
                }

                if (DateTimeOffset.UtcNow >= _openUntil.Value)
                {
                    _openUntil = null;
                    _consecutiveFailures = 0;
                    return false;
                }

                return true;
            }
        }
    }

    public DateTimeOffset? OpenUntil
    {
        get
        {
            lock (_sync)
            {
                return _openUntil;
            }
        }
    }

    public void ThrowIfOpen()
    {
        if (!IsOpen)
        {
            return;
        }

        var until = OpenUntil;
        throw new InvalidOperationException($"Target circuit breaker is open until {until:O}.");
    }

    public void OnSuccess()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
        }
    }

    public void OnFailure()
    {
        lock (_sync)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _failuresThreshold)
            {
                _openUntil = DateTimeOffset.UtcNow.Add(_openDuration);
                _consecutiveFailures = 0;
            }
        }
    }
}
