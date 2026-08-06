using System.Diagnostics.Metrics;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// Throughput/latency instrumentation for the engine, published on a BCL <see cref="Meter"/> (no external
/// dependency — inert unless something listens). Counters and the duration histogram are tagged by
/// <c>type_key</c> (the wave+ripple pair), so a host that wires OpenTelemetry with
/// <c>AddMeter(<see cref="MeterName"/>)</c> gets per-handler-type throughput and latency (e.g. in the Aspire
/// dashboard), plus an <c>ripple.executing</c> gauge of this instance's live in-flight count. Registered as a
/// singleton by <c>AddRippleEngine</c>.
/// </summary>
public sealed class RippleMetrics : IDisposable
{
    public const string MeterName = "x86cc.RippleEngine";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _claimed;
    private readonly Counter<long> _succeeded;
    private readonly Counter<long> _failed;
    private readonly Histogram<double> _duration;

    private Func<int>? _executingProvider;
    private string _instanceId = "";

    public RippleMetrics()
    {
        _claimed = _meter.CreateCounter<long>("ripple.claimed", "{ripple}", "Ripples claimed for execution.");
        _succeeded = _meter.CreateCounter<long>("ripple.succeeded", "{ripple}", "Ripples that executed successfully.");
        _failed = _meter.CreateCounter<long>("ripple.failed", "{ripple}", "Ripple executions that threw (an attempt failed).");
        _duration = _meter.CreateHistogram<double>("ripple.duration", "ms", "Wall-clock duration of a single ripple execution.");
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
