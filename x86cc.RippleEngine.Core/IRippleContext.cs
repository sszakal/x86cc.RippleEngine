namespace x86cc.RippleEngine.Core;

/// <summary>
/// The per-execution handle passed to an <see cref="IRippleHandler{TWave,TRipple}"/>. Exposes retry
/// bookkeeping (read-only), a cancellation token honouring the execution timeout and shutdown, and the wave/
/// ripple ids used to expand the wave in flight. Per-target results are the handler's <b>return value</b>
/// (<see cref="SplashReport"/>s), not accumulated on the context.
/// <para>
/// This is the developer-facing contract only; the engine supplies the concrete implementation (which also
/// carries the internal surface the runtime reads back after the handler returns). Handlers can be unit-tested
/// against a fake of this interface without constructing any engine machinery.
/// </para>
/// </summary>
public interface IRippleContext
{
    /// <summary>
    /// The wave this ripple belongs to. Together with <see cref="RippleId"/> it is the parent token a handler
    /// hands to a wave generator's <c>Continue(this)</c> to <b>expand the wave in flight</b> — adding ripples
    /// parented to this one. The source picks the generator:
    /// <see cref="ICollectionWaveGenerator.Continue"/> for items already in memory, or a queryable-source
    /// generator (<c>IMartenWaveGenerator</c> / <c>IEfWaveGenerator</c>) <c>Continue</c> for a server-side
    /// <c>INSERT … SELECT</c> whose source rows never load into the handler.
    /// </summary>
    Guid WaveId { get; }

    /// <summary>This ripple's id — the parent stamped on any child ripples it spawns (the audit lineage).</summary>
    Guid RippleId { get; }

    /// <summary>This attempt's number, 1-based (the first splash is attempt 1).</summary>
    int Attempt { get; }

    /// <summary>The ceiling on attempts before a failure becomes terminal.</summary>
    int MaxAttempts { get; }

    /// <summary>How many times this ripple has already been retried before this attempt (<c>Attempt - 1</c>).</summary>
    int CurrentRetryCount { get; }

    /// <summary>The maximum number of retries allowed (<c>MaxAttempts - 1</c>).</summary>
    int RetryCount { get; }

    /// <summary>Cancels when the execution timeout elapses or the host is shutting down.</summary>
    CancellationToken CancellationToken { get; }
}
