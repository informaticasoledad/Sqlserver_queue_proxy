using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TDSQueue.Proxy;

public sealed class TdsProxyListenerService : BackgroundService
{
    private readonly ProxyOptions _options;
    private readonly IProxyQueueEngine _queue;
    private readonly PendingRequestStore _pending;
    private readonly TargetConnectionManager _targetConnectionManager;
    private readonly TdsPreLoginNegotiationService _preLoginNegotiation;
    private readonly ClientAccessPolicy _accessPolicy;
    private readonly ProxyMetrics _metrics;
    private readonly ILogger<TdsProxyListenerService> _logger;
    private readonly IPEndPoint _targetEndpoint;

    public TdsProxyListenerService(
        IOptions<ProxyOptions> options,
        IProxyQueueEngine queue,
        PendingRequestStore pending,
        TargetConnectionManager targetConnectionManager,
        TdsPreLoginNegotiationService preLoginNegotiation,
        ClientAccessPolicy accessPolicy,
        ProxyMetrics metrics,
        ILogger<TdsProxyListenerService> logger)
    {
        _options = options.Value;
        _queue = queue;
        _pending = pending;
        _targetConnectionManager = targetConnectionManager;
        _preLoginNegotiation = preLoginNegotiation;
        _accessPolicy = accessPolicy;
        _metrics = metrics;
        _logger = logger;
        _targetEndpoint = TargetEndpointResolver.Resolve(_options);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, _options.ListeningPort);
        listener.Start();

        _logger.LogInformation(
            "TDS proxy listening on port {Port}. Target: {TargetEndpoint}. QueueEngine: {QueueEngine}. MARS passthrough: {MarsPassthrough}. ForceQueueOnly: {ForceQueueOnly}",
            _options.ListeningPort,
            _targetEndpoint,
            _options.QueueEngine,
            _options.EnableMarsPassthrough,
            _options.ForceQueueOnly);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                if (!IsClientAllowed(client))
                {
                    client.Dispose();
                    continue;
                }

