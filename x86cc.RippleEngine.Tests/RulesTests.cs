using System.Text.Json;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// The pure rules the engine and the dashboard lean on — the per-attempt report, the type key, the schedule guard —
/// tested directly: no host, no Postgres. The end-to-end report behaviour (what lands on the splash, retries,
/// faulting the wave) is <see cref="SplashReportTests"/>.
/// </summary>
public sealed class RulesTests
{
    // ---- SplashReportBuilder ------------------------------------------------------------------------

    [Fact]
    public void a_null_report_succeeds_every_target_as_one_item()
    {
        var (report, failed) = SplashReportBuilder.Resolve(["a", "b", "c"], null);

        failed.ShouldBeFalse();
        var item = Items(report).ShouldHaveSingleItem();
        item.Outcome.ShouldBe(SplashOutcome.Succeeded);
        item.Output.ShouldBeNull();
        item.TargetIds.ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public void unreported_targets_join_the_plain_success_item_and_annotated_ones_stay_apart()
    {
        var returned = SplashReport.Create().Success("a").Success("b", "rounded").Failed("c", "no rate");

        var (report, failed) = SplashReportBuilder.Resolve(["a", "b", "c", "d"], returned);

        failed.ShouldBeTrue("any Failed target fails the attempt");
        var items = Items(report);
        items.Count.ShouldBe(3);
        items.Single(i => i is { Outcome: SplashOutcome.Succeeded, Output: null }).TargetIds.ShouldBe(["a", "d"]);
        items.Single(i => i.Output == "rounded").TargetIds.ShouldBe(["b"]);
        items.Single(i => i.Outcome == SplashOutcome.Failed).TargetIds.ShouldBe(["c"]);
    }

    [Fact]
    public void targets_sharing_a_failure_message_aggregate_into_one_item()
    {
        var returned = SplashReport.Create().Failed("a", "timeout").Failed("b", "timeout").Failed("c", "other");

        var (report, _) = SplashReportBuilder.Resolve(["a", "b", "c"], returned);

        Items(report).Single(i => i.Output == "timeout").TargetIds.ShouldBe(["a", "b"]);
    }

    [Fact]
    public void a_ripple_with_no_targets_and_no_report_has_no_report()
    {
        SplashReportBuilder.Resolve([], null).ShouldBe((null, false));
    }

    [Fact]
    public void a_target_the_ripple_did_not_list_is_still_kept()
    {
        var (report, _) = SplashReportBuilder.Resolve(["a"], SplashReport.Create().Failed("extra", "x"));

        Items(report).SelectMany(i => i.TargetIds).ShouldBe(["extra", "a"], ignoreOrder: true);
    }

    [Fact]
    public void a_thrown_handler_fails_every_target_with_the_message()
    {
        var item = Items(SplashReportBuilder.Failed(["a", "b"], "InvalidOperationException: boom")).ShouldHaveSingleItem();

        item.Outcome.ShouldBe(SplashOutcome.Failed);
        item.Output.ShouldBe("InvalidOperationException: boom");
        item.TargetIds.ShouldBe(["a", "b"]);
    }

    // ---- RippleTypeKey -------------------------------------------------------------------------------

    [Theory]
    [InlineData("Tax", "Company", "Tax|Company")]
    [InlineData(null, "Company", "|Company")] // an untyped wave
    [InlineData("Tax", null, "Tax|")]
    public void a_type_key_is_wave_type_bar_ripple_type(string? wave, string? ripple, string expected)
        => RippleTypeKey.Compose(wave, ripple).ShouldBe(expected);

    [Theory]
    [InlineData("Tax|Company", true)]
    [InlineData("|Company", true)]
    [InlineData("Tax|", true)]
    [InlineData(RippleTypeKey.Default, true)]
    [InlineData("Tax", false)]
    [InlineData("a|b|c", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void only_composed_keys_and_the_default_row_are_well_formed(string? key, bool expected)
        => RippleTypeKey.IsWellFormed(key).ShouldBe(expected);

    [Fact]
    public void every_composed_key_is_well_formed_and_never_the_default()
    {
        var key = RippleTypeKey.Compose("A", "B");

        RippleTypeKey.IsWellFormed(key).ShouldBeTrue();
        key.ShouldNotBe(RippleTypeKey.Default);
    }

    // ---- TypeScheduleGuard ---------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 0.001, null)]
    [InlineData(10_000, 3600.0, 1)]
    public void a_usable_schedule_has_no_problem(int batchSize, double gapSeconds, int? maxAttempts)
        => TypeScheduleGuard.Check(batchSize, gapSeconds, maxAttempts).ShouldBeNull();

    [Theory]
    [InlineData(0, 1.0, null, "batchSize")]
    [InlineData(-5, 1.0, null, "batchSize")]
    [InlineData(1, 0.0, null, "gapSeconds")]
    [InlineData(1, -1.0, null, "gapSeconds")]
    [InlineData(1, double.NaN, null, "gapSeconds")]
    [InlineData(1, double.PositiveInfinity, null, "gapSeconds")]
    [InlineData(1, 1.0, 0, "maxAttempts")]
    [InlineData(0, -1.0, 0, "batchSize")] // the first broken rule is the one reported
    public void each_broken_rule_names_its_parameter(int batchSize, double gapSeconds, int? maxAttempts, string param)
    {
        var problem = TypeScheduleGuard.Check(batchSize, gapSeconds, maxAttempts).ShouldNotBeNull();
        problem.ParamName.ShouldBe(param);
        problem.Message.ShouldStartWith(param);

        // Validate throws exactly what Check reports, so the dashboard's 400 and the store's refusal agree.
        Should.Throw<ArgumentOutOfRangeException>(() => TypeScheduleGuard.Validate(batchSize, gapSeconds, maxAttempts))
            .ParamName.ShouldBe(param);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private sealed record Item(SplashOutcome Outcome, string? Output, string[] TargetIds);

    private static List<Item> Items(string? report)
        => JsonSerializer.Deserialize<List<Item>>(report.ShouldNotBeNull(), RippleJson.Options)!;
}
