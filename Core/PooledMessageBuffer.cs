using System.Buffers;

namespace TDSQueue.Proxy;

public sealed class PooledMessageBuffer : IDisposable
{
    private byte[] _buffer;

    public PooledMessageBuffer(int initialCapacity = 4096)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        Length = 0;
    }

    public int Length { get; private set; }

    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, Length);

    public void Append(ReadOnlySequence<byte> source)
    {
        var sourceLength = checked((int)source.Length);
        EnsureCapacity(Length + sourceLength);
        source.CopyTo(_buffer.AsSpan(Length));
        Length += sourceLength;
    }

    public void Append(ReadOnlySpan<byte> source)
    {
        EnsureCapacity(Length + source.Length);
        source.CopyTo(_buffer.AsSpan(Length));
        Length += source.Length;
    }

    public byte[] DetachToArray()
    {
        var output = new byte[Length];
        _buffer.AsSpan(0, Length).CopyTo(output);
        return output;
    }

    public void Dispose()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_buffer, clearArray: false);
        _buffer = Array.Empty<byte>();
        Length = 0;
    }

    private void EnsureCapacity(int requested)
    {
        if (requested <= _buffer.Length)
        {
            return;
        }

        var next = Math.Max(_buffer.Length * 2, requested);
        var replacement = ArrayPool<byte>.Shared.Rent(next);
        _buffer.AsSpan(0, Length).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: false);
        _buffer = replacement;
    }
}
