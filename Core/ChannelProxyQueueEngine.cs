using System.Threading.Channels;

namespace TDSQueue.Proxy;

public sealed class ChannelProxyQueueEngine : IProxyQueueEngine
{
    private readonly Channel<QueuedRequest> _channel;

    public ChannelProxyQueueEngine(Channel<QueuedRequest> channel)
    {
        _channel = channel;
    }

    public ValueTask EnqueueAsync(QueuedRequest request, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(request, cancellationToken);

    public async IAsyncEnumerable<QueueDelivery> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var request in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new QueueDelivery(request.RequestId);
        }
    }

    public ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);

    public void TryComplete() => _channel.Writer.TryComplete();
}
