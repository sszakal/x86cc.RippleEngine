namespace x86cc.RippleEngine.Core;

/// <summary>
/// The per-target result a handler builds and returns. Report <b>by exception</b>: call
/// <see cref="Success(string, string?)"/> / <see cref="Failed(string, string?)"/> only for the targets you want
/// to annotate — anything you don't mention is inferred <see cref="SplashOutcome.Succeeded"/> with no output.
/// Returning <c>null</c> therefore means "all targets succeeded". If any target is
/// <see cref="SplashOutcome.Failed"/>, the whole ripple attempt is treated as failed (and retried); throwing is
/// equivalent to failing every target with the exception message.
/// <para>
/// Targets that share the same <c>(outcome, output)</c> are aggregated into a single
/// <see cref="SplashReportItem"/>, so 200k successes with no message collapse to one item (and one export row).
/// </para>
/// </summary>
/// <example>
/// <code>
/// var report = SplashReport.Create();
/// report.Success("1233");
/// report.Failed("3223", "Some failed condition");
/// return report;
/// </code>
/// </example>
public sealed class SplashReport
{
    // Targets bucketed by their (outcome, output) so same-result targets aggregate into one item.
    private readonly Dictionary<(SplashOutcome Outcome, string? Output), List<string>> _groups = new();

    private SplashReport()
    {
    }

    /// <summary>Starts a new, empty report.</summary>
    public static SplashReport Create() => new();

    /// <summary>Marks <paramref name="targetId"/> succeeded (optionally with an output message).</summary>
    public SplashReport Success(string targetId, string? output = null) => Add(SplashOutcome.Succeeded, output, targetId);

    /// <summary>Marks each of <paramref name="targetIds"/> succeeded (optionally with a shared output message).</summary>
    public SplashReport Success(IEnumerable<string> targetIds, string? output = null) => Add(SplashOutcome.Succeeded, output, targetIds);

    /// <summary>Marks <paramref name="targetId"/> failed (the <paramref name="output"/> is the reason).</summary>
    public SplashReport Failed(string targetId, string? output = null) => Add(SplashOutcome.Failed, output, targetId);

    /// <summary>Marks each of <paramref name="targetIds"/> failed with a shared reason.</summary>
    public SplashReport Failed(IEnumerable<string> targetIds, string? output = null) => Add(SplashOutcome.Failed, output, targetIds);

    /// <summary>The aggregated entries (targets grouped by <c>(outcome, output)</c>).</summary>
    public IReadOnlyList<SplashReportItem> Items =>
        _groups.Select(kv => new SplashReportItem(kv.Key.Outcome, kv.Key.Output, kv.Value)).ToList();

    /// <summary>True if any target was reported <see cref="SplashOutcome.Failed"/> — the attempt then fails/retries.</summary>
    public bool HasFailures => _groups.Keys.Any(k => k.Outcome == SplashOutcome.Failed);

    private SplashReport Add(SplashOutcome outcome, string? output, string targetId)
    {
        Bucket(outcome, output).Add(targetId);
        return this;
    }

    private SplashReport Add(SplashOutcome outcome, string? output, IEnumerable<string> targetIds)
    {
        Bucket(outcome, output).AddRange(targetIds);
        return this;
    }

    private List<string> Bucket(SplashOutcome outcome, string? output)
        => _groups.TryGetValue((outcome, output), out var list) ? list : _groups[(outcome, output)] = [];
}
