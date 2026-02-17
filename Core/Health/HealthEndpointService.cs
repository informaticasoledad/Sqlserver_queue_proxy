using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TDSQueue.Proxy.Health;

public sealed class HealthEndpointService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ObservabilityOptions _observability;
    private readonly RuntimeTuningState _runtimeTuning;
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly ProxyMetrics _metrics;
    private readonly HealthCheckService _healthCheckService;
    private readonly ILogger<HealthEndpointService> _logger;

    public HealthEndpointService(
        IOptions<ObservabilityOptions> observability,
        RuntimeTuningState runtimeTuning,
        AdaptiveConcurrencyLimiter limiter,
        ProxyMetrics metrics,
        HealthCheckService healthCheckService,
        ILogger<HealthEndpointService> logger)
    {
        _observability = observability.Value;
        _runtimeTuning = runtimeTuning;
        _limiter = limiter;
        _metrics = metrics;
        _healthCheckService = healthCheckService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{ToHttpListenerHost(_observability.ListenAddress)}:{_observability.HealthPort}/");
        listener.Start();

        _logger.LogInformation("Health/Admin endpoint listening on port {Port}", _observability.HealthPort);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
                _ = HandleAsync(context, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/health/live", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, 200, new { status = "Healthy" }, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (path.Equals("/health/ready", StringComparison.OrdinalIgnoreCase))
            {
                var report = await _healthCheckService.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                var code = report.Status switch
                {
                    HealthStatus.Healthy => 200,
                    HealthStatus.Degraded => 200,
                    _ => 503
                };

                await WriteJsonAsync(context.Response, code, new { status = report.Status.ToString() }, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (path.Equals("/admin/state", StringComparison.OrdinalIgnoreCase))
            {
                if (!AuthorizeAdmin(context.Request))
                {
                    await WriteJsonAsync(context.Response, 401, new { error = "Unauthorized" }, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var state = new
                {
                    limiter = new { _limiter.MinLimit, _limiter.MaxLimit, _limiter.CurrentLimit },
                    tuning = _runtimeTuning.Snapshot(),
                    metrics = _metrics.GetSnapshot()
                };

                await WriteJsonAsync(context.Response, 200, state, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/admin/tuning", StringComparison.OrdinalIgnoreCase) &&
                context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                if (!AuthorizeAdmin(context.Request))
                {
                    await WriteJsonAsync(context.Response, 401, new { error = "Unauthorized" }, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var update = await ReadBodyAsync<RuntimeTuningUpdate>(context.Request, cancellationToken).ConfigureAwait(false)
                    ?? new RuntimeTuningUpdate();

                _runtimeTuning.Update(update);
                await WriteJsonAsync(context.Response, 200, new { status = "Updated", tuning = _runtimeTuning.Snapshot() }, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (path.Equals("/admin/limiter", StringComparison.OrdinalIgnoreCase) &&
                context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                if (!AuthorizeAdmin(context.Request))
                {
                    await WriteJsonAsync(context.Response, 401, new { error = "Unauthorized" }, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var update = await ReadBodyAsync<LimiterUpdate>(context.Request, cancellationToken).ConfigureAwait(false)
                    ?? new LimiterUpdate();

                _limiter.Reconfigure(update.MinLimit, update.MaxLimit, update.CurrentLimit);
                await WriteJsonAsync(
                        context.Response,
                        200,
                        new
                        {
                            status = "Updated",
                            limiter = new { _limiter.MinLimit, _limiter.MaxLimit, _limiter.CurrentLimit }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context.Response, 404, new { error = "Not Found" }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Health/Admin endpoint request handling warning.");
            if (context.Response.OutputStream.CanWrite)
            {
                await WriteJsonAsync(context.Response, 500, new { status = "Error" }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private bool AuthorizeAdmin(HttpListenerRequest request)
    {
        if (!_observability.EnableAdminEndpoint)
        {
            return false;
        }

        if (!_observability.AllowRemoteAdmin && request.RemoteEndPoint is { } remote && !IPAddress.IsLoopback(remote.Address))
        {
            return false;
        }

        var configuredToken = SecretResolver.Resolve(_observability.AdminToken ?? string.Empty);
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return false;
        }

        var requestToken = request.Headers["X-Admin-Token"] ?? string.Empty;
        return FixedTimeTokenEquals(configuredToken, requestToken);
    }

    private static bool FixedTimeTokenEquals(string configuredToken, string requestToken)
    {
        var configuredBytes = Encoding.UTF8.GetBytes(configuredToken);
        var requestBytes = Encoding.UTF8.GetBytes(requestToken);
        return CryptographicOperations.FixedTimeEquals(configuredBytes, requestBytes);
    }

    private static string ToHttpListenerHost(string listenAddress)
    {
        if (string.IsNullOrWhiteSpace(listenAddress))
        {
            return "127.0.0.1";
        }

        return listenAddress switch
        {
            "0.0.0.0" => "*",
            "*" => "*",
            "+" => "+",
            _ => listenAddress
        };
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasEntityBody)
        {
            return default;
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        int statusCode,
        object body,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(body, JsonOptions);
        var data = Encoding.UTF8.GetBytes(payload);
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = data.LongLength;

        await response.OutputStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    public sealed class LimiterUpdate
    {
        public int? MinLimit { get; init; }
        public int? MaxLimit { get; init; }
        public int? CurrentLimit { get; init; }
    }
}
