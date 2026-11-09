using Microsoft.Extensions.Options;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// Decides what happens to a ripple whose attempt failed: retry it after a delay, or fail it terminally. Consulted
/// once per failed attempt, by the <see cref="ExecutionPipeline"/>, when it builds the outcome it settles.
/// </summary>
/// <remarks>
/// Only the in-process failure path asks this. The DB-side paths that also end attempts — dead-instance and
/// self-stranded recovery — apply <c>max_attempts</c> in SQL and requeue without a backoff, so a policy that
/// gives up before <paramref name="maxAttempts"/> narrows only this path.
/// </remarks>
internal interface IRetryPolicy
{
    /// <summary>The delay before the next attempt, or null when <paramref name="attempt"/> was the last one.</summary>
    /// <param name="attempt">The attempt that just failed (1-based).</param>
    /// <param name="maxAttempts">The type's resolved retry ceiling.</param>
    TimeSpan? NextDelay(int attempt, int maxAttempts);
}

/// <summary>
/// The default policy: retry until <c>maxAttempts</c>, waiting <see cref="RippleEngineOptions.RetryBackoff"/>
/// doubled per attempt, capped at <see cref="RippleEngineOptions.MaxRetryBackoff"/>.
/// </summary>
internal sealed class ExponentialRetryPolicy(IOptions<RippleEngineOptions> options) : IRetryPolicy
{
    private readonly RippleEngineOptions _options = options.Value;

    public TimeSpan? NextDelay(int attempt, int maxAttempts)
    {
        if (attempt >= maxAttempts)
        {
            return null;
        }

        var factor = Math.Pow(2, Math.Max(0, attempt - 1));
        var ms = Math.Min(_options.RetryBackoff.TotalMilliseconds * factor, _options.MaxRetryBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms);
    }
}
