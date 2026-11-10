using System.Text.Json;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// Turns a handler's returned <see cref="SplashReport"/> into the per-attempt report persisted on the splash's
/// <c>report</c> jsonb. Report-by-exception: any target the handler didn't mention is inferred succeeded /
/// no-output (the report already aggregates by <c>(outcome, output)</c>). Also decides whether the attempt
/// failed (any target reported <see cref="SplashOutcome.Failed"/>).
/// </summary>
internal static class SplashReportBuilder
{
    /// <summary>
    /// Fills in inferred successes for any unreported target, then returns the serialized <c>report</c> jsonb
    /// (null only when there are no targets and nothing was reported) and whether the attempt failed.
    /// </summary>
    public static (string? Report, bool Failed) Resolve(IReadOnlyList<string> targetIds, SplashReport? report)
    {
        report ??= SplashReport.Create();

        // Report by exception: add an inferred success for any target the handler didn't mention (the builder
        // aggregates it into the existing succeeded item).
        var reported = report.Items.SelectMany(i => i.TargetIds).ToHashSet();
        foreach (var id in targetIds)
        {
            if (!reported.Contains(id))
            {
                report.Success(id);
            }
        }

        var items = report.Items;
        var json = items.Count == 0 ? null : Serialize(items);
        return (json, report.HasFailures);
    }

    /// <summary>The all-targets-failed report recorded when a handler throws (framework-terminated failure).</summary>
    public static string Failed(IReadOnlyList<string> targetIds, string message)
        => Serialize(SplashReport.Create().Failed(targetIds, message).Items);

    /// <summary>
    /// The stand-in report for an outcome whose own report the database refused to store (see
    /// <c>SettlementWriter</c>): one targetless item that keeps the attempt's <paramref name="outcome"/> and says
    /// why the real report is missing, so the ripple still settles as what it was.
    /// </summary>
    public static string Unstorable(SplashOutcome outcome, string reason)
        => Serialize([new SplashReportItem(outcome, $"report could not be stored: {reason}", [])]);

    // Every report is serialized here, so this is where text the database cannot hold is taken out. Postgres
    // rejects a NUL anywhere in a jsonb string, and these strings are not ours: an exception message or a
    // handler's output can carry one (binary data echoed into an error is the usual source). Left in, the
    // settlement write for that attempt fails on every retry.
    private static string Serialize(IReadOnlyList<SplashReportItem> items)
        => JsonSerializer.Serialize(
            items.Any(i => HasNul(i.Output) || i.TargetIds.Any(HasNul))
                ? items.Select(i => new SplashReportItem(i.Outcome, StripNul(i.Output), i.TargetIds.Select(StripNul).ToList()!)).ToList()
                : items,
            RippleJson.Options);

    private static bool HasNul(string? text) => text is not null && text.Contains('\0');

    private static string? StripNul(string? text) => text?.Replace("\0", "");
}
