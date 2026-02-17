namespace TDSQueue.Proxy.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class RedisEngineIntegrationTests
{
    private readonly IntegrationDockerFixture _fixture;

    public RedisEngineIntegrationTests(IntegrationDockerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Redis_Enqueue_Read_Ack_Works()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var queueKey = $"tdsqueue:it:queue:{Guid.NewGuid():N}";
        var processingKey = $"tdsqueue:it:processing:{Guid.NewGuid():N}";
        await _fixture.PurgeRedisListsAsync(queueKey, processingKey);

        using var metrics = new ProxyMetrics();
        using var queue = new RedisProxyQueueEngine(
            _fixture.BuildRedisOptions(queueKey, processingKey),
            metrics,
            TestHelpers.LoggerFactory().CreateLogger<RedisProxyQueueEngine>());

        var requestId = Guid.NewGuid();
        await queue.EnqueueAsync(new QueuedRequest(requestId), CancellationToken.None);

        var delivery = await TestHelpers.ReadSingleAsync(queue, TimeSpan.FromSeconds(6));
        Assert.Equal(requestId, delivery.RequestId);

        await delivery.AckAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Redis_Nack_Requeue_Redelivers_Message()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var queueKey = $"tdsqueue:it:queue:{Guid.NewGuid():N}";
        var processingKey = $"tdsqueue:it:processing:{Guid.NewGuid():N}";
        await _fixture.PurgeRedisListsAsync(queueKey, processingKey);

        using var metrics = new ProxyMetrics();
        using var queue = new RedisProxyQueueEngine(
            _fixture.BuildRedisOptions(queueKey, processingKey),
            metrics,
            TestHelpers.LoggerFactory().CreateLogger<RedisProxyQueueEngine>());

        var requestId = Guid.NewGuid();
        await queue.EnqueueAsync(new QueuedRequest(requestId), CancellationToken.None);

        var first = await TestHelpers.ReadSingleAsync(queue, TimeSpan.FromSeconds(6));
        Assert.Equal(requestId, first.RequestId);
        await first.NackAsync(true, CancellationToken.None);

        var second = await TestHelpers.ReadSingleAsync(queue, TimeSpan.FromSeconds(6));
        Assert.Equal(requestId, second.RequestId);
        await second.AckAsync(CancellationToken.None);
    }
}
