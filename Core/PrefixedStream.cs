using System.Buffers;

namespace TDSQueue.Proxy;

// Exposes already-buffered bytes first, then continues reading from the inner stream.
public sealed class PrefixedStream : Stream
{
    private readonly Stream _inner;
    private ReadOnlyMemory<byte> _prefix;

    public PrefixedStream(Stream inner, ReadOnlyMemory<byte> prefix)
    {
        _inner = inner;
        _prefix = prefix;
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

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!_prefix.IsEmpty)
        {
            var copied = Math.Min(count, _prefix.Length);
            _prefix.Span.Slice(0, copied).CopyTo(buffer.AsSpan(offset, copied));
            _prefix = _prefix.Slice(copied);
            return copied;
        }

        return _inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        if (!_prefix.IsEmpty)
        {
            var copied = Math.Min(buffer.Length, _prefix.Length);
            _prefix.Span.Slice(0, copied).CopyTo(buffer.Slice(0, copied));
            _prefix = _prefix.Slice(copied);
            return copied;
        }

        return _inner.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (!_prefix.IsEmpty)
        {
            var copied = Math.Min(buffer.Length, _prefix.Length);
            _prefix.Span.Slice(0, copied).CopyTo(buffer.Span.Slice(0, copied));
            _prefix = _prefix.Slice(copied);
            return copied;
        }

        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The wrapped transport stream lifetime is owned by ClientSession.
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
