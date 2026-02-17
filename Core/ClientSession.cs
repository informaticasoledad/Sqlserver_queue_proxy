using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TDSQueue.Proxy;

public sealed class ClientSession : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly IPEndPoint _targetEndpoint;
    private readonly TargetConnectionManager _targetConnectionManager;
    private TargetConnectionLease? _targetLease;

    public ClientSession(
        TcpClient client,
        IPEndPoint targetEndpoint,
        TargetConnectionManager targetConnectionManager,
        ILogger logger)
    {
        SessionId = Guid.NewGuid();
        Client = client;
        _targetEndpoint = targetEndpoint;
        _targetConnectionManager = targetConnectionManager;
        _logger = logger;

        ClientTransportStream = client.GetStream();
        ClientReader = PipeReader.Create(ClientTransportStream, new StreamPipeReaderOptions(leaveOpen: true));
        ClientWriter = PipeWriter.Create(ClientTransportStream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public Guid SessionId { get; }
    public TcpClient Client { get; }

    public Stream ClientTransportStream { get; private set; }
    public PipeReader ClientReader { get; private set; }
    public PipeWriter ClientWriter { get; private set; }
    public bool OpaqueEncryptedQueueMode { get; set; }

    public Stream? TargetTransportStream { get; private set; }
    public PipeReader? TargetReader { get; private set; }
    public PipeWriter? TargetWriter { get; private set; }

    // Serializes request/response pairs for one target connection.
    public SemaphoreSlim TargetRequestLock { get; } = new(1, 1);
    private readonly ConcurrentQueue<ProxyContext> _awaitingResponses = new();

    public async ValueTask EnsureTargetConnectedAsync(CancellationToken cancellationToken)
    {
        if (_targetLease is { } lease && IsTargetConnectionAlive(lease.Client))
        {
            return;
        }

        InvalidateTargetConnection();
        var newLease = await _targetConnectionManager.ConnectAsync(_targetEndpoint, cancellationToken)
            .ConfigureAwait(false);

        _targetLease = newLease;
        TargetTransportStream = newLease.Client.GetStream();
        TargetReader = PipeReader.Create(TargetTransportStream, new StreamPipeReaderOptions(leaveOpen: true));
        TargetWriter = PipeWriter.Create(TargetTransportStream, new StreamPipeWriterOptions(leaveOpen: true));

        _logger.LogInformation(
            "Session {SessionId} connected to target {TargetEndpoint}",
            SessionId,
            _targetEndpoint);
    }

    public async ValueTask UpgradeClientTransportAsync(Stream upgradedStream)
    {
        await ClientReader.CompleteAsync().ConfigureAwait(false);
        await ClientWriter.CompleteAsync().ConfigureAwait(false);

        ClientTransportStream = upgradedStream;
        ClientReader = PipeReader.Create(upgradedStream, new StreamPipeReaderOptions(leaveOpen: true));
        ClientWriter = PipeWriter.Create(upgradedStream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public async ValueTask UpgradeTargetTransportAsync(Stream upgradedStream)
    {
        if (TargetReader is null || TargetWriter is null)
        {
            throw new InvalidOperationException("Target transport is not initialized.");
        }

        await TargetReader.CompleteAsync().ConfigureAwait(false);
        await TargetWriter.CompleteAsync().ConfigureAwait(false);

        TargetTransportStream = upgradedStream;
        TargetReader = PipeReader.Create(upgradedStream, new StreamPipeReaderOptions(leaveOpen: true));
        TargetWriter = PipeWriter.Create(upgradedStream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public void EnqueueAwaitingResponse(ProxyContext context) => _awaitingResponses.Enqueue(context);

    public bool TryDequeueAwaitingResponse(out ProxyContext? context) =>
        _awaitingResponses.TryDequeue(out context);

    public void InvalidateTargetConnection()
    {
        if (TargetReader is not null)
        {
            try { TargetReader.Complete(); } catch { }
            TargetReader = null;
        }

        if (TargetWriter is not null)
        {
            try { TargetWriter.Complete(); } catch { }
            TargetWriter = null;
        }

        try
        {
            TargetTransportStream?.Dispose();
        }
        catch { }
        TargetTransportStream = null;

        if (_targetLease is { } lease)
        {
            lease.Dispose();
            _targetLease = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ClientReader.CompleteAsync().ConfigureAwait(false);
            await ClientWriter.CompleteAsync().ConfigureAwait(false);

            if (TargetReader is not null)
            {
                await TargetReader.CompleteAsync().ConfigureAwait(false);
            }

            if (TargetWriter is not null)
            {
                await TargetWriter.CompleteAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session {SessionId}: pipe completion warning.", SessionId);
        }

        TargetRequestLock.Dispose();

        try
        {
            if (ClientTransportStream is IAsyncDisposable clientAsync)
            {
                await clientAsync.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                ClientTransportStream.Dispose();
            }

            if (TargetTransportStream is IAsyncDisposable targetAsync)
            {
                await targetAsync.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                TargetTransportStream?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session {SessionId}: stream disposal warning.", SessionId);
        }

        if (_targetLease is { } lease)
        {
            lease.Dispose();
            _targetLease = null;
        }

        Client.Dispose();
    }

    private static bool IsTargetConnectionAlive(TcpClient client)
    {
        try
        {
            var socket = client.Client;
            return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch
        {
            return false;
        }
    }
}
