---
title: Dashboard
sidebar_position: 1
---

# Dashboard

`o.EnableDashboard = true` serves the Angular SPA at the host's root URL, same origin as its API (`/api`):

- waves by year / month / day / range, with timelines;
- per-type throughput metrics;
- live cluster membership;
- a wave's aggregated report as CSV;
- a settings page that edits each type's `batch_size` / `gap_seconds` / `max_attempts` (including the
  reserved `__default__` row) and pauses/resumes types.

Both ship inside the `x86cc.RippleEngine.Dashboard` package. The SPA is **embedded in the assembly**, so there
is no `wwwroot` to deploy. It's built as part of `dotnet build` when Node is on PATH (skipped with a warning
otherwise; disable with `-p:BuildSpa=false`). It is mapped as a route *fallback*, so it never shadows the host's
own endpoints.

:::danger No authentication by default
`EnableDashboard = true` maps the API and SPA **anonymously**. The API includes **writes**: pausing and
resuming types, rescheduling live waves, and editing or deleting type schedules (retry ceilings included).
Anyone who can reach the host can throttle or stall your jobs. Only enable the flag where the host isn't
reachable by untrusted clients, or map the endpoints yourself behind authorization (below).
:::

## Mapping it yourself

Leave `EnableDashboard` off and call the two mapping extensions where you want them:

| Extension | Maps |
|---|---|
| `MapRippleDashboard()` | the `/api` group: read projections and operator writes |
| `MapRippleDashboardSpa()` | the embedded Angular SPA as a lowest-precedence `{*path}` fallback |

Both return `IEndpointRouteBuilder`, so you can map them onto a route group with an authorization policy:

```csharp
var ops = app.MapGroup("").RequireAuthorization("RippleOperators");
ops.MapRippleDashboard();
ops.MapRippleDashboardSpa();
```

Constraints:

- The SPA is built with `<base href="/">` and calls `/api`, so it must be served from the **root**. You can
  map `MapRippleDashboard()` under a prefix (`app.MapGroup("/ops").MapRippleDashboard()` gives `/ops/api`) for
  your own tooling, but the bundled SPA won't find it there.
- The SPA is a catch-all fallback. An app that has its own SPA fallback should map only
  `MapRippleDashboard()`.
- The API needs the Ripple storage services, so the host must call `AddRippleEngine`. A host with
  `EnableWorkers = false` works too: `/api/settings/types` then lists only types that already have a schedule
  row, since that process has no handler registry of its own.

## API

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/waves` | list waves (filter by `status`, `q`, `from`, `to`) |
| `GET` | `/api/waves/activity` | per-day activity |
| `GET` | `/api/waves/histogram` | bucketed wave histogram |
| `GET` | `/api/waves/{id}` | one wave's numbers |
| `GET` | `/api/waves/{id}/report.csv` | the aggregated report |
| `POST` | `/api/waves/{id}/reschedule` | re-stamp a live wave's pending ripples with a new batch/gap |
| `GET` | `/api/metrics/types` | per-type throughput |
| `GET` | `/api/cluster` | live instances |
| `GET` | `/api/settings/types` | type configuration (+ registered handlers) |
| `PUT` / `DELETE` | `/api/settings/types/{typeKey}` | edit / remove a type's schedule |
| `POST` | `/api/settings/types/{typeKey}/pause` | pause a type |
| `POST` | `/api/settings/types/{typeKey}/resume?rebase=` | resume a type, optionally rebased to the frontier |
| `GET` | `/api/settings/engine` | effective engine options |

Writes are validated with the store's own rules, so a bad edit is a `400`, never a `500` or a silent no-op.

## Pause / resume

`PauseTypeAsync(typeKey)` flips a desired state and takes effect immediately — the claim skips the type at
once. The millions of ripples then move `Pending ⇄ Paused` in bounded background chunks
(`PauseReconcileChunkSize` per statement, `PauseReconcileMaxRowsPerPass` per tick), so pausing 10M ripples never
takes a long lock. Resume *rebases* the resumed work to the current frontier by default (`rebase=true`), so it
interleaves fairly; `rebase=false` keeps its original position, which puts it ahead of everything queued since.

While a type is paused, its waves stay `Active`: paused ripples count as not done. The same operations are
available from code. See [Programmatic control](/guides/programmatic-control).
