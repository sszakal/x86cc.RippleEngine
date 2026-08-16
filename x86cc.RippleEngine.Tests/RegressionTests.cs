using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;
using x86cc.RippleEngine.Storage.Migrations;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// Storage-level regressions for defects that had no coverage. Each test fails against the pre-fix code.
/// </summary>
public sealed class RegressionTests : RippleTestBase
{
    /// <summary>
    /// Graceful deregistration must actually delete the heartbeat. It used to also delete from a
    /// <c>type_active</c> table that the migration never created, and because Npgsql runs a multi-statement
    /// command in ONE implicit transaction, the 42P01 rolled the heartbeat delete back too — so EVERY graceful
    /// shutdown left a stale row and peers waited out the full HeartbeatTimeout before a pointless recovery sweep.
    /// The dispatcher swallows the failure as a warning, which is why nothing surfaced it.
    /// </summary>
    [Fact]
    public async Task removing_an_instance_deletes_its_heartbeat()
    {
        await ResetAsync();
        await Engine.BeatAsync("inst-gone", 0);
        (await ScalarAsync("select count(*) from ripple.instance_heartbeat where instance_id = 'inst-gone'"))
            .ShouldBe(1);

        await Engine.RemoveInstanceAsync("inst-gone");

        (await ScalarAsync("select count(*) from ripple.instance_heartbeat where instance_id = 'inst-gone'"))
            .ShouldBe(0);
    }

