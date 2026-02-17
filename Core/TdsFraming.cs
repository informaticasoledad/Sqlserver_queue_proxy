using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace TDSQueue.Proxy;

public static class TdsFraming
{
    private const int HeaderSize = 8;
    private const byte EndOfMessage = 0x01;
    private const int DefaultMaxMessageBytes = 1024 * 1024;

    public static async ValueTask<PooledMessageBuffer?> ReadMessageAsync(
        PipeReader reader,
        CancellationToken cancellationToken)
        => await ReadMessageAsync(reader, DefaultMaxMessageBytes, cancellationToken).ConfigureAwait(false);

    public static async ValueTask<PooledMessageBuffer?> ReadMessageAsync(
        PipeReader reader,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        var payload = new PooledMessageBuffer(4096);

        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryReadMessage(ref buffer, payload, maxMessageBytes, out var isCompletedMessage))
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (isCompletedMessage)
                {
                    return payload;
                }
            }
            else
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
            }

            if (result.IsCompleted)
            {
                if (payload.Length == 0)
                {
                    payload.Dispose();
                    return null;
                }

                payload.Dispose();
                throw new InvalidOperationException("Stream ended before TDS message was completed.");
            }
        }
    }

    private static bool TryReadMessage(
        ref ReadOnlySequence<byte> buffer,
        PooledMessageBuffer collector,
        int maxMessageBytes,
        out bool isCompletedMessage)
    {
        isCompletedMessage = false;

        var cursor = buffer;
        Span<byte> header = stackalloc byte[HeaderSize];

        while (cursor.Length >= HeaderSize)
        {
            cursor.Slice(0, HeaderSize).CopyTo(header);

            var packetLength = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(2, 2));
            if (packetLength < HeaderSize)
            {
                throw new InvalidOperationException($"Invalid TDS packet length {packetLength}.");
            }

            if (cursor.Length < packetLength)
            {
                break;
            }

            if (collector.Length + packetLength > maxMessageBytes)
            {
                throw new InvalidOperationException(
                    $"TDS message size exceeded configured limit of {maxMessageBytes} bytes.");
            }

            var packet = cursor.Slice(0, packetLength);
            collector.Append(packet);

            var status = header[1];
            cursor = cursor.Slice(packetLength);

            if ((status & EndOfMessage) == EndOfMessage)
            {
                buffer = cursor;
                isCompletedMessage = true;
                return true;
            }
        }

        buffer = cursor;
        return false;
    }
}
