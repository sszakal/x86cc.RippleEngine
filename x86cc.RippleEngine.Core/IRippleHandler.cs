namespace x86cc.RippleEngine.Core;

/// <summary>
/// <b>The one interface a developer implements.</b> Given the wave's shared payload (context, e.g. the law
/// change) and a ripple's own payload (the target(s), e.g. a company id or a batch of ids), do the work and
/// return the per-target outcomes as <see cref="SplashReport"/> groups.
/// <para>
/// Reporting is <b>by exception</b>: build a <see cref="SplashReport"/> and annotate only the targets that
/// deviate from success (the engine infers the rest). Returning <c>null</c> means every target succeeded; a
/// <see cref="SplashOutcome.Failed"/> target (or a thrown exception) marks the whole attempt failed — it retries
/// until <c>MaxAttempts</c>, then fails terminally. A throw is equivalent to failing every
/// <see cref="IRippleTarget.TargetIds"/> with the exception message. Nothing about claiming, concurrency,
/// retries, batching, or recovery is the developer's concern — the engine owns all of it and only ever calls this.
/// </para>
/// </summary>
/// <typeparam name="TWave">The shared per-wave payload type (the wave's <c>$type</c>).</typeparam>
/// <typeparam name="TRipple">The per-ripple payload type (the ripple's <c>$type</c>); carries its target ids.</typeparam>
/// <remarks>
/// Handlers should be idempotent: a ripple can run more than once (a non-terminal failure requeues it, and
/// crash recovery re-runs work an instance lost mid-flight). Upsert-style work is naturally idempotent;
/// batch work that isn't should checkpoint its own progress.
/// </remarks>
public interface IRippleHandler<in TWave, in TRipple>
    where TRipple : IRippleTarget
{
    Task<SplashReport?> Execute(TWave wave, TRipple ripple, IRippleContext context);
}
