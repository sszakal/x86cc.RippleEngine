namespace x86cc.RippleEngine.Core;

/// <summary>The terminal result of a single ripple splash (one execution attempt), stored on the <c>ripple.splash</c> audit row.</summary>
public enum SplashOutcome
{
    Succeeded,
    Failed,

    /// <summary>The attempt was interrupted — its owning instance died mid-run, so it produced no result;
    /// written by recovery so an outcome-less splash is explained rather than a mystery (its <c>output</c>
    /// records why).</summary>
    Abandoned
}
