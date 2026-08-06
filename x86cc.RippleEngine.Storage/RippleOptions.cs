namespace x86cc.RippleEngine.Storage;

/// <summary>
/// Shared storage-layer settings — currently just retention.
/// </summary>
/// <remarks>
/// The scheduler/retry defaults (batch size, gap, retry ceiling) are deliberately NOT settable here. They live
/// in ONE place at runtime: the reserved DEFAULT row of <c>type_schedule</c>
/// (<see cref="RippleTypeKey.Default"/>), which the migration seeds from the <c>*Fallback</c> constants below and
/// which is thereafter <b>owned by the dashboard</b> — it is edited via <c>PUT /api/settings/types/__default__</c>
/// and is intentionally the one row that cannot be deleted. Mirroring those values as settable properties here
/// would create a second writer that silently reverted every dashboard edit on the next restart. Configure them
/// per type in code via <c>AddHandler(batchSize, gapSeconds, maxAttempts)</c> (seeded insert-if-absent, so the
/// dashboard still wins), or change the floor itself from the dashboard.
/// </remarks>
public sealed class RippleOptions
{
    /// <summary>Fan-out fallback batch size when a type is unconfigured (inlined into the stamp SQL).</summary>
    public const int DefaultBatchSizeFallback = 1000;

    /// <summary>Fan-out fallback gap (seconds) when a type is unconfigured (inlined into the stamp SQL).</summary>
    public const double DefaultGapSecondsFallback = 1;

    /// <summary>Default retry ceiling when a type is unconfigured. The migration seeds this into the
    /// <c>type_schedule</c> default row (<see cref="RippleTypeKey.Default"/>), which the claim/recovery read.</summary>
    public const int DefaultMaxAttemptsFallback = 5;

    /// <summary>
    /// How long a finished wave (its <c>wave</c> row + report chunks) is kept after <c>completed_at</c> before
    /// the retention purge deletes it. <c>null</c> ⇒ <b>keep forever</b> (retention is opt-in). Stamped as
    /// <c>expire_at</c> at compaction; <see cref="RetentionByWaveType"/> overrides it per wave <c>type</c>.
    /// </summary>
    public TimeSpan? DefaultRetention { get; set; }

    /// <summary>Per-wave-type retention overrides (keyed by the wave's <c>type</c>); a value of <c>null</c>
    /// keeps that type forever. Falls back to <see cref="DefaultRetention"/> for unlisted types.</summary>
    public IDictionary<string, TimeSpan?> RetentionByWaveType { get; } = new Dictionary<string, TimeSpan?>();

    /// <summary>Resolves the retention for a wave <c>type</c>: its override if present, else the default.</summary>
    public TimeSpan? RetentionFor(string waveType) => RetentionByWaveType.TryGetValue(waveType, out var r) ? r : DefaultRetention;
}