                _ = Task.Run(() => HandleClientSessionAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Listener canceled.");
        }
        finally
        {
            listener.Stop();
            _queue.TryComplete();
        }
    }

    private bool IsClientAllowed(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remoteEndpoint)
        {
            return false;
        }

        if (_accessPolicy.IsAllowed(remoteEndpoint.Address))
        {
            return true;
        }

        _logger.LogWarning("Rejected client from {RemoteAddress} by allowlist policy.", remoteEndpoint.Address);
        return false;
    }

    private async Task HandleClientSessionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var session = new ClientSession(client, _targetEndpoint, _targetConnectionManager, _logger);
        _metrics.OnSessionOpened();
        CancellationTokenSource? serverPumpCts = null;
        Task? serverPumpTask = null;

        _logger.LogInformation("Accepted client session {SessionId}", session.SessionId);

        try
        {
            var marsPassthroughEnabled = _options.EnableMarsPassthrough && !_options.ForceQueueOnly;
            var forcePassthrough = false;
            if (!marsPassthroughEnabled)
            {
                var mode = await _preLoginNegotiation.NegotiateAsync(session, cancellationToken).ConfigureAwait(false);
                forcePassthrough = mode == PreLoginNegotiationMode.Passthrough;
            }

            if (marsPassthroughEnabled || forcePassthrough)
            {
                await HandleMarsPassthroughAsync(session, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (session.OpaqueEncryptedQueueMode)
            {
                await HandleOpaqueEncryptedQueueAsync(session, cancellationToken).ConfigureAwait(false);
                return;
            }

            await session.EnsureTargetConnectedAsync(cancellationToken).ConfigureAwait(false);
            serverPumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            serverPumpTask = Task.Run(
                () => PumpTargetToClientAsync(session, serverPumpCts.Token),
                serverPumpCts.Token);

            while (!cancellationToken.IsCancellationRequested)
            {
                using var activity = ProxyDiagnostics.ActivitySource.StartActivity("proxy.listener.request");
                activity?.SetTag("session.id", session.SessionId);

                var readStarted = Stopwatch.GetTimestamp();
                var requestBuffer = await TdsFraming.ReadMessageAsync(
                        session.ClientReader,
                        _options.MaxMessageBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                _metrics.RecordStageLatency("read_client", Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds);

                if (requestBuffer is null)
                {
                    break;
                }

                var context = new ProxyContext(session, requestBuffer);
                activity?.SetTag("request.id", context.RequestId);

                try
                {
                    _pending.Add(context);
                    _metrics.OnPendingAdded();

                    _metrics.OnRequestEnqueued();
                    var enqueueStarted = Stopwatch.GetTimestamp();
                    await _queue.EnqueueAsync(new QueuedRequest(context.RequestId), cancellationToken).ConfigureAwait(false);
                    _metrics.RecordStageLatency("enqueue", Stopwatch.GetElapsedTime(enqueueStarted).TotalMilliseconds);

                    var waitResponseStarted = Stopwatch.GetTimestamp();
                    var responseBytes = await context.ResponseSource.Task.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    _metrics.RecordStageLatency("wait_response", Stopwatch.GetElapsedTime(waitResponseStarted).TotalMilliseconds);

                    if (responseBytes.Length > 0)
                    {
                        var writeStarted = Stopwatch.GetTimestamp();
                        await session.ClientWriter.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
                        await session.ClientWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                        _metrics.RecordStageLatency("write_client", Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds);
                    }
                }
                finally
                {
                    if (_pending.TryRemove(context.RequestId))
                    {
                        _metrics.OnPendingRemoved();
                    }

                    context.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Session {SessionId} canceled.", session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId} failed", session.SessionId);
        }
        finally
        {
            if (serverPumpCts is not null)
            {
                serverPumpCts.Cancel();
            }

            if (serverPumpTask is not null)
            {
                try
                {
                    await serverPumpTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Session {SessionId}: target->client pump failed.", session.SessionId);
                }
            }

            serverPumpCts?.Dispose();
            _metrics.OnSessionClosed();
            _logger.LogInformation("Session {SessionId} closed.", session.SessionId);
        }
    }

    private async Task HandleMarsPassthroughAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.EnsureTargetConnectedAsync(cancellationToken).ConfigureAwait(false);

        var targetReader = session.TargetReader
            ?? throw new InvalidOperationException("Target reader not initialized.");

        var targetWriter = session.TargetWriter
            ?? throw new InvalidOperationException("Target writer not initialized.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = linkedCts.Token;

        var clientToTarget = PumpAsync(
            session.ClientReader,
            targetWriter,
            linkedToken,
            session.SessionId,
            "client->target");

        var targetToClient = PumpAsync(
            targetReader,
            session.ClientWriter,
            linkedToken,
            session.SessionId,
            "target->client");

        var completed = await Task.WhenAny(clientToTarget, targetToClient).ConfigureAwait(false);
        linkedCts.Cancel();

        try
        {
            await Task.WhenAll(clientToTarget, targetToClient).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (completed.IsCompleted)
        {
            // Expected while shutting down opposite direction after one side closes.
        }
    }

    private async Task HandleOpaqueEncryptedQueueAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.EnsureTargetConnectedAsync(cancellationToken).ConfigureAwait(false);

        var targetReader = session.TargetReader
            ?? throw new InvalidOperationException("Target reader not initialized.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = linkedCts.Token;

        var targetToClient = PumpAsync(
            targetReader,
            session.ClientWriter,
            linkedToken,
            session.SessionId,
            "target->client-opaque");

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                var readStarted = Stopwatch.GetTimestamp();
                var readResult = await session.ClientReader.ReadAsync(linkedToken).ConfigureAwait(false);
                var buffer = readResult.Buffer;
                _metrics.RecordStageLatency("read_client", Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds);

                if (!buffer.IsEmpty)
                {
                    var chunkCapacity = Math.Max(1024, (int)Math.Min(buffer.Length, 65536));
                    var chunkBuffer = new PooledMessageBuffer(chunkCapacity);
                    chunkBuffer.Append(buffer);
                    var context = new ProxyContext(session, chunkBuffer);

                    try
                    {
                        _pending.Add(context);
                        _metrics.OnPendingAdded();
                        _metrics.OnRequestEnqueued();

                        var enqueueStarted = Stopwatch.GetTimestamp();
                        await _queue.EnqueueAsync(new QueuedRequest(context.RequestId), linkedToken).ConfigureAwait(false);
                        _metrics.RecordStageLatency("enqueue", Stopwatch.GetElapsedTime(enqueueStarted).TotalMilliseconds);

                        var waitStarted = Stopwatch.GetTimestamp();
                        await context.ResponseSource.Task.WaitAsync(linkedToken).ConfigureAwait(false);
                        _metrics.RecordStageLatency("wait_response", Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds);
                    }
                    finally
                    {
                        if (_pending.TryRemove(context.RequestId))
                        {
                            _metrics.OnPendingRemoved();
                        }

                        context.Dispose();
                    }
                }

                session.ClientReader.AdvanceTo(buffer.End);

                if (readResult.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            linkedCts.Cancel();
            try
            {
                await targetToClient.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected while stopping linked pumps.
            }
        }
    }

    private async Task PumpAsync(
        PipeReader reader,
        PipeWriter writer,
        CancellationToken cancellationToken,
        Guid sessionId,
        string direction)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (!buffer.IsEmpty)
            {
                await WriteBufferAsync(writer, buffer, cancellationToken).ConfigureAwait(false);
                var flushResult = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flushResult.IsCompleted)
                {
                    break;
                }
            }

            reader.AdvanceTo(buffer.End);

            if (result.IsCompleted)
            {
                _logger.LogDebug("Session {SessionId}: pump {Direction} reached end of stream.", sessionId, direction);
                break;
            }
        }
    }

    private async Task PumpTargetToClientAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var targetReader = session.TargetReader
            ?? throw new InvalidOperationException("Target reader not initialized.");
        var forwarded = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var responseBuffer = await TdsFraming.ReadMessageAsync(
                    targetReader,
                    _options.MaxMessageBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            if (responseBuffer is null)
            {
                return;
            }

            byte[] responseBytes;
            using (responseBuffer)
            {
                responseBytes = responseBuffer.DetachToArray();
            }

            forwarded++;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var packetType = responseBytes.Length > 0 ? responseBytes[0] : (byte)0;
                _logger.LogDebug(
                    "Session {SessionId}: target->client message #{MessageNumber} type=0x{PacketType:X2} bytes={Bytes}",
                    session.SessionId,
                    forwarded,
                    packetType,
                    responseBytes.Length);
            }

            var writeStarted = Stopwatch.GetTimestamp();
            await session.ClientWriter.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
            await session.ClientWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            _metrics.RecordStageLatency("write_client", Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds);

            if (session.TryDequeueAwaitingResponse(out var waitingContext) && waitingContext is not null)
            {
                waitingContext.ResponseSource.TrySetResult(Array.Empty<byte>());
            }
            else if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Session {SessionId}: unsolicited target message type=0x{PacketType:X2} bytes={Bytes}",
                    session.SessionId,
                    responseBytes.Length > 0 ? responseBytes[0] : (byte)0,
                    responseBytes.Length);
            }
        }
    }

    private static async ValueTask WriteBufferAsync(
        PipeWriter writer,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (buffer.IsSingleSegment)
        {
            await writer.WriteAsync(buffer.First, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var segment in buffer)
        {
            await writer.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
        }
    }
}
