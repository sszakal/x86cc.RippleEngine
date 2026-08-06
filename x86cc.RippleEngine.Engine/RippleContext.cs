using x86cc.RippleEngine.Core;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// The engine's concrete <see cref="IRippleContext"/>: a plain per-execution carrier of the wave/ripple ids and
/// retry bookkeeping. Constructed per execution by <see cref="ExecutionPipeline"/>; the handler's results come
/// back as its returned <see cref="SplashReport"/>s, so the context holds no mutable output.
/// </summary>
internal sealed class RippleContext(
    Guid waveId, Guid rippleId, int attempt, int maxAttempts, CancellationToken cancellationToken) : IRippleContext
{
    /// <summary>The wave this ripple belongs to.</summary>
    public Guid WaveId { get; } = waveId;

    /// <summary>This ripple's id — the parent stamped on any child ripples it spawns.</summary>
    public Guid RippleId { get; } = rippleId;

    /// <summary>This attempt's number, 1-based (the first splash is attempt 1).</summary>
    public int Attempt { get; } = attempt;

    /// <summary>The ceiling on attempts before a failure becomes terminal.</summary>
    public int MaxAttempts { get; } = maxAttempts;

    /// <summary>How many times this ripple has already been retried before this attempt (<c>Attempt - 1</c>).</summary>
    public int CurrentRetryCount => Attempt - 1;

    /// <summary>The maximum number of retries allowed (<c>MaxAttempts - 1</c>).</summary>
    public int RetryCount => MaxAttempts - 1;

    /// <summary>Cancels when the execution timeout elapses or the host is shutting down.</summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;
}
