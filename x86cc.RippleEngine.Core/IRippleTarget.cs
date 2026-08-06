namespace x86cc.RippleEngine.Core;

/// <summary>
/// A ripple payload's <b>targets</b> — the aggregate id(s) the ripple acts on. One id for a single-target
/// ripple, many for a batched one (a user-defined batch). Every ripple payload must implement this: the engine
/// needs the full target set up front to attribute per-target outcomes — synthesising an all-failed report if
/// the handler throws, and inferring "succeeded, no output" for any target the handler didn't explicitly
/// report (see <see cref="SplashReport"/>).
/// </summary>
public interface IRippleTarget
{
    /// <summary>The aggregate id(s) this ripple targets. Usually a computed projection of the payload's ids.</summary>
    IReadOnlyList<string> TargetIds { get; }
}
