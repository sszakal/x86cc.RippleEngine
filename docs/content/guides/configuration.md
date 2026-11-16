---
title: Configuration
sidebar_position: 4
---

# Configuration

Everything is configured in one lambda on `AddRippleEngine`. `RippleSetupOptions` derives from
`RippleEngineOptions`, so engine tunables and process-level switches sit side by side.

```csharp
builder.AddRippleEngine(o =>
    {
        o.MaxConcurrency   = 32;
        o.EnableDashboard  = true;
        o.EnableMetrics    = true;
        o.EnableTracing    = true;
        o.DefaultRetention = TimeSpan.FromDays(30);
        o.Retention<CorporateTaxChange>(TimeSpan.FromDays(90));
        o.UseMartenFanOut();
    })
    .AddHandler<TaxChange, RecalcCompany, RecalcHandler>(batchSize: 200, gapSeconds: 1, maxAttempts: 5);
```

## Process switches

| Option | Default | Meaning |
|---|---|---|
| `ConnectionString` | `ConnectionStrings:ripple` | the Postgres the engine coordinates on |
| `AutoMigrate` | `true` | apply the schema migration at startup (advisory-lock-safe on every replica); turn off and call `IServiceProvider.MigrateRipple()` if migrations are a release step |
| `EnableWorkers` | `true` | run the dispatcher, pipeline, recovery, stats, compaction, pause reconcile. `false` = creation-side host |
| `EnableDashboard` | `false` | serve `/api` + the SPA from this process |
| `EnableMetrics` | `false` | publish the `x86cc.RippleEngine` meter (OTLP if `OTEL_EXPORTER_OTLP_ENDPOINT` is set) |
| `EnableTracing` | `false` | publish the `x86cc.RippleEngine` activity source — set on creators *and* workers |
| `ShutdownTimeout` | derived | `HostOptions.ShutdownTimeout`; derived from `ShutdownDrainGrace` + `HandlerCancellationGrace` + `ShutdownTimeoutMargin` (10s) |
| `DefaultRetention` | `null` (forever) | how long a finished wave's report is kept |

## Per-type scheduling

The composite **`type_key = "{waveType}|{rippleType}"`** is both the config key and the handler key.
`AddHandler(batchSize, gapSeconds, maxAttempts)` seeds its `type_schedule` row:

| Setting | Read at | Effect |
|---|---|---|
| `batch_size` | fan-out | how many consecutive ripples share one schedule slot |
| `gap_seconds` | fan-out | distance between slots |
| `max_attempts` | claim / recovery | retry ceiling (`null` → engine default 5) |

A job's steady-state share ≈ `batch_size / gap_seconds`. Keep `batch_size` well under the cluster's total
execution capacity for a *blended* mix. Batch/gap are baked into `schedule_order` at fan-out, so editing them
(on the dashboard) reaches the *next* wave; use **reschedule** to change a live wave. Unconfigured types fall
back to the reserved `__default__` row (1000 / 1s).

## Engine tunables

| Option | Default | Meaning |
|---|---|---|
| `InstanceId` | machine:pid:guid | identity for claims, heartbeat, recovery |
| `MaxConcurrency` | 64 | ripples executing in parallel on this instance |
| `PrefetchFactor` | 2 | pipeline depth = `MaxConcurrency × PrefetchFactor` |
| `ClaimBatchSize` | 128 | max ripples claimed per poll |
| `MinPollDelay` / `MaxPollDelay` | 25ms / 2s | adaptive poll cadence |
| `ExecutionTimeout` | 5 min | per-attempt handler timeout |
| `HandlerCancellationGrace` | 5s | wait after cancel before abandoning a handler |
| `ShutdownDrainGrace` | 15s | let in-flight ripples finish on shutdown |
| `RetryBackoff` / `MaxRetryBackoff` | 2s / 5 min | exponential retry backoff |
| `HeartbeatInterval` / `HeartbeatTimeout` | 3s / 15s | liveness; silent past the timeout ⇒ declared dead |
| `RecoveryInterval` | 10s | dead-instance sweep |
| `SelfReconcileGrace` | 30s | before reclaiming this instance's own stranded claims |
| `WaveStatsRefreshInterval` | 2s | how fresh wave numbers / completion are |
| `CompactionInterval` | 5s | compaction + retention purge cadence |
| `ReportChunkSize` | 10 000 | targets per `report_chunk` row |
| `PauseReconcileChunkSize` / `…MaxRowsPerPass` | 10 000 / 200 000 | bounds on the pause/resume drain |
| `QueueMetricsInterval` | 15s | queue-gauge sampling |

## Retention

A finished wave's report chunks are kept for its retention after completion, then purged. Unconfigured types
fall back to `DefaultRetention`; no default means keep forever.

```csharp
o.DefaultRetention = TimeSpan.FromDays(30);
o.Retention<CorporateTaxChange>(TimeSpan.FromDays(90));   // keyed by the wave payload type
o.RetentionForever<AuditRun>();                           // exempt from the default
```
