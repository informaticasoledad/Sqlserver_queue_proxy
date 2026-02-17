using System.IO;
using System.Threading.Channels;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace TDSQueue.Proxy;

public sealed class RabbitMqProxyQueueEngine : IProxyQueueEngine, IDisposable
{
    private const int EnvelopeSize = 16;

    private readonly ProxyOptions _options;
    private readonly ProxyMetrics _metrics;
    private readonly ILogger<RabbitMqProxyQueueEngine> _logger;
    private readonly IConnection _connection;
    private readonly IModel _publisherChannel;
    private readonly Channel<QueuedRequest> _publishQueue;
    private readonly CancellationTokenSource _publisherCts = new();
    private readonly Task _publisherTask;
    private readonly object _publisherSync = new();

    public RabbitMqProxyQueueEngine(
        ProxyOptions options,
        ProxyMetrics metrics,
        ILogger<RabbitMqProxyQueueEngine> logger)
    {
        _options = options;
        _metrics = metrics;
        _logger = logger;

        var rabbitOptions = _options.RabbitMq;
        var factory = new ConnectionFactory
        {
            HostName = rabbitOptions.HostName,
            Port = rabbitOptions.Port,
            UserName = SecretResolver.Resolve(rabbitOptions.UserName),
            Password = SecretResolver.Resolve(rabbitOptions.Password),
            VirtualHost = rabbitOptions.VirtualHost,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
        };

        if (rabbitOptions.UseTls)
        {
            factory.Ssl = new SslOption
            {
                Enabled = true,
                ServerName = rabbitOptions.TlsServerName ?? rabbitOptions.HostName,
                AcceptablePolicyErrors = rabbitOptions.TlsAcceptInvalidCertificates
                    ? System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors | System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch
                    : System.Net.Security.SslPolicyErrors.None
            };
        }

        _connection = factory.CreateConnection();
        _publisherChannel = _connection.CreateModel();
        DeclareQueue(_publisherChannel);

        _publishQueue = Channel.CreateBounded<QueuedRequest>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        _publisherTask = Task.Run(() => RunPublisherAsync(_publisherCts.Token));

        _logger.LogInformation(
            "RabbitMQ queue engine enabled. Queue={QueueName}, Host={Host}:{Port}, VHost={VirtualHost}",
            rabbitOptions.QueueName,
            rabbitOptions.HostName,
            rabbitOptions.Port,
            rabbitOptions.VirtualHost);
    }

    public ValueTask EnqueueAsync(QueuedRequest request, CancellationToken cancellationToken) =>
        _publishQueue.Writer.WriteAsync(request, cancellationToken);

    public async IAsyncEnumerable<QueueDelivery> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var inbox = Channel.CreateUnbounded<QueueDelivery>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        var pump = Task.Run(() => RunConsumerPumpAsync(inbox.Writer, cancellationToken), cancellationToken);

