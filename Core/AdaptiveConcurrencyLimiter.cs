using System.Threading;

namespace TDSQueue.Proxy;

public sealed class AdaptiveConcurrencyLimiter
{
    private readonly SemaphoreSlim _semaphore;
    private readonly object _sync = new();
    private int _debt;
    private int _minLimit;
    private int _maxLimit;

    public AdaptiveConcurrencyLimiter(int minLimit, int maxLimit, int initialLimit)
    {
        _minLimit = minLimit;
        _maxLimit = maxLimit;
        CurrentLimit = Math.Clamp(initialLimit, _minLimit, _maxLimit);
        _semaphore = new SemaphoreSlim(CurrentLimit, _maxLimit);
    }

    public int MinLimit => _minLimit;
    public int MaxLimit => _maxLimit;
    public int CurrentLimit { get; private set; }

    public async ValueTask<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this);
    }

    public void SetLimit(int requestedLimit)
    {
        lock (_sync)
        {
            var next = Math.Clamp(requestedLimit, _minLimit, _maxLimit);
            if (next == CurrentLimit)
            {
                return;
            }

            if (next > CurrentLimit)
            {
                var delta = next - CurrentLimit;
                CurrentLimit = next;

                if (_debt > 0)
                {
                    var paid = Math.Min(_debt, delta);
                    _debt -= paid;
                    delta -= paid;
                }

                if (delta > 0)
                {
                    _semaphore.Release(delta);
                }

                return;
            }

            var decrease = CurrentLimit - next;
            CurrentLimit = next;
            _debt += decrease;
        }
    }

    public void Reconfigure(int? minLimit, int? maxLimit, int? currentLimit)
    {
        lock (_sync)
        {
            if (minLimit is { } min)
            {
                _minLimit = Math.Max(1, min);
            }

            if (maxLimit is { } max)
            {
                _maxLimit = Math.Max(_minLimit, max);
            }

            var targetCurrent = currentLimit ?? CurrentLimit;
            var clamped = Math.Clamp(targetCurrent, _minLimit, _maxLimit);
            SetLimit(clamped);
        }
    }

    private void Release()
    {
        lock (_sync)
        {
            if (_debt > 0)
            {
                _debt--;
                return;
            }
        }

        _semaphore.Release();
    }

    public readonly struct Lease : IDisposable
    {
        private readonly AdaptiveConcurrencyLimiter _owner;

        public Lease(AdaptiveConcurrencyLimiter owner)
        {
            _owner = owner;
        }

        public void Dispose() => _owner.Release();
    }
}
