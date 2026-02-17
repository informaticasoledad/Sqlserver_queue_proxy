using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TDSQueue.Proxy;
using TDSQueue.Proxy.Health;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOptions<ProxyOptions>()
    .Bind(builder.Configuration.GetSection(ProxyOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.MaxInFlightRequests >= options.MinInFlightRequests, "MaxInFlightRequests must be >= MinInFlightRequests")
    .Validate(options => options.QueueHighWatermark >= options.QueueLowWatermark, "QueueHighWatermark must be >= QueueLowWatermark")
    .Validate(options => !options.Tls.Enabled || !string.IsNullOrWhiteSpace(options.Tls.CertificatePath),
        "Proxy:Tls:CertificatePath is required when Proxy:Tls:Enabled=true")
    .Validate(options => !options.ForceQueueOnly || !options.EnableMarsPassthrough,
        "Proxy:ForceQueueOnly=true is incompatible with Proxy:EnableMarsPassthrough=true")
    .ValidateOnStart();

builder.Services
    .AddOptions<ObservabilityOptions>()
    .Bind(builder.Configuration.GetSection(ObservabilityOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var observability = builder.Configuration
    .GetSection(ObservabilityOptions.SectionName)
    .Get<ObservabilityOptions>() ?? new ObservabilityOptions();
var otlpEndpoint = string.IsNullOrWhiteSpace(observability.OtlpEndpoint)
    ? null
    : SecretResolver.Resolve(observability.OtlpEndpoint);

builder.Services
    .AddHealthChecks()
    .AddCheck<QueueEngineHealthCheck>("queue-engine", failureStatus: HealthStatus.Unhealthy)
    .AddCheck<TargetHealthCheck>("target-circuit", failureStatus: HealthStatus.Degraded);

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("TDSQueue.Proxy");

        if (observability.EnableOtlpExporter && !string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
        }
    })
    .WithTracing(tracing =>
    {
        tracing.AddSource(ProxyDiagnostics.ActivitySourceName);

        if (observability.EnableOtlpExporter && !string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
        }
    });

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ProxyOptions>>().Value);
builder.Services.AddSingleton<PendingRequestStore>();
builder.Services.AddSingleton<ReplayGuard>();
builder.Services.AddSingleton<ProxyMetrics>();
builder.Services.AddSingleton<RuntimeTuningState>();

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<ProxyOptions>();
    return new AdaptiveConcurrencyLimiter(
        options.MinInFlightRequests,
        options.MaxInFlightRequests,
        Math.Min(options.WorkerCount, options.MaxInFlightRequests));
});

builder.Services.AddSingleton<TargetConnectionManager>();
builder.Services.AddSingleton<FaultInjector>();
builder.Services.AddSingleton<ClientAccessPolicy>();
builder.Services.AddSingleton<TlsTerminationService>();
builder.Services.AddSingleton<TdsPreLoginNegotiationService>();

builder.Services.AddSingleton<IProxyQueueEngine>(sp =>
{
    var options = sp.GetRequiredService<ProxyOptions>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var metrics = sp.GetRequiredService<ProxyMetrics>();

    if (options.QueueEngine.Equals("RabbitMq", StringComparison.OrdinalIgnoreCase))
    {
        return (IProxyQueueEngine)new RabbitMqProxyQueueEngine(
            options,
            metrics,
            loggerFactory.CreateLogger<RabbitMqProxyQueueEngine>());
    }

    if (options.QueueEngine.Equals("Redis", StringComparison.OrdinalIgnoreCase))
    {
        return (IProxyQueueEngine)new RedisProxyQueueEngine(
            options,
            metrics,
            loggerFactory.CreateLogger<RedisProxyQueueEngine>());
    }

    if (!options.QueueEngine.Equals("Channel", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Unsupported Proxy:QueueEngine '{options.QueueEngine}'. Use 'Channel', 'RabbitMq' or 'Redis'.");
    }

    var boundedOptions = new BoundedChannelOptions(options.ChannelCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    };

    var channel = Channel.CreateBounded<QueuedRequest>(boundedOptions);
    return (IProxyQueueEngine)new ChannelProxyQueueEngine(channel);
});

builder.Services.AddHostedService<TdsProxyListenerService>();
builder.Services.AddHostedService<TdsProxyWorkerService>();
builder.Services.AddHostedService<ProxyMetricsReporterService>();
builder.Services.AddHostedService<PendingRequestJanitorService>();
builder.Services.AddHostedService<HealthEndpointService>();

await builder.Build().RunAsync();
