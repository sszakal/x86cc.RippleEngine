using System.Collections.Concurrent;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// The attempts this instance owns: posted to the execute block and not yet <b>durably settled</b> (executed AND
/// their outcome written). One instance per engine, shared by the three parties that care about ownership: the
/// <see cref="ExecutionPipeline"/> takes a slot on post, the <see cref="SettlementWriter"/> releases it once the
/// outcome lands, and the <see cref="RecoveryLoop"/> reconciles the DB's view against <see cref="RippleIds"/>.
/// </summary>
/// <remarks>
/// A slot is held until settlement — not just until execution finishes — so a stalled settlement (see
/// <see cref="SettlementWriter"/>) keeps <see cref="Count"/> high, shrinks the capacity the dispatcher claims
/// against to zero, and stops it pulling more work. That also implicitly bounds the settlement channels: at most
/// the pipeline depth's worth of outcomes can be awaiting a write at once.
/// <para>
/// Keyed on (ripple id, ATTEMPT), not on ripple id. This instance can genuinely own two attempts of the same
/// ripple at once: it stalls past HeartbeatTimeout while still inside ExecutionTimeout, a peer requeues the
/// ripple, and it comes back under the same InstanceId and re-claims it. Keyed by id alone, attempt 2's post
/// overwrote attempt 1's entry and then attempt 1's settlement — which correctly no-ops against the store's
/// attempt fence, but is still a completed write from the flush loop's point of view — REMOVED the key
/// belonging to the live attempt 2. Self-recovery would then see a Running row it believes nothing is
/// executing and requeue a ripple whose handler was still running, re-running it and burning an attempt.
/// </para>
/// </remarks>
internal sealed class InFlightTracker
{
    private readonly ConcurrentDictionary<(Guid RippleId, int Attempt), byte> _owned = new();

    // Kept beside the dictionary rather than read from its Count: ConcurrentDictionary.Count takes every bucket
    // lock, and the dispatcher reads this on each poll and heartbeat.
    private int _count;

    /// <summary>How many attempts are owned right now (executing, queued, or awaiting settlement).</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Snapshot of the ripple ids owned right now. Distinct: the DB row has one current attempt, so an id must be
    /// reported as owned while ANY of its attempts is still live here.
    /// </summary>
    public IReadOnlyCollection<Guid> RippleIds => _owned.Keys.Select(k => k.RippleId).Distinct().ToArray();

    /// <summary>Takes a slot for an attempt about to be posted.</summary>
    public void Track(Guid rippleId, int attempt)
    {
        _owned[(rippleId, attempt)] = 0;
        Interlocked.Increment(ref _count);
    }

    /// <summary>Gives back the slots of attempts that are no longer this instance's: settled, or never accepted.</summary>
    public void Release(IEnumerable<(Guid RippleId, int Attempt)> attempts)
    {
        var released = 0;
        foreach (var attempt in attempts)
        {
            _owned.TryRemove(attempt, out _);
            released++;
        }

        Interlocked.Add(ref _count, -released);
    }
}
