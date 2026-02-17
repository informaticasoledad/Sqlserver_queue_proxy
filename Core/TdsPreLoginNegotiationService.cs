using System.IO.Pipelines;
using System.Security.Authentication;

namespace TDSQueue.Proxy;

public enum PreLoginNegotiationMode
{
    QueuePipeline = 0,
    Passthrough = 1
}

public sealed class TdsPreLoginNegotiationService
{
    private readonly ProxyOptions _options;
    private readonly TlsTerminationService _tls;
    private readonly ILogger<TdsPreLoginNegotiationService> _logger;

    public TdsPreLoginNegotiationService(
        ProxyOptions options,
        TlsTerminationService tls,
        ILogger<TdsPreLoginNegotiationService> logger)
    {
        _options = options;
        _tls = tls;
        _logger = logger;
    }

    public async Task<PreLoginNegotiationMode> NegotiateAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.EnsureTargetConnectedAsync(cancellationToken).ConfigureAwait(false);

        var clientPreLogin = await TdsFraming.ReadMessageAsync(
                session.ClientReader,
                _options.MaxMessageBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (clientPreLogin is null)
        {
            throw new IOException("Client closed connection before PreLogin.");
        }

        byte[] clientPreLoginBytes;
        using (clientPreLogin)
        {
            clientPreLoginBytes = clientPreLogin.DetachToArray();
        }

        if (!TdsPreLoginParser.IsPreLoginMessage(clientPreLoginBytes))
        {
            throw new InvalidOperationException("Expected TDS PreLogin packet from client.");
        }

        var targetWriter = session.TargetWriter
            ?? throw new InvalidOperationException("Target writer not initialized.");

        var targetReader = session.TargetReader
            ?? throw new InvalidOperationException("Target reader not initialized.");

        await targetWriter.WriteAsync(clientPreLoginBytes, cancellationToken).ConfigureAwait(false);
        await targetWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

        var targetPreLogin = await TdsFraming.ReadMessageAsync(
                targetReader,
                _options.MaxMessageBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (targetPreLogin is null)
        {
            throw new IOException("Target closed connection during PreLogin response.");
        }

        byte[] targetPreLoginBytes;
        using (targetPreLogin)
        {
            targetPreLoginBytes = targetPreLogin.DetachToArray();
        }

        await session.ClientWriter.WriteAsync(targetPreLoginBytes, cancellationToken).ConfigureAwait(false);
        await session.ClientWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (!TdsPreLoginParser.TryGetEncryptionOption(clientPreLoginBytes, out var clientEncryption) ||
            !TdsPreLoginParser.TryGetEncryptionOption(targetPreLoginBytes, out var serverEncryption))
        {
            _logger.LogDebug("PreLogin encryption token was not detected. Skipping TLS upgrade.");
            return PreLoginNegotiationMode.QueuePipeline;
        }

        _logger.LogDebug(
            "PreLogin encryption options: client=0x{ClientEncryption:X2}, server=0x{ServerEncryption:X2}",
            clientEncryption,
            serverEncryption);

        if (_options.ForceQueueOnly)
        {
            session.OpaqueEncryptedQueueMode = true;
            _logger.LogInformation(
                "Session {SessionId}: ForceQueueOnly=true, using opaque encrypted queue mode (no TLS termination in proxy).",
                session.SessionId);
            return PreLoginNegotiationMode.QueuePipeline;
        }

        if (!TdsPreLoginParser.ShouldUpgradeTls(clientEncryption, serverEncryption))
        {
            _logger.LogDebug("PreLogin negotiated non-TLS session.");
            return PreLoginNegotiationMode.QueuePipeline;
        }

        // ENCRYPT_OFF (0x00) is often "encrypt login only" and can behave differently across clients.
        // If both sides advertise 0x00, prefer transparent passthrough for this session.
        if (clientEncryption == 0x00 && serverEncryption == 0x00)
        {
            if (_options.ForceQueueOnly || !_options.FallbackToPassthroughOnEncryptOff)
            {
                throw new InvalidOperationException(
                    "PreLogin negotiated ENCRYPT_OFF (0x00/0x00). Queue-only policy is active " +
                    "(Proxy:ForceQueueOnly=true or Proxy:FallbackToPassthroughOnEncryptOff=false). " +
                    "Force Encrypt=True/mandatory TLS end-to-end, or allow fallback passthrough.");
            }

            _logger.LogInformation(
                "Session {SessionId}: PreLogin negotiated ENCRYPT_OFF (0x00/0x00). Falling back to passthrough mode for compatibility.",
                session.SessionId);
            return PreLoginNegotiationMode.Passthrough;
        }

        if (!_options.Tls.Enabled)
        {
            throw new InvalidOperationException(
                "Target/client negotiated TLS during PreLogin but Proxy:Tls:Enabled=false. Enable TLS termination or use passthrough.");
        }

        var clientPrefetched = DrainPrefetchedBytes(session.ClientReader);
        Stream clientBaseStream = clientPrefetched.Length > 0
            ? new PrefixedStream(session.ClientTransportStream, clientPrefetched)
            : session.ClientTransportStream;
        var clientTlsStream = await WrapTlsTransportAsync(
                clientBaseStream,
                "client",
                session.SessionId,
                canProbe: true,
                initialProbe: clientPrefetched.Length > 0 ? clientPrefetched[0] : null,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug("Session {SessionId}: upgrading client side to TLS.", session.SessionId);
        var upgradedClient = await _tls.UpgradeClientSideAsync(clientTlsStream, cancellationToken)
            .ConfigureAwait(false);

        var targetStream = session.TargetTransportStream
            ?? throw new InvalidOperationException("Target stream not initialized.");

        var targetPrefetched = DrainPrefetchedBytes(targetReader);
        Stream targetBaseStream = targetPrefetched.Length > 0
            ? new PrefixedStream(targetStream, targetPrefetched)
            : targetStream;
        var targetTlsStream = await WrapTlsTransportAsync(
                targetBaseStream,
                "target",
                session.SessionId,
                canProbe: false,
                initialProbe: targetPrefetched.Length > 0 ? targetPrefetched[0] : null,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug("Session {SessionId}: upgrading target side to TLS.", session.SessionId);
        var upgradedTarget = await _tls.UpgradeTargetSideAsync(targetTlsStream, _options.TargetHost, cancellationToken)
            .ConfigureAwait(false);

        await session.UpgradeClientTransportAsync(upgradedClient).ConfigureAwait(false);
        await session.UpgradeTargetTransportAsync(upgradedTarget).ConfigureAwait(false);

        _logger.LogInformation("Session {SessionId}: PreLogin-aware TLS upgrade completed.", session.SessionId);
        return PreLoginNegotiationMode.QueuePipeline;
    }

    private static byte[] DrainPrefetchedBytes(PipeReader reader)
    {
        if (!reader.TryRead(out var result))
        {
            return Array.Empty<byte>();
        }

        var buffer = result.Buffer;
        if (buffer.IsEmpty)
        {
            reader.AdvanceTo(buffer.Start, buffer.End);
            return Array.Empty<byte>();
        }

        var payload = new byte[checked((int)buffer.Length)];
        var written = 0;
        foreach (var segment in buffer)
        {
            segment.Span.CopyTo(payload.AsSpan(written));
            written += segment.Length;
        }
        reader.AdvanceTo(buffer.End);
        return payload;
    }

    private async Task<Stream> WrapTlsTransportAsync(
        Stream baseStream,
        string side,
        Guid sessionId,
        bool canProbe,
        byte? initialProbe,
        CancellationToken cancellationToken)
    {
        if (initialProbe is byte prefetchedFirstByte)
        {
            return WrapByFirstByte(baseStream, prefetchedFirstByte, side, sessionId);
        }

        if (!canProbe)
        {
            _logger.LogDebug(
                "Session {SessionId}: no buffered bytes on {Side} side; defaulting to TDS-encapsulated TLS framing.",
                sessionId,
                side);
            return new TdsTlsEncapsulationStream(baseStream);
        }

        var probe = new byte[1];
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(TimeSpan.FromSeconds(Math.Min(5, _options.Tls.HandshakeTimeoutSeconds)));

        int read;
        try
        {
            read = await baseStream.ReadAsync(probe.AsMemory(0, 1), probeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthenticationException($"Timed out probing first TLS byte for {side} side.");
        }

        if (read == 0)
        {
            throw new IOException($"Unexpected EOF while probing TLS transport on {side} side.");
        }

        var restored = (Stream)new PrefixedStream(baseStream, probe.AsMemory(0, 1));
        return WrapByFirstByte(restored, probe[0], side, sessionId);
    }

    private Stream WrapByFirstByte(Stream stream, byte firstByte, string side, Guid sessionId)
    {
        if (firstByte == 0x12)
        {
            _logger.LogDebug(
                "Session {SessionId}: detected TDS-encapsulated TLS on {Side} side (first byte 0x12).",
                sessionId,
                side);
            return new TdsTlsEncapsulationStream(stream);
        }

        if (firstByte == 0x16)
        {
            _logger.LogDebug(
                "Session {SessionId}: detected raw TLS on {Side} side (first byte 0x16).",
                sessionId,
                side);
            return stream;
        }

        _logger.LogDebug(
            "Session {SessionId}: unknown TLS framing byte 0x{FirstByte:X2} on {Side} side. Trying TDS encapsulation.",
            sessionId,
            firstByte,
            side);
        return new TdsTlsEncapsulationStream(stream);
    }
}
