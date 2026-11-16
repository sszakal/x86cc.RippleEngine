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
own endpoints. To place the API yourself — a different prefix, behind auth — leave the flag off and call
`MapRippleDashboard()`.

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
takes a long lock. Resume can *rebase* the resumed work to the current frontier or keep its original position.
