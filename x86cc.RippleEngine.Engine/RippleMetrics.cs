using System.Diagnostics.Metrics;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// Throughput/latency instrumentation for the engine, published on a BCL <see cref="Meter"/> (no external
/// dependency — inert unless something listens). Counters and the duration histogram are tagged by
/// <c>type_key</c> (the wave+ripple pair), so a host that wires OpenTelemetry with
/// <c>AddMeter(<see cref="MeterName"/>)</c> gets per-handler-type throughput and latency (e.g. in the Aspire
/// dashboard), plus an <c>ripple.executing</c> gauge of this instance's live in-flight count. Registered as a
/// singleton by <c>AddRippleEngine</c>.
/// <para>
/// Beyond throughput, it reports what otherwise only shows up in logs — the engine intervening:
/// <c>ripple.recovered</c> (work reclaimed from a dead instance, or this instance's own stranded claims),
/// <c>ripple.abandoned</c> / <c>ripple.released</c> (handlers it stopped waiting for, or stopped at shutdown),
/// <c>ripple.settlement.retries</c> / <c>ripple.settlement.rejected</c> (outcome writes that failed, or landed
/// without their report) and <c>ripple.poll.duration</c>. And the queue itself, cluster-wide:
/// <c>ripple.queue.depth</c> and <c>ripple.queue.head_wait</c> (see <see cref="QueueSnapshot"/>), sampled by
/// <c>QueueMetricsLoop</c>.
/// </para>
/// </summary>
public sealed class RippleMetrics : IDisposable
{
    public const string MeterName = "x86cc.RippleEngine";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _claimed;
    private readonly Counter<long> _succeeded;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _abandoned;
    private readonly Counter<long> _released;
    private readonly Counter<long> _recovered;
    private readonly Counter<long> _settlementRetries;
    private readonly Counter<long> _settlementRejected;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _pollDuration;
    private readonly ObservableGauge<long> _queueDepth;
    private readonly ObservableGauge<double> _queueHeadWait;

    // The last queue sample (boxed so the read/write is one atomic reference swap); null until the first one.
    private object? _queue;

    private Func<int>? _executingProvider;
    private string _instanceId = "";

    public RippleMetrics()
    {
        _claimed = _meter.CreateCounter<long>("ripple.claimed", "{ripple}", "Ripples claimed for execution.");
        _succeeded = _meter.CreateCounter<long>("ripple.succeeded", "{ripple}", "Ripples that executed successfully.");
        _failed = _meter.CreateCounter<long>("ripple.failed", "{ripple}", "Ripple executions that threw (an attempt failed).");
        _abandoned = _meter.CreateCounter<long>("ripple.abandoned", "{ripple}",
            "Handlers that ignored cancellation and were abandoned (the attempt failed; the handler may still be running).");
        _released = _meter.CreateCounter<long>("ripple.released", "{ripple}",
            "Attempts interrupted by shutdown and requeued without being counted.");
        _recovered = _meter.CreateCounter<long>("ripple.recovered", "{ripple}",
            "Ripples this instance's recovery sweep reclaimed (reason: dead_instance | self_stranded).");
        _settlementRetries = _meter.CreateCounter<long>("ripple.settlement.retries", "{write}",
            "Settlement batch writes that failed and were retried.");
        _settlementRejected = _meter.CreateCounter<long>("ripple.settlement.rejected", "{ripple}",
            "Outcomes the database rejected, settled without their report.");
        _duration = _meter.CreateHistogram<double>("ripple.duration", "ms", "Wall-clock duration of a single ripple execution.");
        _pollDuration = _meter.CreateHistogram<double>("ripple.poll.duration", "ms",
            "Round-trip time of one poll (heartbeat + claim).");
        _queueDepth = _meter.CreateObservableGauge("ripple.queue.depth", ObserveQueueDepth, "{ripple}",
            "Pending ripples across all active waves (cluster-wide; every instance reports the same value).");
        _queueHeadWait = _meter.CreateObservableGauge("ripple.queue.head_wait", ObserveQueueHeadWait, "s",
            "How long the next ripple to be claimed has been waiting (cluster-wide).");
        _meter.CreateObservableGauge("ripple.executing", ObserveExecuting, "{ripple}",
            "Ripples currently owned (executing or awaiting settlement) on this instance.");
    }

    /// <summary>Wires the gauge to this instance's live in-flight count (called once by the execution pipeline).</summary>
    public void SetExecutingProvider(Func<int> provider, string instanceId)
    {
        _executingProvider = provider;
        _instanceId = instanceId;
    }

    public void Claimed(int count, string typeKey)
    {
        if (count > 0)
        {
            _claimed.Add(count, Tag(typeKey));
        }
    }

    public void Succeeded(double durationMs, string typeKey)
    {
        var tag = Tag(typeKey);
        _succeeded.Add(1, tag);
        _duration.Record(durationMs, tag);
    }

    public void Failed(double durationMs, string typeKey)
    {
        var tag = Tag(typeKey);
        _failed.Add(1, tag);
        _duration.Record(durationMs, tag);
    }

    public void Abandoned(string typeKey) => _abandoned.Add(1, Tag(typeKey));

    public void Released(string typeKey) => _released.Add(1, Tag(typeKey));

    /// <param name="reason"><c>dead_instance</c> or <c>self_stranded</c>.</param>
    public void Recovered(int count, string reason)
    {
        if (count > 0)
        {
            _recovered.Add(count, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    public void SettlementRetried() => _settlementRetries.Add(1);

    public void SettlementRejected() => _settlementRejected.Add(1);

    public void Polled(double durationMs) => _pollDuration.Record(durationMs);

    /// <summary>Whether anything is listening to the queue gauges — the sampling loop skips its read otherwise.</summary>
    public bool QueueObserved => _queueDepth.Enabled || _queueHeadWait.Enabled;

    public void SetQueue(QueueSnapshot snapshot) => Volatile.Write(ref _queue, snapshot);

    private IEnumerable<Measurement<long>> ObserveQueueDepth()
    {
        if (Volatile.Read(ref _queue) is QueueSnapshot snapshot)
        {
            yield return new Measurement<long>(snapshot.Depth);
        }
    }

    private IEnumerable<Measurement<double>> ObserveQueueHeadWait()
    {
        if (Volatile.Read(ref _queue) is QueueSnapshot snapshot)
        {
            yield return new Measurement<double>(snapshot.HeadWaitSeconds);
        }
    }

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<int>> ObserveExecuting()
    {
        var provider = _executingProvider;
        if (provider is not null)
        {
            yield return new Measurement<int>(provider(), new KeyValuePair<string, object?>("instance_id", _instanceId));
        }
    }

    private static KeyValuePair<string, object?> Tag(string typeKey) => new("type_key", typeKey);
}
