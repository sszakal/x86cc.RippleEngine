namespace x86cc.RippleEngine.Storage;

/// <summary>
/// The one rule set for a valid per-type scheduling config, applied at BOTH doors into
/// <c>type_schedule</c>: the developer-facing registration (<c>AddHandler</c>, which fails the host at startup)
/// and <see cref="IEngineStore.UpsertTypeScheduleAsync"/> / <see cref="IEngineStore.SeedTypeScheduleAsync"/>,
/// which every other caller — the dashboard edit path included — goes through.
/// </summary>
/// <remarks>
/// These values are not advisory: they are operands in the <c>schedule_order</c> arithmetic
/// (<c>base + (k / batch_size) * gap_seconds</c>, see <see cref="ScheduleOrderSql"/>), so a nonsense value is
/// not a bad-but-working setting, it is a permanent silent outage:
/// <list type="bullet">
/// <item><c>batch_size = 0</c> makes <c>batch_size</c> the divisor zero, so EVERY fan-out for that type —
/// <c>INSERT … SELECT</c>, unnest insert, and the resume rebase alike — raises <c>division by zero</c>, forever,
/// with nothing wrong at registration time to point at.</item>
/// <item>A negative <c>gap_seconds</c> makes <c>schedule_order</c> DECREASE as <c>k</c> grows, so the type's
/// later batches sort ahead of the global frontier the claim reads from — one such type starves every other job
/// in the cluster. <b>Zero starves too</b>, just less obviously: it collapses every batch of the type onto the
/// single slot at <c>base</c> (and <c>BaseExpr</c>'s continuation arm, <c>max(tail) + gap</c>, then returns the
/// tail unchanged), so a million-ripple wave sits as a million rows at one <c>schedule_order</c> while every
/// other type's second and later batches are stamped strictly to its right — and the claim, which is just
/// <c>order by schedule_order</c>, drains the whole type before anything else progresses. The dashboard's PUT
/// already rejects it; so does this, so the two agree.</item>
/// <item><c>max_attempts &lt; 1</c> makes a ripple that is claimed, runs once, and is then terminally Failed
/// whatever it returns — <c>ExecutionPipeline</c> settles it terminal on <c>attempt &gt;= max_attempts</c>, which
/// is already true of the first attempt. The wave faults rather than doing its work. (The claim itself has no
/// <c>max_attempts</c> predicate — <c>PollSql</c> only projects the resolved value out for the pipeline and
/// recovery to enforce.)</item>
/// </list>
/// This mirrors the startup validation <c>AddRippleEngine</c> applies to <c>RippleEngineOptions</c>, for the
/// same reason and against the same failure mode.
/// </remarks>
public static class TypeScheduleGuard
{
    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if the config can't produce a usable schedule.</summary>
    public static void Validate(int batchSize, double gapSeconds, int? maxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        // Zero is rejected along with negatives: it doesn't mean "no spacing", it means every batch of the type
        // shares one slot and the type monopolises the claim until drained (see the remarks above).
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gapSeconds);
        if (!double.IsFinite(gapSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(gapSeconds), gapSeconds,
                "gapSeconds must be a finite number.");
        }

        if (maxAttempts is { } max)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max, nameof(maxAttempts));
        }
    }
}
