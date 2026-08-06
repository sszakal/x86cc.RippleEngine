namespace x86cc.RippleEngine.Core;

/// <summary>
/// Fluent builder that fans a wave's ripples out of an <b>in-memory collection the caller already holds</b> —
/// no source query, no <c>INSERT … SELECT</c>; each message is serialized to its ripple payload and
/// bulk-inserted. The counterpart to <see cref="IWaveBuilder"/> for work items that aren't a queryable source
/// (an explicit list, a computed set, a stream drained into a list). Each <c>AddRipples</c> call accumulates
/// its messages; <see cref="DispatchAsync"/> creates the wave and inserts them (going through the same
/// <c>schedule_order</c> stamping as any other fan-out).
/// </summary>
public interface ICollectionWaveBuilder
{
    /// <summary>
    /// Adds one ripple per message. <typeparamref name="TMessage"/>'s name becomes each ripple's
    /// <c>payload_type</c> (and, with the wave's type, its scheduling <c>type_key</c>). Call it more than once
    /// with different message types to mix them into one wave.
    /// </summary>
    ICollectionWaveBuilder AddRipples<TMessage>(IEnumerable<TMessage> messages)
        where TMessage : notnull;

    /// <summary>Persists the wave (Active), inserts the accumulated ripples, and returns the wave.</summary>
    Task<Wave> DispatchAsync(CancellationToken ct = default);
}

/// <summary>Convenience helpers over <see cref="ICollectionWaveBuilder"/>.</summary>
public static class CollectionWaveBuilderExtensions
{
    /// <summary>Adds a single ripple (the one-item convenience over <see cref="ICollectionWaveBuilder.AddRipples{TMessage}"/>).</summary>
    public static ICollectionWaveBuilder AddRipple<TMessage>(this ICollectionWaveBuilder builder, TMessage message)
        where TMessage : notnull
        => builder.AddRipples(new[] { message });
}
