namespace x86cc.RippleEngine.Core;

/// <summary>
/// One aggregated entry of a <see cref="SplashReport"/>: the set of targets that share an
/// <see cref="Outcome"/> and <see cref="Output"/> message. Aggregating this way keeps the report small (all
/// same-result targets collapse into one item) and is the unit the export renders as a row.
/// </summary>
public sealed class SplashReportItem(SplashOutcome outcome, string? output, IReadOnlyList<string> targetIds)
{
    public SplashOutcome Outcome { get; } = outcome;

    public string? Output { get; } = output;

    public IReadOnlyList<string> TargetIds { get; } = targetIds;
}
