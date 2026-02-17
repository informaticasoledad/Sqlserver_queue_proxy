using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace TDSQueue.Proxy.IntegrationTests;

public sealed class IntegrationDockerFixture : IAsyncLifetime
{
    private readonly string _composeFile;
    public bool IsAvailable { get; private set; } = true;
    public string? UnavailableReason { get; private set; }

    public IntegrationDockerFixture()
    {
        _composeFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../docker-compose.integration.yml"));
    }

    public async Task InitializeAsync()
    {
        if (!TryEnsureDockerComposeAvailable())
        {
            return;
        }

        try
        {
            await RunComposeAsync("up -d redis rabbitmq");
            await WaitForRedisAsync();
            await WaitForRabbitMqAsync();
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = ex.Message;
        }
    }

    public async Task DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        try
        {
            await RunComposeAsync("down -v");
        }
        catch
        {
            // Best effort cleanup for integration environments.
        }
    }

    public ProxyOptions BuildRedisOptions(string queueKey, string processingQueueKey)
    {
        return new ProxyOptions
        {
            QueueEngine = "Redis",
            Redis = new RedisQueueOptions
            {
                Configuration = "127.0.0.1:16379,abortConnect=false",
                QueueKey = queueKey,
                ProcessingQueueKey = processingQueueKey,
                PopBlockTimeoutSeconds = 1,
                EnqueueMaxRetries = 3,
                EnqueueRetryDelayMs = 50,
                ConsumeMaxRetries = 3,
                ConsumeRetryDelayMs = 50,
                RequeueOnFailure = true,
                AbortOnConnectFail = false
            }
        };
    }

    public ProxyOptions BuildRabbitOptions(string queueName)
    {
        return new ProxyOptions
        {
            QueueEngine = "RabbitMq",
            ChannelCapacity = 1000,
            RabbitMq = new RabbitMqOptions
            {
                HostName = "127.0.0.1",
                Port = 15673,
                UserName = "guest",
                Password = "guest",
                VirtualHost = "/",
                QueueName = queueName,
                Durable = false,
                PrefetchCount = 20,
                PublishBatchSize = 8,
                PublishBatchMaxDelayMs = 2,
                PublishMaxRetries = 5,
                PublishRetryDelayMs = 50,
                ConsumeMaxRetries = 5,
                ConsumeRetryDelayMs = 50,
                ConsumerChannelRestartDelayMs = 50,
                RequeueOnFailure = true
            }
        };
    }

    public async Task PurgeRabbitQueueAsync(string queueName)
    {
        var factory = new ConnectionFactory
        {
            HostName = "127.0.0.1",
            Port = 15673,
            UserName = "guest",
            Password = "guest",
            VirtualHost = "/"
        };

        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.QueueDeclare(queueName, durable: false, exclusive: false, autoDelete: false, arguments: null);
        channel.QueuePurge(queueName);
        await Task.CompletedTask;
    }

    public async Task PurgeRedisListsAsync(string queueKey, string processingQueueKey)
    {
        var mux = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:16379,abortConnect=false");
        try
        {
            var db = mux.GetDatabase();
            await db.KeyDeleteAsync(queueKey);
            await db.KeyDeleteAsync(processingQueueKey);
        }
        finally
        {
            mux.Dispose();
        }
    }

    private bool TryEnsureDockerComposeAvailable()
    {
        var result = RunProcess("docker", "compose version", timeoutMs: 15000);
        if (result.ExitCode != 0)
        {
            IsAvailable = false;
            UnavailableReason = "Docker Compose no está disponible para tests de integración.";
            return false;
        }

        return true;
    }

    private async Task WaitForRedisAsync()
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(60);
        Exception? last = null;

        while (DateTime.UtcNow < timeoutAt)
        {
            try
            {
                using var mux = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:16379,abortConnect=false");
                var db = mux.GetDatabase();
                var pong = await db.PingAsync();
                if (pong >= TimeSpan.Zero)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Redis no respondió a tiempo: {last?.Message}");
    }

    private async Task WaitForRabbitMqAsync()
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(90);
        Exception? last = null;

        while (DateTime.UtcNow < timeoutAt)
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = "127.0.0.1",
                    Port = 15673,
                    UserName = "guest",
                    Password = "guest",
                    VirtualHost = "/"
                };

                using var connection = factory.CreateConnection();
                using var channel = connection.CreateModel();
                channel.QueueDeclare("tdsqueue.it.probe", durable: false, exclusive: false, autoDelete: true, arguments: null);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(800);
        }

        throw new TimeoutException($"RabbitMQ no respondió a tiempo: {last?.Message}");
    }

    private Task RunComposeAsync(string args)
    {
        var escapedCompose = Quote(_composeFile);
        var fullArgs = $"compose -f {escapedCompose} {args}";
        var result = RunProcess("docker", fullArgs, timeoutMs: 120000);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {fullArgs} falló con código {result.ExitCode}. stdout: {result.StdOut}\nstderr: {result.StdErr}");
        }

        return Task.CompletedTask;
    }

    private static string Quote(string value)
    {
        if (!value.Contains(' '))
        {
            return value;
        }

        return $"\"{value}\"";
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(string fileName, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo arrancar el proceso: {fileName} {args}");
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            throw new TimeoutException($"Timeout ejecutando comando: {fileName} {args}");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        return (process.ExitCode, stdout, stderr);
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationDockerFixture>
{
    public const string Name = "integration-docker";
}

public static class TestHelpers
{
    public static async Task<QueueDelivery> ReadSingleAsync(IProxyQueueEngine queue, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var delivery in queue.ReadAllAsync(cts.Token))
        {
            return delivery;
        }

        throw new TimeoutException("No se recibió delivery a tiempo.");
    }

    public static ILoggerFactory LoggerFactory() => NullLoggerFactory.Instance;
}
