using System.Text.Json;

namespace x86cc.RippleEngine.Core;

/// <summary>
/// A wave's lifecycle. In the simplified engine there is no admission step: a wave is <see cref="Active"/>
/// from the moment it is created (its ripples are immediately eligible to be pulled). It drains to
/// <see cref="Completed"/> — or <see cref="Faulted"/> if any ripple failed terminally — when it has no pending
/// and no running ripples left; the periodic DB-side stats refresh decides this from the actual ripple states.
/// </summary>
public enum WaveStatus
{
    Active,
    Completed,
    Faulted
}

/// <summary>
/// A <b>wave</b> — the top-level unit of work (what a broker-based system would call a job). One small
/// row per fan-out. Its only job now is to carry the shared <see cref="Payload"/> (e.g. the
/// legislation-change event) once — rather than duplicating it into every ripple. There is no per-type
/// admission: every instance pulls pending ripples globally in <c>schedule_order</c> order (the batch-
/// interleaved fair-share stamped at fan-out), regardless of which wave they belong to.
/// </summary>
/// <remarks>
/// The live numbers below are recomputed from the ripples themselves by the DB-side stats refresh, not
/// maintained on the hot paths. The wave is done when it has no <see cref="Pending"/> and no
/// <see cref="Running"/>, and no <see cref="Paused"/> ripples (parked work is not done). <see cref="RippleCount"/> keeps growing as executors expand the wave
/// (iterative fan-out), so completion can't be inferred from the original count.
/// </remarks>
public class Wave
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>
    /// The wave kind — the discriminator for <see cref="Payload"/> and the dashboard grouping key. Not
    /// load-bearing for scheduling (fairness is stamped into each ripple's <c>schedule_order</c> at fan-out).
    /// </summary>
    public string Type { get; set; } = "default";

    /// <summary>
    /// The shared "event" payload every ripple in this wave sees (e.g. the law change, or migration
    /// audit info). Stored once here; a handler receives it alongside the per-ripple payload.
    /// </summary>
    public JsonDocument? Payload { get; set; }

    /// <summary>The CLR type name of <see cref="Payload"/> (its <c>$type</c> discriminator).</summary>
    public string? PayloadType { get; set; }

    public WaveStatus Status { get; set; }

    /// <summary>Total ripples ever created for the wave (grows with executor-driven expansion).</summary>
    public long RippleCount { get; set; }

    /// <summary>Ripples awaiting a claim.</summary>
    public long Pending { get; set; }

    /// <summary>Ripples claimed and currently executing (across the whole cluster).</summary>
    public long Running { get; set; }

    /// <summary>Ripples parked because their type is paused (state='Paused') — not yet done; blocks completion.</summary>
    public long Paused { get; set; }

    public long Succeeded { get; set; }

    public long Failed { get; set; }

    /// <summary>Re-execution attempts — splashes with attempt &gt; 1. Recomputed, not a hot-path counter.</summary>
    public long RetryCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when the wave drains (Active → Completed/Faulted).</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}
