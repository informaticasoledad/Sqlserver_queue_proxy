using System.Collections.Concurrent;

namespace TDSQueue.Proxy;

public sealed class ReplayGuard
{
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();

    public int Increment(Guid requestId) =>
        _attempts.AddOrUpdate(requestId, 1, (_, current) => current + 1);

    public void Clear(Guid requestId) => _attempts.TryRemove(requestId, out _);
}
