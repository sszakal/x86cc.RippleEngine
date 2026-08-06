namespace x86cc.RippleEngine.Core;

/// <summary>
/// Entry point for generating a <b>wave</b> and its <b>ripples</b> from an in-memory collection — the
/// source-less counterpart to a provider's wave generator (<c>IMartenWaveGenerator</c>,
/// <c>IEfWaveGenerator</c>). Use it when the work items are already materialised (an explicit list, a computed
/// set) rather than a queryable source. It needs no provider session; the implementation (in the Storage
/// package) goes straight through the engine store, so the ripples get the same <c>schedule_order</c> stamping
/// as any other fan-out.
/// <para>
/// <see cref="Create(string)"/> starts a brand-new wave; <see cref="Continue"/> expands an existing wave from
/// inside a handler — the same <c>Create</c>/<c>Continue</c> verbs the queryable generators expose, so the
/// only thing that varies across the three generators is the <i>source</i> (collection vs Marten vs EF).
/// </para>
/// </summary>
public interface ICollectionWaveGenerator
{
    /// <summary>Start a brand-new wave (no shared wave payload).</summary>
    ICollectionWaveBuilder Create(string name);

    /// <summary>
    /// Start a brand-new wave carrying a typed <paramref name="wavePayload"/> — the shared "event" seen by
    /// every ripple. It is serialised once onto the wave row, and <c>typeof(TWave).Name</c> becomes the wave's
    /// <c>Type</c>/<c>PayloadType</c> discriminator (the wave half of each ripple's scheduling <c>type_key</c>).
    /// </summary>
    ICollectionWaveBuilder Create<TWave>(string name, TWave wavePayload)
        where TWave : notnull;

    /// <summary>
    /// Expand the wave the current ripple belongs to (in-flight expansion), adding ripples parented to that
    /// ripple for the audit lineage — the in-memory counterpart to a queryable generator's <c>Continue</c>.
    /// The wave id and parent ripple id are read from <paramref name="context"/> (a running handler's
    /// <see cref="IRippleContext"/>); the wave's <c>ripple_count</c> grows and it will not complete until the
    /// added ripples (and anything they in turn spawn) have settled.
    /// </summary>
    ICollectionWaveBuilder Continue(IRippleContext context);
}

/// <summary>Convenience helpers over <see cref="ICollectionWaveGenerator"/>.</summary>
public static class CollectionWaveGeneratorExtensions
{
    /// <summary>
    /// <b>Fire a single job and task</b>: create a wave carrying <paramref name="wavePayload"/> (the event)
    /// with exactly one ripple (<paramref name="ripple"/>, the target) and dispatch it in one call. The
    /// composite handler key is <c>"{typeof(TWave).Name}|{typeof(TRipple).Name}"</c> — the same one an
    /// <c>AddHandler&lt;TWave, TRipple, THandler&gt;</c> registers. Returns the created (Active) wave so the
    /// caller can track it to completion.
    /// </summary>
    public static Task<Wave> FireAsync<TWave, TRipple>(this ICollectionWaveGenerator generator,
        string name, TWave wavePayload, TRipple ripple, CancellationToken ct = default)
        where TWave : notnull
        where TRipple : notnull
        => generator.Create(name, wavePayload).AddRipple(ripple).DispatchAsync(ct);
}
