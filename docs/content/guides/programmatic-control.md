---
title: Programmatic control
sidebar_position: 6
---

# Programmatic control

Everything the [dashboard](/operations/dashboard) does goes through two storage interfaces you can inject
into your own code. They are registered by `AddRippleEngine` on every host, including a creation-only host
with `EnableWorkers = false`:

| Interface | Namespace | Use for |
|---|---|---|
| `IEngineStore` | `x86cc.RippleEngine.Storage` | reading a wave, pausing/resuming types, editing type schedules, rescheduling a live wave |
| `IReportStore` | `x86cc.RippleEngine.Storage` | reading a finished wave's aggregated report |

The rest of `IEngineStore` (poll, heartbeat, recovery, stats refresh, low-level ripple inserts) is the
engine's own plumbing. Don't call it from application code: create waves through a
[generator](/guides/fan-out), and let the engine run the loops.

## Tracking a wave

`DispatchAsync` returns the created `Wave`. Keep its `Id` and read it back with `GetWaveAsync`:

```csharp
public async Task<Wave> WaitForWaveAsync(IEngineStore engine, Guid waveId, CancellationToken ct)
{
    while (true)
    {
        var wave = await engine.GetWaveAsync(waveId, ct)
                   ?? throw new InvalidOperationException($"wave {waveId} not found (purged?)");
        if (wave.Status != WaveStatus.Active)
            return wave;                                          // Completed or Faulted
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }
}
```

| `Wave` member | Meaning |
|---|---|
| `Status` | `Active` → `Completed`, or `Faulted` if any ripple failed terminally |
| `RippleCount` | ripples ever created, which grows with [in-flight expansion](/guides/expansion) |
| `Pending` / `Running` / `Paused` / `Failed` | recomputed from the ripple rows by the [stats refresh](/architecture/stats-and-completion) |
| `Succeeded` | derived: `RippleCount − Pending − Running − Paused − Failed` |
| `RetryCount` | re-execution attempts (splashes with attempt > 1) |
| `DrainRate` | ripples settled per second right now (a ~30s moving average); null unless Active and measured |
| `EstimatedSecondsRemaining` | `(Pending + Running) / DrainRate`: seconds to the estimated finish; null when not measurable or only paused work is left |
| `CreatedAt` / `CompletedAt` | lifecycle timestamps |
| `Payload` / `PayloadType` / `Type` | the shared wave payload and its type |
| `IdempotencyKey` | the key the wave was created under, if any |

The numbers are **eventually consistent**, up to one `WaveStatsRefreshInterval` (default 2s) stale. Before the
first refresh, every ripple reads as pending. A wave that is not found has either never existed or been purged
by [retention](/guides/configuration#retention).

:::note A paused type holds its waves open
`Paused` ripples count as not done. A wave with ripples of a paused type stays `Active` until the type is
resumed and those ripples drain.
:::

## Pausing and resuming a type

```csharp
await engine.PauseTypeAsync("TaxChange|RecalcCompany");
// …
await engine.ResumeTypeAsync("TaxChange|RecalcCompany");               // rebase: true (the default)
await engine.ResumeTypeAsync("TaxChange|RecalcCompany", rebase: false); // keep the original queue position
```

The `type_key` is `{WaveType}|{RippleType}`, each half from `RippleTypeName.Of<T>()`.

- **Pause** takes effect at once: the claim skips the type immediately, and new work of that type (fan-out,
  retries, recovery) is parked as `Paused`. Existing `Pending` ripples are moved to `Paused` in bounded
  background chunks. `Running` ripples are never paused; they finish their attempt.
- **Resume** moves the ripples back to `Pending`, also in chunks. With `rebase: true`, the default, each chunk
  is re-stamped onto the current global frontier, so resumed work **interleaves** fairly with everything
  queued meanwhile. With `rebase: false` the original `schedule_order` is kept, and because that position is now
  far in the past, the resumed job runs **ahead of everything** until it catches up.

## Editing a type's schedule

```csharp
await engine.UpsertTypeScheduleAsync("TaxChange|RecalcCompany", batchSize: 200, gapSeconds: 1, maxAttempts: 3);

var result = await engine.DeleteTypeScheduleAsync("TaxChange|RecalcCompany");   // revert to __default__
```

`UpsertTypeScheduleAsync` overwrites the row, and the change survives restarts: the startup seeder from
`AddHandler` only inserts if the row is absent. Pass `RippleTypeKey.Default` (`__default__`) to edit the
fallback row. Batch and gap apply to **future** fan-outs only, because they are baked into `schedule_order`.
`maxAttempts` applies at the next claim.

`DeleteTypeScheduleAsync` returns a `TypeScheduleDeleteResult`:

| Result | Meaning |
|---|---|
| `Deleted` | the row was removed; the type inherits `__default__` again |
| `NotFound` | no row existed, or you passed `__default__` (which can never be deleted) |
| `PauseInProgress` | refused: the type is paused or resuming, and its row holds that state. Resume first |

Validate input with `TypeScheduleGuard.Check` and `RippleTypeKey.IsWellFormed`, the same rules the dashboard
API applies.

## Rescheduling a live wave

Changing a type's schedule doesn't touch ripples that already exist. To change the throughput share of a wave
that is **already fanned out**, re-stamp its remaining `Pending` ripples:

```csharp
int? n = await engine.RescheduleWaveAsync(waveId, batchSize: 50, gapSeconds: 5);           // every type in the wave
int? m = await engine.RescheduleWaveAsync(waveId, 50, 5, typeKey: "TaxChange|RecalcCompany"); // just one
if (n is null) { /* another reschedule holds the lock: retry shortly */ }
```

The wave keeps its place in the queue; only its density changes. `Running` ripples, retry requeues and later
expansions aren't covered. It runs as one transaction, which is the known ceiling for multi-million-ripple
waves (see [the scheduler](/architecture/scheduler#rescheduling-a-live-wave)).

## Reading a report

After a wave goes terminal, [compaction](/architecture/stats-and-completion#compaction-and-retention) rolls its
splashes into aggregated report chunks. Read them with `IReportStore`:

```csharp
var report = await reports.GetReportAsync(waveId);
if (report is null)            { /* unknown or purged wave */ }
else if (report.CompactedAt is null) { /* still active, or not yet compacted: try again later */ }
else
{
    foreach (var item in report.Items)          // one item per distinct (outcome, output)
        Console.WriteLine($"{item.Outcome} x{item.TargetIds.Count}: {item.Output}");
}
```

`WaveReport` carries the wave's `Name`, `Status`, `RippleCount`, timestamps, `AvgDurationMs` (mean execution
time over succeeded attempts) and the `Items` (`Outcome`, `Output`, `TargetIds`). The report outlives the
ripple rows and lasts until the wave type's retention expires.

The dashboard's `GET /api/waves/{id}/report.csv` serves the same data as
`outcome,output,target_count,target_ids`, with the ids `;`-separated. It returns `202 Accepted` until the wave
is compacted and `404` for an unknown wave.
