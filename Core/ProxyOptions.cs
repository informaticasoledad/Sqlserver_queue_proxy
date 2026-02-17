using System.ComponentModel.DataAnnotations;

namespace TDSQueue.Proxy;

public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    [Range(1, 65535)]
    public int ListeningPort { get; init; } = 14330;

    [Range(1, 4096)]
    public int WorkerCount { get; init; } = 10;

    [Range(1, 200000)]
    public int ChannelCapacity { get; init; } = 1000;

    [Range(1024, 16777216)]
    public int MaxMessageBytes { get; init; } = 1048576;

    [Range(1, 3600)]
    public int MetricsReportIntervalSeconds { get; init; } = 10;

    // Supported values: Channel | RabbitMq | Redis
    public string QueueEngine { get; init; } = "Channel";

    // Enables transparent full-duplex tunneling per session.
    // Use this when clients require explicit MARS behavior.
    public bool EnableMarsPassthrough { get; init; }
    public bool ForceQueueOnly { get; init; }
    public bool FallbackToPassthroughOnEncryptOff { get; init; } = true;

    public bool EnforceClientAllowlist { get; init; }
    public string[] AllowedClientCidrs { get; init; } = Array.Empty<string>();

    [Required]
    public string TargetHost { get; init; } = "127.0.0.1";

    [Range(1, 65535)]
    public int TargetPort { get; init; } = 1433;

    [Range(1, 128)]
    public int TargetConnectMaxRetries { get; init; } = 5;

    [Range(1, 60000)]
    public int TargetConnectRetryDelayMs { get; init; } = 200;

    [Range(1, 1000)]
    public int CircuitBreakerFailuresThreshold { get; init; } = 20;

    [Range(1, 3600)]
    public int CircuitBreakerOpenSeconds { get; init; } = 30;

    [Range(1, 100000)]
    public int MaxTargetConnections { get; init; } = 2000;

    [Range(1, 8192)]
    public int MaxInFlightRequests { get; init; } = 256;

    [Range(1, 8192)]
    public int MinInFlightRequests { get; init; } = 16;

    public bool AdaptiveBackpressureEnabled { get; init; } = true;

    [Range(1, 1000000)]
    public int QueueHighWatermark { get; init; } = 500;

    [Range(1, 1000000)]
    public int QueueLowWatermark { get; init; } = 100;

    [Range(1, 300000)]
    public int TargetLatencyHighMs { get; init; } = 800;

    [Range(1, 300000)]
    public int TargetLatencyLowMs { get; init; } = 120;

    [Range(1, 128)]
    public int RequestProcessingRetries { get; init; } = 2;

    [Range(1, 60000)]
    public int RequestRetryDelayMs { get; init; } = 50;

    [Range(1, 100)]
    public int MaxReplayAttempts { get; init; } = 3;

    [Range(1, 1800)]
    public int PendingRequestTtlSeconds { get; init; } = 120;

    public TlsTerminationOptions Tls { get; init; } = new();
    public RabbitMqOptions RabbitMq { get; init; } = new();
    public RedisQueueOptions Redis { get; init; } = new();
    public FaultInjectionOptions FaultInjection { get; init; } = new();
}

public sealed class TlsTerminationOptions
{
    public bool Enabled { get; init; }

    public string? CertificatePath { get; init; }
    public string? CertificatePassword { get; init; }

    public bool ClientCertificateRequired { get; init; }
    public bool CheckCertificateRevocation { get; init; } = true;
    public bool TrustTargetServerCertificate { get; init; }
    public string? TargetServerName { get; init; }

    [Range(1, 120)]
    public int HandshakeTimeoutSeconds { get; init; } = 20;
}

public sealed class RabbitMqOptions
{
    [Required]
    public string HostName { get; init; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; init; } = 5672;

    [Required]
    public string UserName { get; init; } = "guest";

    [Required]
    public string Password { get; init; } = "guest";

    [Required]
    public string VirtualHost { get; init; } = "/";

    [Required]
    public string QueueName { get; init; } = "tdsqueue.proxy.requests";

    public bool Durable { get; init; } = true;

    [Range(1, 8192)]
    public ushort PrefetchCount { get; init; } = 100;

    [Range(1, 1000)]
    public int PublishBatchSize { get; init; } = 64;

    [Range(1, 10000)]
    public int PublishBatchMaxDelayMs { get; init; } = 5;

    [Range(1, 128)]
    public int PublishMaxRetries { get; init; } = 5;

    [Range(1, 60000)]
    public int PublishRetryDelayMs { get; init; } = 100;

    [Range(1, 128)]
    public int ConsumeMaxRetries { get; init; } = 5;

    [Range(1, 60000)]
    public int ConsumeRetryDelayMs { get; init; } = 200;

    [Range(1, 600000)]
    public int ConsumerChannelRestartDelayMs { get; init; } = 500;

    public bool RequeueOnFailure { get; init; } = true;

    public bool UseTls { get; init; }
    public string? TlsServerName { get; init; }
    public bool TlsAcceptInvalidCertificates { get; init; }
}

public sealed class RedisQueueOptions
{
    [Required]
    public string Configuration { get; init; } = "localhost:6379";

    [Required]
    public string QueueKey { get; init; } = "tdsqueue:requests";

    [Required]
    public string ProcessingQueueKey { get; init; } = "tdsqueue:requests:processing";

    [Range(1, 60)]
    public int PopBlockTimeoutSeconds { get; init; } = 1;

    [Range(1, 128)]
    public int EnqueueMaxRetries { get; init; } = 5;

    [Range(1, 60000)]
    public int EnqueueRetryDelayMs { get; init; } = 100;

    [Range(1, 128)]
    public int ConsumeMaxRetries { get; init; } = 5;

    [Range(1, 60000)]
    public int ConsumeRetryDelayMs { get; init; } = 200;

    public bool RequeueOnFailure { get; init; } = true;

    public bool UseTls { get; init; }
    public string? SslHost { get; init; }
    public bool AbortOnConnectFail { get; init; } = false;
}

public sealed class FaultInjectionOptions
{
    public bool Enabled { get; init; }

    [Range(0, 1)]
    public double ThrowProbability { get; init; }

    [Range(0, 1)]
    public double DelayProbability { get; init; }

    [Range(0, 60000)]
    public int DelayMs { get; init; } = 50;
}
