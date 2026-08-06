using Marten;
using x86cc.RippleEngine.Core;

namespace x86cc.RippleEngine.MartenDb;

/// <summary>
/// Entry point for generating a <b>wave</b> and its <b>ripples</b> from Marten source aggregates, entirely
/// server-side — no source rows are ever loaded into the client. Injected (it carries the Ripple
/// connection); the caller passes its Marten query session so the builder can translate the source
/// predicate/projection into the <c>INSERT ... SELECT</c>.
/// </summary>
public interface IMartenWaveGenerator
{
    /// <summary>Start a brand-new wave (no shared wave payload).</summary>
    IWaveBuilder Create(IQuerySession session, string name);

    /// <summary>
    /// Start a brand-new wave carrying a typed <paramref name="wavePayload"/> — the shared "event" common
    /// to every ripple (e.g. the legislation that changed, or migration-run audit info). It is serialised
    /// once onto the wave row (not duplicated into each ripple), and <c>typeof(TWave).Name</c> becomes the
    /// wave's <c>Type</c>/<c>PayloadType</c> discriminator. A handler receives it alongside the per-ripple payload.
    /// </summary>
    IWaveBuilder Create<TWave>(IQuerySession session, string name, TWave wavePayload)
        where TWave : notnull;

    /// <summary>
    /// Expand the wave the current ripple belongs to (in-flight expansion) with a server-side
    /// <c>INSERT … SELECT</c> — the same <c>Continue</c> verb the in-memory
    /// <see cref="ICollectionWaveGenerator"/> exposes, only the source differs. The wave id and parent ripple
    /// id are read from <paramref name="context"/> (a running handler's <see cref="IRippleContext"/>); new
    /// ripples are stamped with that ripple as parent for lineage, the wave's <c>ripple_count</c> grows, and it
    /// can't complete before the children run.
    /// </summary>
    IWaveBuilder Continue(IQuerySession session, IRippleContext context);
}