        try
        {
            await foreach (var delivery in inbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return delivery;
            }
        }
        finally
        {
            inbox.Writer.TryComplete();
            try
            {
                await pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    public void TryComplete()
    {
        _publishQueue.Writer.TryComplete();
        _publisherCts.Cancel();
    }

    public ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(_connection.IsOpen && _publisherChannel.IsOpen);

    public void Dispose()
    {
        TryComplete();
        try
        {
            _publisherTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RabbitMQ publisher task shutdown warning.");
        }

        _publisherChannel.Dispose();
        _connection.Dispose();
        _publisherCts.Dispose();
    }

    private async Task RunConsumerPumpAsync(ChannelWriter<QueueDelivery> writer, CancellationToken cancellationToken)
    {
        var retries = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var consumerChannel = _connection.CreateModel();
                var sync = new object();
                var restartSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                DeclareQueue(consumerChannel);
                consumerChannel.BasicQos(0, _options.RabbitMq.PrefetchCount, false);
                consumerChannel.ModelShutdown += (_, _) => restartSignal.TrySetResult();

                var consumer = new AsyncEventingBasicConsumer(consumerChannel);
                consumer.Received += (_, args) =>
                {
                    try
                    {
                        var body = args.Body.ToArray();
                        if (body.Length != EnvelopeSize)
                        {
                            lock (sync)
                            {
                                if (consumerChannel.IsOpen)
                                {
                                    consumerChannel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
                                }
                            }

                            _logger.LogWarning("RabbitMQ message ignored due to invalid envelope size: {Size}", body.Length);
                            return Task.CompletedTask;
                        }

                        var requestId = new Guid(body);
                        var delivery = new QueueDelivery(
                            requestId,
                            ack: _ =>
                            {
                                lock (sync)
                                {
                                    if (consumerChannel.IsOpen)
                                    {
                                        consumerChannel.BasicAck(args.DeliveryTag, multiple: false);
                                    }
                                }

                                return ValueTask.CompletedTask;
                            },
                            nack: (requeue, _) =>
                            {
                                lock (sync)
                                {
                                    if (consumerChannel.IsOpen)
                                    {
                                        consumerChannel.BasicNack(args.DeliveryTag, multiple: false, requeue: requeue);
                                    }
                                }

                                return ValueTask.CompletedTask;
                            });

                        if (!writer.TryWrite(delivery))
                        {
                            _metrics.OnQueueConsumeError();
                            _logger.LogWarning("RabbitMQ delivery dropped because local inbox is closed.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _metrics.OnQueueConsumeError();
                        _logger.LogError(ex, "RabbitMQ consume callback failed.");
                    }

                    return Task.CompletedTask;
                };

                var consumerTag = consumerChannel.BasicConsume(
                    queue: _options.RabbitMq.QueueName,
                    autoAck: false,
                    consumer: consumer);

                retries = 0;
                await restartSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                if (consumerChannel.IsOpen)
                {
                    consumerChannel.BasicCancelNoWait(consumerTag);
                }

                await Task.Delay(_options.RabbitMq.ConsumerChannelRestartDelayMs, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                writer.TryComplete();
                return;
            }
            catch (Exception ex)
            {
                retries++;
                _metrics.OnQueueConsumeError();

                var maxRetries = _options.RabbitMq.ConsumeMaxRetries;
                var delay = Math.Min(_options.RabbitMq.ConsumeRetryDelayMs * retries, 5000);

                _logger.LogWarning(
                    ex,
                    "RabbitMQ consumer setup failed retry {Retry}/{MaxRetries} after {DelayMs} ms",
                    retries,
                    maxRetries,
                    delay);

                if (retries > maxRetries)
                {
                    writer.TryComplete(ex);
                    return;
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        writer.TryComplete();
    }

    private async Task RunPublisherAsync(CancellationToken cancellationToken)
    {
        var batch = new List<QueuedRequest>(_options.RabbitMq.PublishBatchSize);

        while (await _publishQueue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            batch.Clear();

            var first = await _publishQueue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            batch.Add(first);

            var deadline = DateTime.UtcNow.AddMilliseconds(_options.RabbitMq.PublishBatchMaxDelayMs);

            while (batch.Count < _options.RabbitMq.PublishBatchSize)
            {
                if (!_publishQueue.Reader.TryRead(out var item))
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        break;
                    }

                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                batch.Add(item);
            }

            await PublishBatchWithRetryAsync(batch, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishBatchWithRetryAsync(IReadOnlyList<QueuedRequest> batch, CancellationToken cancellationToken)
    {
        var maxRetries = _options.RabbitMq.PublishMaxRetries;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                lock (_publisherSync)
                {
                    if (!_publisherChannel.IsOpen)
                    {
                        throw new IOException("RabbitMQ publisher channel is closed.");
                    }

                    foreach (var request in batch)
                    {
                        var payload = request.RequestId.ToByteArray();
                        var props = _publisherChannel.CreateBasicProperties();
                        props.Persistent = _options.RabbitMq.Durable;

                        _publisherChannel.BasicPublish(
                            exchange: string.Empty,
                            routingKey: _options.RabbitMq.QueueName,
                            mandatory: false,
                            basicProperties: props,
                            body: payload);
                    }
                }

                return;
            }
            catch (Exception ex) when (attempt <= maxRetries)
            {
                _metrics.OnQueuePublishError();
                var delay = Math.Min(_options.RabbitMq.PublishRetryDelayMs * attempt, 5000);
                _logger.LogWarning(
                    ex,
                    "RabbitMQ publish retry {Attempt}/{MaxRetries} for batch size {BatchSize} after {DelayMs} ms",
                    attempt,
                    maxRetries,
                    batch.Count,
                    delay);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _metrics.OnQueuePublishError();
                _logger.LogError(ex, "RabbitMQ publish failed permanently for batch size {BatchSize}", batch.Count);
                throw;
            }
        }
    }

    private void DeclareQueue(IModel channel)
    {
        var rabbitOptions = _options.RabbitMq;
        channel.QueueDeclare(
            queue: rabbitOptions.QueueName,
            durable: rabbitOptions.Durable,
            exclusive: false,
            autoDelete: false,
            arguments: null);
    }
}
