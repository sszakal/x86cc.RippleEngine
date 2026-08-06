using System.Text.Json;
using System.Text.Json.Serialization;
using x86cc.RippleEngine.Core;

namespace x86cc.RippleEngine.Engine;

/// <summary>
/// Turns a handler's returned <see cref="SplashReport"/> into the per-attempt report persisted on the splash's
/// <c>report</c> jsonb. Report-by-exception: any target the handler didn't mention is inferred succeeded /
/// no-output (the report already aggregates by <c>(outcome, output)</c>). Also decides whether the attempt
/// failed (any target reported <see cref="SplashOutcome.Failed"/>).
/// </summary>
internal static class SplashReportBuilder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

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
        var json = items.Count == 0 ? null : JsonSerializer.Serialize(items, Json);
        return (json, report.HasFailures);
    }

    /// <summary>The all-targets-failed report recorded when a handler throws (framework-terminated failure).</summary>
    public static string Failed(IReadOnlyList<string> targetIds, string message)
        => JsonSerializer.Serialize(SplashReport.Create().Failed(targetIds, message).Items, Json);
}