    /// <summary>
    /// A report item with NO target ids must survive compaction. <c>compact_wave</c> exploded targetIds with a
    /// CROSS join lateral, which drops the whole row for an empty array — silently discarding every targetless
    /// item. Both recovery paths write 'targetIds': [] (Abandoned), and a framework-terminated failure before
    /// target resolution (no handler registered for the type_key, payload deserialization throwing) produces one
    /// too. A wave whose ripples all failed that way compacted to a completely EMPTY report.
    /// </summary>
    [Fact]
    public async Task compaction_preserves_report_items_with_no_target_ids()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 1);

        var claimed = await Engine.PollAsync(10, "inst-1", 0);
        claimed.Count.ShouldBe(1);

        // Exactly the shape SplashReportBuilder.Failed emits when the failure predates target resolution.
        await Splashes.FailRipplesAsync(
            [new RippleFailure(claimed[0].Id, wave.Id, claimed[0].Attempt, DateTimeOffset.UtcNow,
                """[{"outcome":"Failed","output":"No handler registered for type key 'X|Y'.","targetIds":[]}]""",
                Terminal: true, null)],
            "inst-1");

        await RefreshWaveStatsAsync();
        (await Engine.GetWaveAsync(wave.Id))!.Status.ShouldBe(WaveStatus.Faulted);

        await CompactWaveAsync(wave.Id);

        (await ScalarAsync("select count(*) from ripple.report_chunk where wave_id = @waveId", new { waveId = wave.Id }))
            .ShouldBe(1, "the targetless failure must still be reported, not silently dropped");
        (await ScalarAsync(
            "select count(*) from ripple.report_chunk " +
            "where wave_id = @waveId and items::text like '%No handler registered%'", new { waveId = wave.Id }))
            .ShouldBe(1);
        // It describes no targets, so it contributes 0 to the target count — but the item itself is retained.
        (await ScalarAsync("select coalesce(sum(target_count), -1) from ripple.report_chunk where wave_id = @waveId",
            new { waveId = wave.Id })).ShouldBe(0);
    }

    /// <summary>
    /// Resume must not declare a type active while Paused rows remain. The drain chunk selects
    /// <c>for update skip locked</c>, so a short chunk only means "fewer than chunkSize UNLOCKED rows" — it is not
    /// proof the set is empty. Treating it as proof flipped pause_state to 'active' with rows still Paused, and
    /// since the reconcile only revisits paused/resuming_* types, those ripples were stranded FOREVER: the claim
    /// only sees state='Pending', so their wave never drains and never compacts.
    /// Here a concurrent transaction holds one Paused row locked so skip locked cannot see it.
    /// </summary>
    [Fact]
    public async Task resume_does_not_activate_a_type_while_skip_locked_hides_paused_rows()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 4, "TypeA");
        const string typeKey = "RecalcContext|TypeA";

        await Engine.PauseTypeAsync(typeKey);
        await ReconcilePauseTransitionsAsync();
        (await ScalarAsync(
            "select count(*) from ripple.ripple where type_key = @typeKey and state = 'Paused'", new { typeKey }))
            .ShouldBe(4);

        // Hold ONE paused row locked in an open transaction — skip locked will pass over it.
        await using var blocker = await Db.OpenConnectionAsync();
        await using var blockerTx = await blocker.BeginTransactionAsync();
        var lockedId = await blocker.ExecuteScalarAsync<Guid>(
            "select id from ripple.ripple where type_key = @typeKey and state = 'Paused' " +
            "order by schedule_order limit 1 for update",
            new { typeKey }, blockerTx);

        await Engine.ResumeTypeAsync(typeKey, rebase: false);
        await ReconcilePauseTransitionsAsync();

        // The locked row is still parked, so the type must NOT have been declared active.
        (await ScalarAsync("select count(*) from ripple.ripple where id = @lockedId and state = 'Paused'",
            new { lockedId })).ShouldBe(1);
        var pauseState = await StringAsync(
            "select pause_state from ripple.type_schedule where type_key = @typeKey", new { typeKey });
        pauseState.ShouldBe("resuming_asis", "a type with Paused rows left must stay resuming for the next pass");

        // Once the lock is released the next pass finishes the job and flips it active.
        await blockerTx.RollbackAsync();
        await blocker.CloseAsync();
        await ReconcilePauseTransitionsAsync();

        (await ScalarAsync("select count(*) from ripple.ripple where type_key = @typeKey and state = 'Paused'",
            new { typeKey })).ShouldBe(0);
        (await StringAsync("select pause_state from ripple.type_schedule where type_key = @typeKey", new { typeKey }))
            .ShouldBe("active");
    }

    /// <summary>
    /// Down() must drop every table Up() creates, or a MigrateDown→MigrateUp round-trip fails with
    /// "relation ripple_type_metric already exists". It was the one table missing from the teardown.
    /// </summary>
    [Fact]
    public async Task migration_round_trips_down_and_up()
    {
        await ResetAsync();

        // The migration runner is scoped (FluentMigrator registers it that way), so resolve it from a scope
        // rather than the container root.
        using var scope = Storage.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<FluentMigrator.Runner.IMigrationRunner>();
        runner.MigrateDown(0);

        // version_info is FluentMigrator's own bookkeeping and is meant to survive a down-migration; every
        // table M0001 creates must not.
        (await ScalarAsync(
            "select count(*) from information_schema.tables " +
            "where table_schema = 'ripple' and table_name <> 'version_info'"))
            .ShouldBe(0, "Down() must leave no table behind");

        Should.NotThrow(() => runner.MigrateUp());

        // Back to a full schema — including the DEFAULT row the hot-path SQL falls back to.
        (await ScalarAsync(
            $"select count(*) from ripple.type_schedule where type_key = '{RippleTypeKey.Default}'")).ShouldBe(1);
    }

    /// <summary>
    /// Resetting a type whose ripples are parked must be REFUSED. The row is the pause state machine: delete it
    /// while ripples sit in 'Paused' and nothing can un-park them — the reconcile only visits paused/resuming_*
    /// types and the claim only sees 'Pending' — so they are unclaimable forever, their wave never completes and
    /// never compacts, and nothing logs a thing. The dashboard's Reset button reached this directly.
    /// </summary>
    [Fact]
    public async Task resetting_a_paused_type_is_refused_so_its_parked_ripples_are_not_stranded()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 3, "TypeA");
        const string typeKey = "RecalcContext|TypeA";

        await SeedScheduleAsync(typeKey, batchSize: 1, gapSeconds: 1);
        await Engine.PauseTypeAsync(typeKey);
        await ReconcilePauseTransitionsAsync();
        (await ScalarAsync("select count(*) from ripple.ripple where type_key = @typeKey and state = 'Paused'",
            new { typeKey })).ShouldBe(3);

        (await Engine.DeleteTypeScheduleAsync(typeKey)).ShouldBe(TypeScheduleDeleteResult.PauseInProgress);
        (await ScalarAsync("select count(*) from ripple.type_schedule where type_key = @typeKey", new { typeKey }))
            .ShouldBe(1, "the row must survive, or the parked ripples lose their only route back to Pending");

        // Once fully resumed the reset is allowed again.
        await Engine.ResumeTypeAsync(typeKey, rebase: false);
        await ReconcilePauseTransitionsAsync();
        (await Engine.DeleteTypeScheduleAsync(typeKey)).ShouldBe(TypeScheduleDeleteResult.Deleted);
    }

    /// <summary>
    /// Pausing a type that was inheriting the DEFAULT batch/gap must not silently freeze those values. The
    /// materialising insert used to copy the DEFAULT's concrete numbers into the new row, so the type stopped
    /// tracking later DEFAULT edits and began reporting itself as configured. Null means inherit.
    /// </summary>
    [Fact]
    public async Task pausing_an_inheriting_type_keeps_it_inheriting()
    {
        await ResetAsync();
        const string typeKey = "RecalcContext|TypeA";

        await Engine.PauseTypeAsync(typeKey); // no prior config — the type was inheriting
        var row = await StringAsync(
            "select coalesce(batch_size::text, 'null') || '/' || coalesce(gap_seconds::text, 'null') " +
            "from ripple.type_schedule where type_key = @typeKey", new { typeKey });
        row.ShouldBe("null/null", "the row exists only to carry pause_state; scheduling values must stay inherited");

        // Proof it still tracks the DEFAULT: change the default, resume, and fan out — the new ripples must be
        // spaced by the NEW default gap.
        await Engine.UpsertTypeScheduleAsync(RippleTypeKey.Default, batchSize: 1, gapSeconds: 50, maxAttempts: 5);
        await Engine.ResumeTypeAsync(typeKey, rebase: false);
        await ReconcilePauseTransitionsAsync();

        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 2, "TypeA");
        var spread = await DoubleAsync(
            "select max(schedule_order) - min(schedule_order) from ripple.ripple where type_key = @typeKey",
            new { typeKey });
        spread.ShouldBe(50, 0.001, "batch 1 / gap 50 from the edited DEFAULT row must apply to the paused type too");
    }

    /// <summary>
    /// A continuation must land AFTER the wave's existing tail batch, not on it. The base was the max
    /// schedule_order of the wave's pending rows, and <c>base + (k/batch)*gap</c> gives exactly <c>base</c> for
    /// k = 0 — so expanding a wave doubled up the tail slot instead of appending a new one.
    /// </summary>
    [Fact]
    public async Task continuing_a_wave_appends_after_the_tail_slot_instead_of_onto_it()
    {
        await ResetAsync();
        const string typeKey = "RecalcContext|TypeA";
        await SeedScheduleAsync(typeKey, batchSize: 1, gapSeconds: 10);

        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 2, "TypeA");
        var tail = await DoubleAsync("select max(schedule_order) from ripple.ripple where wave_id = @waveId",
            new { waveId = wave.Id });

        await SeedRipplesOfTypeAsync(wave.Id, 2, "TypeA"); // the continuation

        var newMin = await DoubleAsync(
            "select min(schedule_order) from ripple.ripple where wave_id = @waveId and schedule_order > @tail",
            new { waveId = wave.Id, tail });
        newMin.ShouldBeGreaterThan(tail, "the continuation must start past the existing tail");
        // batch 1 ⇒ one ripple per slot ⇒ four ripples must occupy four distinct slots.
        (await ScalarAsync("select count(distinct schedule_order) from ripple.ripple where wave_id = @waveId",
            new { waveId = wave.Id })).ShouldBe(4);
    }

    /// <summary>
    /// A chunked rebase-resume must ADVANCE across chunks. The base used to be the bare frontier — a MINIMUM —
    /// so once chunk 1 landed its rows the frontier was unchanged and every later chunk re-stamped onto the same
    /// window, piling the whole pass onto a handful of slots. That is exactly the catch-up herd the rebase
    /// exists to prevent.
    /// </summary>
    [Fact]
    public async Task chunked_rebase_resume_advances_instead_of_stacking_every_chunk_on_one_slot()
    {
        await ResetAsync();
        const string typeKey = "RecalcContext|TypeA";
        await SeedScheduleAsync(typeKey, batchSize: 1, gapSeconds: 10);

        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 6, "TypeA");
        await Engine.PauseTypeAsync(typeKey);
        await ReconcilePauseTransitionsAsync();

        await Engine.ResumeTypeAsync(typeKey, rebase: true);
        await ReconcilePauseTransitionsAsync(chunkSize: 2); // 3 chunks of 2

        (await ScalarAsync("select count(*) from ripple.ripple where type_key = @typeKey and state = 'Pending'",
            new { typeKey })).ShouldBe(6);

        // Assert SPACING, not distinctness. Distinctness is far too weak here: the frontier is
        // greatest(now(), min(pending)), and the wall clock advances a few ms between chunks, so even a fully
        // stacked resume yields six *technically distinct* values — arranged as three pairs a millisecond apart
        // rather than six slots a gap apart. batch 1 / gap 10 means every consecutive pair must be a full gap.
        var minSpacing = await DoubleAsync(
            """
            select min(d) from (
                select schedule_order - lag(schedule_order) over (order by schedule_order) as d
                from ripple.ripple where type_key = @typeKey
            ) s where d is not null
            """, new { typeKey });
        minSpacing.ShouldBeGreaterThan(9.0,
            "each chunk must append a full gap after the previous one, not restack on the same frontier");
    }

    /// <summary>
    /// Pausing a large type must not starve every other type's resume. Both directions shared one
    /// <c>maxRowsPerPass</c> budget and pausing ran first, so a big pause consumed the whole pass and no resume
    /// made progress — for as long as the pause took, the resumed work stayed invisible to the claim.
    /// </summary>
    [Fact]
    public async Task a_large_pause_does_not_starve_a_concurrent_resume()
    {
        await ResetAsync();
        const string pausingKey = "RecalcContext|TypeA";
        const string resumingKey = "RecalcContext|TypeB";

        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 10, "TypeA");
        await SeedRipplesOfTypeAsync(wave.Id, 4, "TypeB");

        // TypeB: paused and parked, then asked to resume.
        await Engine.PauseTypeAsync(resumingKey);
        await ReconcilePauseTransitionsAsync();
        await Engine.ResumeTypeAsync(resumingKey, rebase: false);

        // TypeA: newly paused, with plenty of rows to park — enough to eat a shared budget whole.
        await Engine.PauseTypeAsync(pausingKey);

        await ReconcilePauseTransitionsAsync(chunkSize: 2, maxRowsPerPass: 4);

        (await ScalarAsync("select count(*) from ripple.ripple where type_key = @k and state = 'Pending'",
            new { k = resumingKey }))
            .ShouldBeGreaterThan(0, "the resume must get its own share of the pass rather than being starved");
    }

    /// <summary>
    /// A nonsense option must fail at startup rather than degrade into a silent permanent outage.
    /// <c>ReportChunkSize = 0</c> is the divisor in <c>compact_wave</c>'s chunking: it made every compaction
    /// raise "division by zero", which CompactionLoop just logged and retried forever, so ripple/splash rows
    /// were never reclaimed and the tables grew without bound.
    /// </summary>
    [Fact]
    public void invalid_engine_options_are_rejected_rather_than_failing_forever_at_runtime()
    {
        using var host = BuildEngineHost(
            engine => engine.AddHandler<RecalcContext, RecalcCompany, NoopHandler>(),
            o => o.ReportChunkSize = 0);

        var ex = Should.Throw<OptionsValidationException>(
            () => host.Services.GetRequiredService<IOptions<RippleEngineOptions>>().Value);
        ex.Message.ShouldContain(nameof(RippleEngineOptions.ReportChunkSize));
    }

    /// <summary>A handler that is never invoked — the options above are rejected before anything runs.</summary>
    private sealed class NoopHandler : IRippleHandler<RecalcContext, RecalcCompany>
    {
        public Task<SplashReport?> Execute(RecalcContext wave, RecalcCompany ripple, IRippleContext context)
            => Task.FromResult<SplashReport?>(null);
    }

    /// <summary>
    /// Adding ripples to a wave that does not exist must FAIL, not commit orphans. There is no FK on
    /// <c>ripple.wave_id</c>, so the insert used to succeed with a degenerate <c>"|rippleType"</c> type_key while
    /// the trailing <c>update wave set ripple_count</c> matched zero rows — silent on every path. The orphans are
    /// not inert: <c>PollSql</c> inner-joins <c>wave</c>, so the claim flips them to Running and burns an attempt
    /// each pass while returning nothing, cycling until the retry ceiling poison-fails them.
    /// </summary>
    [Fact]
    public async Task adding_ripples_to_a_missing_wave_fails_instead_of_committing_orphans()
    {
        await ResetAsync();
        var ghost = Guid.NewGuid();

        await Should.ThrowAsync<InvalidOperationException>(
            () => Engine.AddRipplesAsync(ghost, [new RippleSeed("{}", "RecalcCompany")]));

        (await ScalarAsync("select count(*) from ripple.ripple where wave_id = @ghost", new { ghost }))
            .ShouldBe(0, "no ripple may be committed for a wave that does not exist");
    }

    /// <summary>
    /// One huge type must not consume a whole direction's reconcile budget. Splitting the budget only BETWEEN
    /// directions left the identical starvation one level down: a running total shared across type_keys let the
    /// first type spend everything, so a second type resumed in the meantime moved zero rows and its ripples
    /// stayed 'Paused' — invisible to the claim, counted in <c>paused</c>, its waves never completing.
    /// </summary>
    [Fact]
    public async Task one_large_type_does_not_consume_the_whole_resume_budget()
    {
        await ResetAsync();
        const string bigKey = "RecalcContext|TypeA";
        const string smallKey = "RecalcContext|TypeB";

        var wave = await CreateWaveAsync();
        await SeedRipplesOfTypeAsync(wave.Id, 10, "TypeA");
        await SeedRipplesOfTypeAsync(wave.Id, 2, "TypeB");

        foreach (var key in new[] { bigKey, smallKey })
        {
            await Engine.PauseTypeAsync(key);
        }

        await ReconcilePauseTransitionsAsync();
        await Engine.ResumeTypeAsync(bigKey, rebase: false);
        await Engine.ResumeTypeAsync(smallKey, rebase: false);

        // Only resumes are pending, so the direction gets the whole budget of 4 — split 2 per type.
        await ReconcilePauseTransitionsAsync(chunkSize: 2, maxRowsPerPass: 4);

        (await ScalarAsync("select count(*) from ripple.ripple where type_key = @k and state = 'Pending'",
            new { k = smallKey }))
            .ShouldBeGreaterThan(0, "the smaller type must get its own slice rather than being starved by the big one");
    }

    /// <summary>
    /// Settlement must fence on the ATTEMPT, not just owner + state. An instance that stalls past
    /// HeartbeatTimeout while still inside ExecutionTimeout gets its ripple reclaimed, then resumes, re-registers
    /// under the SAME instance id, and can re-claim that very ripple as a new attempt. Its stale outcome then
    /// found <c>claimed_by = me</c> and <c>state = 'Running'</c> both true of the NEW attempt and settled it out
    /// from under a still-running handler — after which the real attempt's own settlement silently no-opped.
    /// </summary>
    [Fact]
    public async Task a_superseded_attempts_outcome_does_not_settle_the_ripple_it_was_reclaimed_from()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 1);

        // Attempt 1, claimed by 'inst-1'.
        var first = await Engine.PollAsync(10, "inst-1", 0);
        first.Count.ShouldBe(1);
        first[0].Attempt.ShouldBe(1);

        // 'inst-1' goes silent long enough to be declared dead; a peer requeues its work.
        await ExecuteAsync(
            "update ripple.instance_heartbeat set last_seen_at = now() - interval '1 hour' where instance_id = 'inst-1'");
        (await Engine.RecoverStaleAsync(TimeSpan.FromMinutes(1), selfInstanceId: "survivor")).ShouldBe(1);

        // …but it was only stalled, not dead: it comes back under the same id and re-claims the same ripple.
        var second = await Engine.PollAsync(10, "inst-1", 0);
        second.Count.ShouldBe(1);
        second[0].Id.ShouldBe(first[0].Id);
        second[0].Attempt.ShouldBe(2, "the requeue+reclaim must have spent another attempt");

        // Now attempt 1's outcome finally flushes. Same ripple, same owner, and the row IS Running — only the
        // attempt distinguishes it from the live attempt 2.
        await Splashes.CompleteRipplesAsync(
            [new RippleCompletion(first[0].Id, wave.Id, first[0].Attempt, Ago(1), null)], "inst-1");

        (await ScalarAsync("select count(*) from ripple.ripple where id = @id and state = 'Running'",
            new { id = first[0].Id }))
            .ShouldBe(1, "the stale outcome must not settle an attempt that is still executing");
        (await ScalarAsync("select count(*) from ripple.splash where ripple_id = @id and outcome = 'Succeeded'",
            new { id = first[0].Id }))
            .ShouldBe(0, "a no-op update must not produce a splash either");

        // Attempt 2's own outcome still settles normally.
        await Splashes.CompleteRipplesAsync(
            [new RippleCompletion(second[0].Id, wave.Id, second[0].Attempt, Ago(1), null)], "inst-1");
        (await ScalarAsync("select count(*) from ripple.ripple where id = @id and state = 'Succeeded'",
            new { id = first[0].Id })).ShouldBe(1);
    }

    /// <summary>
    /// The same fence, on the failure paths: a superseded attempt must neither terminally fail nor requeue a
    /// ripple that has since been re-claimed.
    /// </summary>
    [Fact]
    public async Task a_superseded_attempts_failure_does_not_settle_the_ripple_it_was_reclaimed_from()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 1);

        var first = await Engine.PollAsync(10, "inst-1", 0);
        await ExecuteAsync(
            "update ripple.instance_heartbeat set last_seen_at = now() - interval '1 hour' where instance_id = 'inst-1'");
        await Engine.RecoverStaleAsync(TimeSpan.FromMinutes(1), selfInstanceId: "survivor");
        var second = await Engine.PollAsync(10, "inst-1", 0);
        second[0].Attempt.ShouldBe(2);

        // Terminal failure from the dead attempt…
        await Splashes.FailRipplesAsync(
            [new RippleFailure(first[0].Id, wave.Id, first[0].Attempt, Ago(1), null, Terminal: true, null)],
            "inst-1");
        // …and a requeue from it.
        await Splashes.FailRipplesAsync(
            [new RippleFailure(first[0].Id, wave.Id, first[0].Attempt, Ago(1), null,
                Terminal: false, DateTimeOffset.UtcNow.AddMinutes(5))],
            "inst-1");

        (await ScalarAsync("select count(*) from ripple.ripple where id = @id and state = 'Running'",
            new { id = first[0].Id }))
            .ShouldBe(1, "neither stale failure may move a ripple whose live attempt is still executing");
        (await ScalarAsync("select count(*) from ripple.splash where ripple_id = @id and outcome = 'Failed'",
            new { id = first[0].Id })).ShouldBe(0);
    }

    /// <summary>
    /// A settlement batch carrying BOTH a superseded attempt and the live attempt of the same ripple must splash
    /// only the one that transitioned. The transitioned set was keyed by ripple id alone, so the live attempt's
    /// success vouched for the stale entry too and wrote a phantom splash for an attempt that never settled —
    /// inflating retry_count and the compaction rollups.
    /// </summary>
    [Fact]
    public async Task a_batch_holding_both_attempts_of_one_ripple_splashes_only_the_live_one()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 1);

        var first = await Engine.PollAsync(10, "inst-1", 0);
        await ExecuteAsync(
            "update ripple.instance_heartbeat set last_seen_at = now() - interval '1 hour' where instance_id = 'inst-1'");
        await Engine.RecoverStaleAsync(TimeSpan.FromMinutes(1), selfInstanceId: "survivor");
        var second = await Engine.PollAsync(10, "inst-1", 0);
        second[0].Attempt.ShouldBe(2);

        // Both outcomes flush together — same ripple id, different attempts.
        await Splashes.CompleteRipplesAsync(
            [
                new RippleCompletion(first[0].Id, wave.Id, first[0].Attempt, Ago(1), null),
                new RippleCompletion(second[0].Id, wave.Id, second[0].Attempt, Ago(1), null)
            ], "inst-1");

        (await ScalarAsync("select count(*) from ripple.ripple where id = @id and state = 'Succeeded'",
            new { id = first[0].Id })).ShouldBe(1);
        (await ScalarAsync("select count(*) from ripple.splash where ripple_id = @id and outcome = 'Succeeded'",
            new { id = first[0].Id }))
            .ShouldBe(1, "only the attempt that actually transitioned may produce a splash");
        (await ScalarAsync("select count(*) from ripple.splash where ripple_id = @id and attempt = 1",
            new { id = first[0].Id }))
            .ShouldBe(1, "attempt 1 has only its Abandoned recovery splash, not a phantom Succeeded one");
    }

    /// <summary>
    /// <c>duration_ms</c> must measure the HANDLER, not the settlement write. It was computed from a timestamp
    /// sampled inside the splash insert, which runs after the outcome has queued for its batch and through any
    /// settlement retry backoff — so a stalled settlement inflated the value that compaction rolls into
    /// <c>wave.avg_duration_ms</c> and <c>ripple_type_metric.avg_ms</c>, both documented as execution time.
    /// </summary>
    [Fact]
    public async Task duration_measures_the_handler_not_the_settlement_write()
    {
        await ResetAsync();
        var wave = await CreateWaveAsync();
        await SeedRipplesAsync(wave.Id, 1);
        var claimed = await Engine.PollAsync(10, "inst-1", 0);

        // A 200ms handler whose outcome then sat for an hour before being written.
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var endedAt = startedAt.AddMilliseconds(200);
        await Splashes.CompleteRipplesAsync(
            [new RippleCompletion(claimed[0].Id, wave.Id, claimed[0].Attempt, startedAt, null, endedAt)], "inst-1");

        var duration = await ScalarAsync("select duration_ms from ripple.splash where ripple_id = @id",
            new { id = claimed[0].Id });
        duration.ShouldBeInRange(150L, 250L, "the settlement delay must not be counted as execution time");
    }

    /// <summary>
    /// A config that can't produce a usable schedule must be refused at the door. <c>batch_size = 0</c> is the
    /// divisor in <c>base + (k / batch_size) * gap_seconds</c>, so it made EVERY fan-out for the type raise
    /// "division by zero" forever, with a clean startup and nothing to point at; a negative gap inverts the
    /// ordering so the type's later batches sort ahead of the global frontier and starve every other job.
    /// </summary>
    [Fact]
    public async Task a_poison_type_schedule_is_refused_rather_than_written()
    {
        await ResetAsync();
        const string typeKey = "RecalcContext|TypeA";

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => Engine.UpsertTypeScheduleAsync(typeKey, batchSize: 0, gapSeconds: 1));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => Engine.UpsertTypeScheduleAsync(typeKey, batchSize: 10, gapSeconds: -1));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => Engine.UpsertTypeScheduleAsync(typeKey, batchSize: 10, gapSeconds: 1, maxAttempts: 0));
        // The insert-if-absent seed path (startup) is guarded identically.
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => Engine.SeedTypeScheduleAsync(typeKey, batchSize: 0, gapSeconds: 1));

        // Zero gap is rejected too: it collapses every batch of the type onto one slot, so the type monopolises
        // the claim (which is just `order by schedule_order`) until fully drained. The dashboard PUT already
        // refuses it; the guard now agrees.
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => Engine.UpsertTypeScheduleAsync(typeKey, batchSize: 10, gapSeconds: 0));

        (await ScalarAsync("select count(*) from ripple.type_schedule where type_key = @typeKey", new { typeKey }))
            .ShouldBe(0, "no poison row may reach the table");

        await Should.NotThrowAsync(() => Engine.UpsertTypeScheduleAsync(typeKey, batchSize: 10, gapSeconds: 0.5));
    }

    /// <summary>
    /// The collection generator's <c>Create</c> disposed the wave payload it had just attached to the wave it
    /// returns — and <c>JsonDocument.Dispose()</c> returns the backing buffer to <c>ArrayPool</c>, so a caller
    /// reading <c>wave.Payload</c> got an ObjectDisposedException, or silently corrupt JSON once another thread
    /// rented that array.
    /// </summary>
    [Fact]
    public async Task creating_a_collection_wave_does_not_return_a_disposed_payload()
    {
        await ResetAsync();

        var wave = await CollectionGenerator
            .Create("VAT26 changed", new RecalcContext { LegislationCode = "VAT26" })
            .AddRipples(new[] { new RecalcCompany { CompanyId = Guid.NewGuid() } })
            .DispatchAsync();

        // Detached, matching the query-source builders (WaveBuilderBase returns a wave with no Payload) — the
        // point is that touching it can never hit a returned-to-pool buffer.
        wave.Payload.ShouldBeNull();

        // The payload still reached the database intact, which is what the document was serialized for.
        (await StringAsync("select payload->>'legislationCode' from ripple.wave where id = @id", new { id = wave.Id }))
            .ShouldBe("VAT26");
    }

    private static DateTimeOffset Ago(int seconds) => DateTimeOffset.UtcNow.AddSeconds(-seconds);

    private async Task<string?> StringAsync(string sql, object? p = null)
    {
        await using var conn = await Db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<string>(sql, p);
    }
}
