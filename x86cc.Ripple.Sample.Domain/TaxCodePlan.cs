namespace x86cc.Ripple.Sample.Domain;

/// <summary>A tax code the sample deliberately populates to a known size, so scenarios can target it.</summary>
public readonly record struct TaxCodeTarget(string Code, int Count);

/// <summary>
/// Deterministically maps a company's global seed index to a tax code so that a handful of "target" codes end
/// up with <b>exact</b> cardinalities (1k / 10k / 100k / 200k / 400k) — letting the taxation-change wave be
/// run at each scale. The first 711,000 indices fill the targets (in ascending-size order); everything after
/// is spread across ~500 background codes. Because the mapping is a pure function of the index, seeding is
/// reproducible and idempotent, and the target counts are exact whenever <c>Total ≥ 711,000</c>.
/// </summary>
public static class TaxCodePlan
{
    public const int BackgroundCodeCount = 500;

    /// <summary>The target codes and their exact populated sizes, ascending — cumulative thresholds below.</summary>
    public static readonly IReadOnlyList<TaxCodeTarget> Targets =
    [
        new("TAX-1K", 1_000),
        new("TAX-10K", 10_000),
        new("TAX-100K", 100_000),
        new("TAX-200K", 200_000),
        new("TAX-400K", 400_000),
    ];

    /// <summary>The index (exclusive) at which the target band ends and background codes begin.</summary>
    public static readonly long TargetSpan = Targets.Sum(t => (long)t.Count); // 711,000

    /// <summary>The tax code for a company at global seed <paramref name="index"/> (0-based).</summary>
    public static string TaxCodeForIndex(long index)
    {
        long cursor = 0;
        foreach (var target in Targets)
        {
            cursor += target.Count;
            if (index < cursor)
            {
                return target.Code;
            }
        }

        return $"BG-{(index - TargetSpan) % BackgroundCodeCount:D4}";
    }
}
