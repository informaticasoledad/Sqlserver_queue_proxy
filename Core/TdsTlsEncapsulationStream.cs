using System.Buffers.Binary;

namespace TDSQueue.Proxy;

// Adapts SQL Server TLS-over-TDS transport:
// - Read: strips TDS packet headers and exposes a contiguous TLS byte stream.
// - Write: wraps TLS bytes into TDS packets (type 0x12).
public sealed class TdsTlsEncapsulationStream : Stream
{
    private const int TdsHeaderSize = 8;
    private const byte PreLoginPacketType = 0x12;
    private const byte EndOfMessage = 0x01;
    private const int MaxPacketPayload = 4096;

    private readonly Stream _inner;
    private byte[] _readBuffer = Array.Empty<byte>();
    private int _readOffset;
    private int _packetId;

    public TdsTlsEncapsulationStream(Stream inner)
    {
        _inner = inner;
        _packetId = 1;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (!buffer.IsEmpty)
        {
            if (!TryConsumeReadBuffer(buffer, out var consumed))
            {
                if (total > 0)
                {
                    break;
                }

                FillReadBufferAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (_readBuffer.Length == 0)
                {
                    break;
                }

                continue;
            }

            total += consumed;
            buffer = buffer.Slice(consumed);
        }

        return total;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var total = 0;
        while (!buffer.IsEmpty)
        {
            if (!TryConsumeReadBuffer(buffer.Span, out var consumed))
            {
                if (total > 0)
                {
                    break;
                }

                await FillReadBufferAsync(cancellationToken).ConfigureAwait(false);
                if (_readBuffer.Length == 0)
                {
                    break;
                }

                continue;
            }

            total += consumed;
            buffer = buffer.Slice(consumed);
        }

        return total;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var chunkLen = Math.Min(buffer.Length, MaxPacketPayload);
            var chunk = buffer.Slice(0, chunkLen);
            buffer = buffer.Slice(chunkLen);

            var header = new byte[TdsHeaderSize];
            header[0] = PreLoginPacketType;
            header[1] = buffer.IsEmpty ? EndOfMessage : (byte)0x00;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)(TdsHeaderSize + chunkLen));
            header[4] = 0x00;
            header[5] = 0x00;
            header[6] = unchecked((byte)_packetId++);
            header[7] = 0x00;

            _inner.Write(header);
            _inner.Write(chunk);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            var chunkLen = Math.Min(buffer.Length, MaxPacketPayload);
            var chunk = buffer.Slice(0, chunkLen);
            buffer = buffer.Slice(chunkLen);

            var header = new byte[TdsHeaderSize];
            header[0] = PreLoginPacketType;
            header[1] = buffer.IsEmpty ? EndOfMessage : (byte)0x00;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)(TdsHeaderSize + chunkLen));
            header[4] = 0x00;
            header[5] = 0x00;
            header[6] = unchecked((byte)_packetId++);
            header[7] = 0x00;

            await _inner.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _inner.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private bool TryConsumeReadBuffer(Span<byte> destination, out int consumed)
    {
        consumed = 0;
        if (_readOffset >= _readBuffer.Length)
        {
            return false;
        }

        var available = _readBuffer.Length - _readOffset;
        consumed = Math.Min(available, destination.Length);
        _readBuffer.AsSpan(_readOffset, consumed).CopyTo(destination);
        _readOffset += consumed;

        if (_readOffset >= _readBuffer.Length)
        {
            _readBuffer = Array.Empty<byte>();
            _readOffset = 0;
        }

        return true;
    }

    private async Task FillReadBufferAsync(CancellationToken cancellationToken)
    {
        _readBuffer = Array.Empty<byte>();
        _readOffset = 0;

        var header = new byte[TdsHeaderSize];
        var headerRead = await ReadExactlyAsync(_inner, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return;
        }

        if (headerRead < TdsHeaderSize)
        {
            throw new IOException("Unexpected end of stream while reading TDS TLS header.");
        }

        var packetLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        if (packetLength < TdsHeaderSize)
        {
            throw new IOException($"Invalid TDS packet length {packetLength} during TLS encapsulation.");
        }

        var payloadLength = packetLength - TdsHeaderSize;
        if (payloadLength == 0)
        {
            return;
        }

        var payload = new byte[payloadLength];
        var payloadRead = await ReadExactlyAsync(_inner, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead < payloadLength)
        {
            throw new IOException("Unexpected end of stream while reading TDS TLS payload.");
        }

        _readBuffer = payload;
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
