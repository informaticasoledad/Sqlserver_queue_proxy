using System.Threading;

namespace TDSQueue.Proxy;

public sealed class QueueDelivery
{
    private readonly Func<CancellationToken, ValueTask> _ack;
    private readonly Func<bool, CancellationToken, ValueTask> _nack;
    private int _completed;

    public QueueDelivery(
        Guid requestId,
        Func<CancellationToken, ValueTask>? ack = null,
        Func<bool, CancellationToken, ValueTask>? nack = null)
    {
        RequestId = requestId;
        _ack = ack ?? (_ => ValueTask.CompletedTask);
        _nack = nack ?? ((_, _) => ValueTask.CompletedTask);
    }

    public Guid RequestId { get; }

    public async ValueTask AckAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        await _ack(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask NackAsync(bool requeue, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        await _nack(requeue, cancellationToken).ConfigureAwait(false);
    }
}
