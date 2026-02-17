namespace TDSQueue.Proxy;

public interface IProxyQueueEngine
{
    ValueTask EnqueueAsync(QueuedRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<QueueDelivery> ReadAllAsync(CancellationToken cancellationToken);
    ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken);
    void TryComplete();
}
