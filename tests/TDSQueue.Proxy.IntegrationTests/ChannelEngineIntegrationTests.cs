using System.Threading.Channels;

namespace TDSQueue.Proxy.IntegrationTests;

public sealed class ChannelEngineIntegrationTests
{
    [Fact]
    public async Task Channel_Enqueue_Read_Ack_Works()
    {
        var bounded = Channel.CreateBounded<QueuedRequest>(new BoundedChannelOptions(64)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        var queue = new ChannelProxyQueueEngine(bounded);
        try
        {
            var requestId = Guid.NewGuid();
            await queue.EnqueueAsync(new QueuedRequest(requestId), CancellationToken.None);

            var delivery = await TestHelpers.ReadSingleAsync(queue, TimeSpan.FromSeconds(3));
            Assert.Equal(requestId, delivery.RequestId);

            await delivery.AckAsync(CancellationToken.None);
        }
        finally
        {
            queue.TryComplete();
        }
    }
}
