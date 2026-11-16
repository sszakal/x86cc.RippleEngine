---
title: Production checklist
sidebar_position: 3
---

# Production checklist

Ripple is **pre-1.0** and has not yet run in production (see [Known limits](/operations/known-limits)). If you
deploy it anyway, work through this list first.

## Postgres

- **Version.** The test suite runs on **PostgreSQL 17**. The schema relies on the built-in
  `gen_random_uuid()`, `jsonb`, partial indexes, `FOR UPDATE SKIP LOCKED` and advisory locks, so
  **13 or newer** is the hard floor. Prefer the version the tests run on.
- **The `ripple` schema.** All tables, functions and FluentMigrator's version table (`ripple.version_info`)
  live in one schema, `ripple`, which the migration creates. It doesn't touch your other schemas.
- **Permissions.** The migration creates a schema, tables, indexes and PL/pgSQL functions, so the migrating
  role needs `CREATE` on the database. At runtime the engine reads and writes the `ripple` tables and calls
  its functions. Running both with the same role that owns the schema is simplest. If you split them, migrate
  as the owner and grant the runtime role DML on all `ripple` tables plus `EXECUTE` on its functions.
- **Fan-out reads your tables.** A Marten or EF fan-out is an `INSERT … SELECT` from **your** tables on the
  engine's connection, so that role needs `SELECT` on them. They must also be in the **same database** as the
  `ripple` schema.
- **Connections.** The engine opens its own pool (`RippleDataSource`) from `ConnectionString`, separate from
  your application's pools, on every instance. Budget `max_connections` across all instances and both pools,
  and set `Maximum Pool Size` in the connection string if you need a ceiling.

## Migrations

- **`AutoMigrate = true`** (the default) migrates on startup, before the engine or dashboard serves anything.
  It takes an advisory lock, so every replica can do it safely at once.
- To make migration a release step, set **`AutoMigrate = false`** and run it from a one-off process:

  ```csharp
  var builder = Host.CreateApplicationBuilder(args);
  builder.AddRippleEngine(o => { o.AutoMigrate = false; o.EnableWorkers = false; });
  using var host = builder.Build();
  host.Services.MigrateRipple();
  ```

- **Freeze the schema before keeping a database.** Pre-1.0, the schema is **one** authoritative migration
  that is edited in place, not an `ALTER` trail. A database migrated by an older build won't pick up later
  edits. Before running against a database you intend to keep, pin the version and add forward migrations
  for any change after that point.

## Hosts and roles

- **Creation-side hosts** (an API that only creates waves) use `EnableWorkers = false` plus the fan-out
  providers they need (`o.UseMartenFanOut()`, `o.UseEntityFrameworkFanOut()`).
- **Workers** register handlers with `AddHandler`. A worker only claims types it has a handler for, so you can
  dedicate worker pools to different types, and a rolling deploy that adds a handler doesn't burn other
  instances' attempts.
- **Payload compatibility across deploys.** Creators and workers deploy separately, so evolve payload types
  compatibly. Add properties rather than renaming them (property names are matched case-insensitively, see
  [the payload contract](/guides/fan-out#the-payload-json-contract)). Pin `[RippleType("…")]` before renaming a
  payload class, or ripples already queued lose their handler.

## The dashboard

- `EnableDashboard = true` exposes **unauthenticated write endpoints**. Map it behind authorization or keep
  it off untrusted networks. See [Dashboard → Mapping it yourself](/operations/dashboard#mapping-it-yourself).
- Enable it on one host, or a few. Every instance doesn't need it.

## Shutdown and deployment

On `SIGTERM` an instance stops claiming, lets in-flight ripples finish for `ShutdownDrainGrace` (default
15s), then cancels the rest and **releases** them: they are requeued at once with the attempt handed back, so
a deploy doesn't spend retries. Finally it **deregisters** its heartbeat so peers don't wait out
`HeartbeatTimeout`. It refuses to deregister while it still owns `Running` ripples. In that case the stale
heartbeat is what lets [recovery](/architecture/settlement-and-recovery) reclaim them.

- `HostOptions.ShutdownTimeout` is derived for you (`ShutdownDrainGrace` + `HandlerCancellationGrace` +
  `ShutdownTimeoutMargin`, so 30s by default). Make the orchestrator's grace period at least that, for
  example Kubernetes `terminationGracePeriodSeconds`, or the process is killed mid-drain. Ripples left
  `Running` are then recovered only after `HeartbeatTimeout`.
- Size `ShutdownDrainGrace` to your typical handler duration. A handler that outlives it is released and
  re-run elsewhere, which is safe only because handlers are idempotent.

## Correctness

- **Handlers must be idempotent.** Retries, recovery and shutdown release all re-run ripples.
- **Clocks must be NTP-synced.** Retry backoff uses the host clock and eligibility the database clock.
- **Marten stores need `EnumStorage.AsString`** for fan-out payloads.
- **Retention.** `DefaultRetention` is `null`, so finished waves' reports are kept **forever**. Set it, or
  `RetentionByWaveType`, so the `wave` and `report_chunk` tables stay bounded. A purge also frees the wave's
  idempotency key.

## Observability

Turn on `EnableMetrics` (and `EnableTracing` on creators and workers) and export via OTLP. Alert on at least:

- `ripple.queue.depth` and `ripple.queue.head_wait`, which show the cluster falling behind;
- the `ripple.failed` rate by `type_key`;
- waves that stay `Active` far longer than usual, often a paused type or a type with no worker running a
  handler for it.

See [Metrics & tracing](/operations/observability).
