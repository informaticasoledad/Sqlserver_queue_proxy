using System.Diagnostics;
using System.Threading.Tasks;

namespace TDSQueue.Proxy;

public sealed class ProxyContext : IDisposable
{
    public ProxyContext(ClientSession session, PooledMessageBuffer requestBuffer)
    {
        RequestId = Guid.NewGuid();
        Session = session;
        RequestBuffer = requestBuffer;
        CreatedAt = Stopwatch.GetTimestamp();
        CreatedAtUtcTicks = DateTime.UtcNow.Ticks;
    }

    public Guid RequestId { get; }
    public ClientSession Session { get; }
    public PooledMessageBuffer RequestBuffer { get; }
    public long CreatedAt { get; }
    public long CreatedAtUtcTicks { get; }
    public TaskCompletionSource<byte[]> ResponseSource { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public double GetElapsedMilliseconds() =>
        Stopwatch.GetElapsedTime(CreatedAt).TotalMilliseconds;

    public void Dispose() => RequestBuffer.Dispose();
}
