using System.Collections.Concurrent;

namespace TDSQueue.Proxy;

public sealed class PendingRequestStore
{
    private readonly ConcurrentDictionary<Guid, ProxyContext> _pending = new();

    public int Count => _pending.Count;

    public void Add(ProxyContext context)
    {
        if (!_pending.TryAdd(context.RequestId, context))
        {
            throw new InvalidOperationException($"Duplicated request id {context.RequestId}.");
        }
    }

    public bool TryTake(Guid requestId, out ProxyContext? context)
    {
        if (_pending.TryRemove(requestId, out var value))
        {
            context = value;
            return true;
        }

        context = null;
        return false;
    }

    public bool TryGet(Guid requestId, out ProxyContext? context)
    {
        if (_pending.TryGetValue(requestId, out var value))
        {
            context = value;
            return true;
        }

        context = null;
        return false;
    }

    public bool TryRemove(Guid requestId) => _pending.TryRemove(requestId, out _);

    public int SweepExpired(TimeSpan ttl, Action<ProxyContext> onExpired)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var ttlTicks = ttl.Ticks;
        var removed = 0;

        foreach (var pair in _pending)
        {
            var context = pair.Value;
            var ageTicks = nowTicks - context.CreatedAtUtcTicks;
            if (ageTicks < ttlTicks)
            {
                continue;
            }

            if (_pending.TryRemove(pair.Key, out var expired))
            {
                onExpired(expired);
                removed++;
            }
        }

        return removed;
    }
}
