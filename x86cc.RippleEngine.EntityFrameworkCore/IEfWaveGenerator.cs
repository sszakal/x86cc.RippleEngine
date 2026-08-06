using Microsoft.EntityFrameworkCore;
using x86cc.RippleEngine.Core;

namespace x86cc.RippleEngine.EntityFrameworkCore;

/// <summary>
/// Entry point for generating a <b>wave</b> and its <b>ripples</b> from EF Core entities, entirely
/// server-side — no source rows are ever loaded into the client. Injected (it carries the Ripple
/// connection); the caller passes its <see cref="DbContext"/> so the builder can translate the source
/// predicate/projection into the <c>INSERT … SELECT</c>. The EF equivalent of Marten's
/// <c>IMartenWaveGenerator</c>; both produce the same <see cref="IWaveBuilder"/>.
/// </summary>
public interface IEfWaveGenerator
{
    /// <summary>Start a brand-new wave (no shared wave payload).</summary>
    IWaveBuilder Create(DbContext context, string name);

    /// <summary>
    /// Start a brand-new wave carrying a typed <paramref name="wavePayload"/> — the shared "event" seen by
    /// every ripple. It is serialised once onto the wave row, and <c>typeof(TWave).Name</c> becomes the wave's
    /// <c>Type</c>/<c>PayloadType</c> discriminator.
    /// </summary>
    IWaveBuilder Create<TWave>(DbContext context, string name, TWave wavePayload)
        where TWave : notnull;

    /// <summary>
    /// Expand the wave the current ripple belongs to (in-flight expansion) with a server-side
    /// <c>INSERT … SELECT</c> — the same <c>Continue</c> verb the in-memory
    /// <see cref="ICollectionWaveGenerator"/> exposes, only the source differs. The wave id and parent ripple
    /// id are read from <paramref name="rippleContext"/> (a running handler's <see cref="IRippleContext"/>);
    /// new ripples are stamped with that ripple as parent for lineage.
    /// </summary>
    IWaveBuilder Continue(DbContext context, IRippleContext rippleContext);
}
