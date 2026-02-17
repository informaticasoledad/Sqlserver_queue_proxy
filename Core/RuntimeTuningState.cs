namespace TDSQueue.Proxy;

public sealed class RuntimeTuningState
{
    private readonly object _sync = new();

    public RuntimeTuningState(ProxyOptions options)
    {
        AdaptiveBackpressureEnabled = options.AdaptiveBackpressureEnabled;
        QueueLowWatermark = options.QueueLowWatermark;
        QueueHighWatermark = options.QueueHighWatermark;
        TargetLatencyLowMs = options.TargetLatencyLowMs;
        TargetLatencyHighMs = options.TargetLatencyHighMs;
    }

    public bool AdaptiveBackpressureEnabled { get; private set; }
    public int QueueLowWatermark { get; private set; }
    public int QueueHighWatermark { get; private set; }
    public int TargetLatencyLowMs { get; private set; }
    public int TargetLatencyHighMs { get; private set; }

    public RuntimeTuningSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new RuntimeTuningSnapshot(
                AdaptiveBackpressureEnabled,
                QueueLowWatermark,
                QueueHighWatermark,
                TargetLatencyLowMs,
                TargetLatencyHighMs);
        }
    }

    public void Update(RuntimeTuningUpdate update)
    {
        lock (_sync)
        {
            if (update.AdaptiveBackpressureEnabled is { } enabled)
            {
                AdaptiveBackpressureEnabled = enabled;
            }

            if (update.QueueLowWatermark is { } qLow)
            {
                QueueLowWatermark = qLow;
            }

            if (update.QueueHighWatermark is { } qHigh)
            {
                QueueHighWatermark = qHigh;
            }

            if (update.TargetLatencyLowMs is { } tLow)
            {
                TargetLatencyLowMs = tLow;
            }

            if (update.TargetLatencyHighMs is { } tHigh)
            {
                TargetLatencyHighMs = tHigh;
            }

            if (QueueHighWatermark < QueueLowWatermark)
            {
                QueueHighWatermark = QueueLowWatermark;
            }

            if (TargetLatencyHighMs < TargetLatencyLowMs)
            {
                TargetLatencyHighMs = TargetLatencyLowMs;
            }
        }
    }
}

public readonly record struct RuntimeTuningSnapshot(
    bool AdaptiveBackpressureEnabled,
    int QueueLowWatermark,
    int QueueHighWatermark,
    int TargetLatencyLowMs,
    int TargetLatencyHighMs);

public sealed class RuntimeTuningUpdate
{
    public bool? AdaptiveBackpressureEnabled { get; init; }
    public int? QueueLowWatermark { get; init; }
    public int? QueueHighWatermark { get; init; }
    public int? TargetLatencyLowMs { get; init; }
    public int? TargetLatencyHighMs { get; init; }
}
