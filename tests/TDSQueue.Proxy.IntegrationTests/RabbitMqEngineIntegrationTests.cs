namespace TDSQueue.Proxy.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class RabbitMqEngineIntegrationTests
{
    private readonly IntegrationDockerFixture _fixture;

    public RabbitMqEngineIntegrationTests(IntegrationDockerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RabbitMq_Enqueue_Read_Ack_Works()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var queueName = $"tdsqueue.it.{Guid.NewGuid():N}";
        await _fixture.PurgeRabbitQueueAsync(queueName);

        using var metrics = new ProxyMetrics();
        using var queue = new RabbitMqProxyQueueEngine(
            _fixture.BuildRabbitOptions(queueName),
            metrics,
            TestHelpers.LoggerFactory().CreateLogger<RabbitMqProxyQueueEngine>());

        var requestId = Guid.NewGuid();
        await queue.EnqueueAsync(new QueuedRequest(requestId), CancellationToken.None);

        var delivery = await TestHelpers.ReadSingleAsync(queue, TimeSpan.FromSeconds(8));
        Assert.Equal(requestId, delivery.RequestId);

        await delivery.AckAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RabbitMq_Nack_Requeue_Redelivers_Message()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var queueName = $"tdsqueue.it.{Guid.NewGuid():N}";
        await _fixture.PurgeRabbitQueueAsync(queueName);

        using var metrics = new ProxyMetrics();
        using var queue = new RabbitMqProxyQueueEngine(
            _fixture.BuildRabbitOptions(queueName),
            metrics,
            TestHelpers.LoggerFactory().CreateLogger<RabbitMqProxyQueueEngine>());

        var requestId = Guid.NewGuid();
        await queue.EnqueueAsync(new QueuedRequest(requestId), CancellationToken.None);

        var first = await TestHelpers.ReadSingleAsync(queue, TimeSpan.FromSeconds(8));
        Assert.Equal(requestId, first.RequestId);
        await first.NackAsync(true, CancellationToken.None);

        var second = await TestHelpers.ReadSingleAsync(queue, TimeSpan.FromSeconds(8));
        Assert.Equal(requestId, second.RequestId);
        await second.AckAsync(CancellationToken.None);
    }
}
