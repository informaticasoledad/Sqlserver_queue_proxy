using System.Buffers.Binary;

namespace TDSQueue.Proxy;

public static class TdsPreLoginParser
{
    private const byte PreLoginPacketType = 0x12;
    private const byte EncryptionToken = 0x01;

    public static bool IsPreLoginMessage(ReadOnlySpan<byte> message) =>
        message.Length >= 8 && message[0] == PreLoginPacketType;

    public static bool TryGetEncryptionOption(ReadOnlySpan<byte> message, out byte encryptionValue)
    {
        encryptionValue = 0;
        if (message.Length <= 8)
        {
            return false;
        }

        var payloadBuffer = ExtractTdsPayload(message);
        var payload = payloadBuffer.AsSpan();
        if (payload.Length == 0)
        {
            return false;
        }

        var tableEnd = payload.IndexOf((byte)0xFF);
        if (tableEnd < 0)
        {
            return false;
        }

        var dataBase = tableEnd + 1;
        var index = 0;

        while (index < tableEnd)
        {
            if (index + 4 >= payload.Length)
            {
                return false;
            }

            var token = payload[index];
            var offset = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(index + 1, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(index + 3, 2));

            if (token == EncryptionToken)
            {
                if (length < 1)
                {
                    return false;
                }

                // TDS docs/implementations in the wild may interpret this offset
                // from payload start; some stacks behave as if it is after table.
                if (TryReadEncryptionAt(payload, offset, length, out encryptionValue))
                {
                    return true;
                }

                var afterTableOffset = dataBase + offset;
                if (TryReadEncryptionAt(payload, afterTableOffset, length, out encryptionValue))
                {
                    return true;
                }

                return false;
            }

            index += 5;
        }

        return false;
    }

    private static bool TryReadEncryptionAt(
        ReadOnlySpan<byte> payload,
        int absoluteOffset,
        int length,
        out byte encryptionValue)
    {
        encryptionValue = 0;
        if (absoluteOffset < 0 || absoluteOffset + length > payload.Length)
        {
            return false;
        }

        encryptionValue = payload[absoluteOffset];
        return true;
    }

    private static byte[] ExtractTdsPayload(ReadOnlySpan<byte> message)
    {
        var payload = new byte[Math.Max(0, message.Length - 8)];
        var payloadLength = 0;
        var index = 0;

        while (index + 8 <= message.Length)
        {
            var packetLength = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(index + 2, 2));
            if (packetLength < 8 || index + packetLength > message.Length)
            {
                break;
            }

            var segment = message.Slice(index + 8, packetLength - 8);
            segment.CopyTo(payload.AsSpan(payloadLength));
            payloadLength += segment.Length;

            index += packetLength;
        }

        if (payloadLength == 0)
        {
            return Array.Empty<byte>();
        }

        var result = new byte[payloadLength];
        payload.AsSpan(0, payloadLength).CopyTo(result);
        return result;
    }

    public static bool ShouldUpgradeTls(byte clientEncryption, byte serverEncryption)
    {
        // ENCRYPT_OFF=0x00, ENCRYPT_ON=0x01, ENCRYPT_NOT_SUP=0x02, ENCRYPT_REQ=0x03
        // In SQL Server, ENCRYPT_OFF can still require TLS for login exchange.
        static bool IsNotSupported(byte value) => value == 0x02;
        static bool IsTlsCapable(byte value) => value is 0x00 or 0x01 or 0x03;

        if (IsNotSupported(clientEncryption) || IsNotSupported(serverEncryption))
        {
            return false;
        }

        return IsTlsCapable(clientEncryption) || IsTlsCapable(serverEncryption);
    }
}
