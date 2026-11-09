using Microsoft.Extensions.Options;
using Shouldly;
using x86cc.RippleEngine.Engine;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// The pieces <see cref="ExecutionPipeline"/> delegates to, tested on their own — no host, no Postgres, no clock.
/// Settlement's retry timing is in <see cref="ClockDrivenTests"/>, which has the fake clock it needs.
/// </summary>
public sealed class ExecutionPartsTests
{
    [Theory]
    [InlineData(1, 2)]   // base
    [InlineData(2, 4)]   // doubled
    [InlineData(3, 8)]
    [InlineData(4, 10)]  // capped at MaxRetryBackoff
    [InlineData(8, 10)]
    public void the_default_retry_policy_doubles_the_backoff_up_to_the_cap(int attempt, double expectedSeconds)
    {
        var policy = Policy(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

        policy.NextDelay(attempt, maxAttempts: 10).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(4, 3)] // a ceiling lowered after the claim: still terminal, not a retry
    [InlineData(1, 1)]
    public void the_last_allowed_attempt_gets_no_retry(int attempt, int maxAttempts)
        => Policy(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)).NextDelay(attempt, maxAttempts).ShouldBeNull();

    [Fact]
    public void the_tracker_counts_and_releases_attempts()
    {
        var tracker = new InFlightTracker();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        tracker.Track(a, 1);
        tracker.Track(b, 1);
        tracker.Count.ShouldBe(2);
        tracker.RippleIds.ShouldBe([a, b], ignoreOrder: true);

        tracker.Release([(a, 1), (b, 1)]);
        tracker.Count.ShouldBe(0);
        tracker.RippleIds.ShouldBeEmpty();
    }

    /// <summary>
    /// This instance can own two attempts of one ripple: it stalls, a peer requeues the ripple, and it re-claims it
    /// under the same InstanceId. Settling the stale attempt 1 must not drop the live attempt 2 — if it did,
    /// self-recovery would requeue a ripple whose handler is still running.
    /// </summary>
    [Fact]
    public void settling_a_stale_attempt_keeps_the_live_attempt_of_the_same_ripple_owned()
    {
        var tracker = new InFlightTracker();
        var ripple = Guid.NewGuid();

        tracker.Track(ripple, 1);
        tracker.Track(ripple, 2);
        tracker.RippleIds.ShouldBe([ripple]); // one row in the DB, reported once

        tracker.Release([(ripple, 1)]);

        tracker.Count.ShouldBe(1);
        tracker.RippleIds.ShouldBe([ripple]);
    }

    private static ExponentialRetryPolicy Policy(TimeSpan backoff, TimeSpan max)
        => new(Options.Create(new RippleEngineOptions { RetryBackoff = backoff, MaxRetryBackoff = max }));
}
